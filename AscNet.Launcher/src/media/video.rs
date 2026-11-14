use anyhow::{anyhow, bail, Context, Result};
use openh264::decoder::Decoder;
use openh264::formats::YUVSource;
use std::{
    fs::File,
    io::BufReader,
    path::Path,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

/// One decoded picture, tightly packed RGBA8.
pub struct Frame {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

/// Plays an H.264 mp4 in a loop on its own thread, calling `on_frame` at the file's timestamps.
/// OpenH264 cannot reorder frames, so streams with B-frames are rejected by `start`.
pub struct Video {
    stop: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

impl Video {
    pub fn start(path: &Path, on_frame: impl Fn(Frame) + Send + 'static) -> Result<Self> {
        let clip = Clip::open(path)?;
        let stop = Arc::new(AtomicBool::new(false));
        let paused = Arc::new(AtomicBool::new(false));
        let (s, p) = (stop.clone(), paused.clone());
        let worker = thread::Builder::new()
            .name("launcher-video".into())
            .spawn(move || {
                if let Err(error) = clip.run(&s, &p, on_frame) {
                    eprintln!("background video stopped: {error:#}");
                }
            })
            .context("could not start video decoder thread")?;
        Ok(Self { stop, paused, worker: Some(worker) })
    }

    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::Release);
        if let Some(worker) = &self.worker {
            worker.thread().unpark();
        }
    }
}

impl Drop for Video {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            worker.thread().unpark();
            let _ = worker.join();
        }
    }
}

struct Packet {
    annexb: Vec<u8>,
    due: Duration,
}

/// The whole stream in decode order, already converted from AVCC to Annex B (SPS/PPS ahead of the first sample).
struct Clip {
    packets: Vec<Packet>,
    length: Duration,
}

impl Clip {
    // ponytail: the whole mp4 (about 10 MB of packets) stays in RAM; stream from disk if clips grow.
    fn open(path: &Path) -> Result<Self> {
        let file = File::open(path).with_context(|| format!("could not open background video {}", path.display()))?;
        let size = file.metadata()?.len();
        let mut reader = mp4::Mp4Reader::read_header(BufReader::new(file), size)
            .with_context(|| format!("{} is not a readable mp4", path.display()))?;
        let track = reader
            .tracks()
            .values()
            .find(|t| t.track_type().ok() == Some(mp4::TrackType::Video))
            .context("background video has no video track")?;
        let avc1 = track.trak.mdia.minf.stbl.stsd.avc1.as_ref().context("background video is not H.264")?;
        if avc1.avcc.length_size_minus_one != 3 {
            bail!("background video uses {}-byte NAL lengths, expected 4", avc1.avcc.length_size_minus_one + 1);
        }
        let (id, timescale, count) = (track.track_id(), u64::from(track.timescale()), track.sample_count());
        let mut head = Vec::new();
        for ps in [track.sequence_parameter_set()?, track.picture_parameter_set()?] {
            head.extend_from_slice(&[0, 0, 0, 1]);
            head.extend_from_slice(ps);
        }
        if timescale == 0 || count == 0 {
            bail!("background video has no samples");
        }
        let us = |ticks: u64| Duration::from_micros(ticks * 1_000_000 / timescale);
        let (mut packets, mut times, mut end) = (Vec::with_capacity(count as usize), Vec::new(), 0);
        for sample in 1..=count {
            let s = reader.read_sample(id, sample)?.ok_or_else(|| anyhow!("background video sample {sample} is empty"))?;
            let mut annexb = if sample == 1 { head.clone() } else { Vec::new() };
            let b = &s.bytes[..];
            let mut i = 0;
            while i + 4 <= b.len() {
                let n = u32::from_be_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]) as usize;
                let nal = b.get(i + 4..i + 4 + n).ok_or_else(|| anyhow!("background video sample {sample} is truncated"))?;
                annexb.extend_from_slice(&[0, 0, 0, 1]);
                annexb.extend_from_slice(nal);
                i += 4 + n;
            }
            times.push((s.start_time, s.rendering_offset));
            end = s.start_time + u64::from(s.duration);
            packets.push(Packet { annexb, due: us(s.start_time) });
        }
        check_no_reordering(&times)?;
        Ok(Self { packets, length: us(end) })
    }

    /// Decodes one pass from a fresh decoder (the first sample is an IDR), handing each frame to `f` with its due
    /// time since the pass began. `f` returns false to stop early; the result says whether the pass completed.
    fn pass(&self, mut f: impl FnMut(Duration, Frame) -> bool) -> Result<bool> {
        let mut decoder = Decoder::new().map_err(|e| anyhow!("could not create H.264 decoder: {e}"))?;
        for (n, packet) in self.packets.iter().enumerate() {
            let yuv = decoder.decode(&packet.annexb).map_err(|e| anyhow!("H.264 decode failed at sample {}: {e}", n + 1))?;
            let Some(yuv) = yuv else { continue };
            let (w, h) = yuv.dimensions();
            let mut rgba = vec![0; w * h * 4];
            yuv.write_rgba8(&mut rgba);
            if !f(packet.due, Frame { width: w as u32, height: h as u32, rgba }) {
                return Ok(false);
            }
        }
        Ok(true)
    }

    fn run(&self, stop: &AtomicBool, paused: &AtomicBool, on_frame: impl Fn(Frame)) -> Result<()> {
        let slack = self.length / self.packets.len() as u32 * 2; // later than this behind: resync instead of bursting
        let mut base = Instant::now();
        loop {
            let finished = self.pass(|due, frame| {
                loop {
                    if stop.load(Ordering::Acquire) {
                        return false;
                    }
                    if paused.load(Ordering::Acquire) {
                        thread::park();
                        base = Instant::now() - due; // resume: this frame is due now
                        continue;
                    }
                    let now = Instant::now();
                    match (base + due).checked_duration_since(now) {
                        Some(wait) => thread::park_timeout(wait),
                        None => {
                            if now - (base + due) > slack {
                                base = now - due;
                            }
                            break;
                        }
                    }
                }
                on_frame(frame);
                true
            })?;
            if !finished {
                return Ok(());
            }
            base += self.length;
        }
    }
}

/// B-frames show out of decode order, which OpenH264 cannot reorder: presentation times must rise with decode order.
fn check_no_reordering(times: &[(u64, i32)]) -> Result<()> {
    let pts = |&(start, offset): &(u64, i32)| start as i64 + i64::from(offset);
    for (i, pair) in times.windows(2).enumerate() {
        if pts(&pair[1]) <= pts(&pair[0]) {
            bail!(
                "background video has B-frames (sample {} is shown before sample {}); OpenH264 cannot decode them. \
                 Re-encode without B-frames, e.g. x264 -bf 0 or AVVideoAllowFrameReorderingKey = false",
                i + 2,
                i + 1
            );
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    /// Launcher media with the no-B-frame encode: `ASCNET_LAUNCHER_MEDIA`, else `<repo>/.runtime/launcher-media`.
    /// Tests that need the real file skip (with a note) when it is absent, as `.runtime` is not in git.
    fn media(name: &str) -> Option<PathBuf> {
        let dir = std::env::var_os("ASCNET_LAUNCHER_MEDIA")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../.runtime/launcher-media"));
        let path = dir.join(name);
        if !path.is_file() {
            eprintln!("skipping: {} not found", path.display());
            return None;
        }
        Some(path)
    }

    #[test]
    fn decodes_real_background_and_loops_seamlessly() {
        let Some(path) = media("background.mp4") else { return };
        let clip = Clip::open(&path).unwrap();
        assert_eq!(clip.packets.len(), 200);
        assert!((clip.length.as_secs_f64() - 200.0 / 30.0).abs() < 1e-3);
        let (mut first, mut last, mut count) = (Vec::new(), Vec::new(), 0);
        let mut dues = Vec::new();
        assert!(clip.pass(|due, f| {
            assert_eq!((f.width, f.height, f.rgba.len()), (1920, 1080, 1920 * 1080 * 4));
            if count == 0 { first = f.rgba.clone() }
            last = f.rgba;
            dues.push(due);
            count += 1;
            true
        }).unwrap());
        assert_eq!(count, 200);
        assert!(dues.windows(2).all(|w| w[1] > w[0]));
        // Seam: a second pass restarts from the same first picture (new decoder at the IDR) ...
        let mut again = None;
        clip.pass(|_, f| { again = Some(f.rgba); false }).unwrap();
        assert!(again.unwrap() == first);
        // ... and the last picture leads into it as smoothly as neighbouring frames do (mean abs diff per byte).
        let diff = |a: &[u8], b: &[u8]| a.iter().zip(b).map(|(x, y)| x.abs_diff(*y) as u64).sum::<u64>() as f64 / a.len() as f64;
        let mut prev: Option<Vec<u8>> = None;
        let mut max_step = 0f64;
        clip.pass(|_, f| {
            if let Some(p) = &prev { max_step = max_step.max(diff(p, &f.rgba)) }
            prev = Some(f.rgba);
            true
        }).unwrap();
        assert!(diff(&last, &first) <= max_step * 2.0 + 1.0, "seam jump {} vs largest step {max_step}", diff(&last, &first));
    }

    #[test]
    fn rejects_b_frame_stream() {
        let Some(good) = media("background.mp4") else { return };
        // The pre-1.0.24 encodes (AVAssetWriter default) carry B-frames.
        let old = good.parent().unwrap().join("../launcher-package-1.0.23/background.mp4");
        if !old.is_file() {
            eprintln!("skipping: {} not found", old.display());
            return;
        }
        let error = Clip::open(&old).err().expect("B-frame stream must be rejected").to_string();
        assert!(error.contains("B-frames"), "{error}");
    }

    #[test]
    fn reordered_timestamps_are_rejected() {
        assert!(check_no_reordering(&[(0, 0), (1, 0), (2, 0)]).is_ok());
        assert!(check_no_reordering(&[(0, 2), (1, 5), (2, 0), (3, 1)]).unwrap_err().to_string().contains("B-frames"));
    }

    #[test]
    fn start_reports_missing_file() {
        assert!(Video::start(Path::new("/nonexistent/background.mp4"), |_| {}).is_err());
    }
}
