use anyhow::{bail, Context, Result};
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{FromSample, SampleFormat, SizedSample};
use std::{
    path::Path,
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc, Arc,
    },
    thread::{self, JoinHandle},
};

/// Background music: plays the WAV once up to its `smpl` loop end, then repeats the loop region forever
/// (intro, then loop). All cpal objects live on one worker thread, so `Music` can be held anywhere.
pub struct Music {
    stop: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
    muted: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

impl Music {
    pub fn start(path: &Path) -> Result<Self> {
        let wave = Arc::new(load_wave(path)?);
        let (stop, paused, muted) = (Arc::new(AtomicBool::new(false)), Arc::new(AtomicBool::new(false)), Arc::new(AtomicBool::new(false)));
        let flags = (stop.clone(), paused.clone(), muted.clone());
        let (ready_tx, ready_rx) = mpsc::channel();
        let worker = thread::Builder::new()
            .name("launcher-music".into())
            .spawn(move || {
                let (stop, paused, muted) = flags;
                match open_stream(wave, paused, muted) {
                    Ok(stream) => {
                        let _ = ready_tx.send(Ok(()));
                        while !stop.load(Ordering::Acquire) {
                            thread::park();
                        }
                        drop(stream);
                    }
                    Err(error) => {
                        let _ = ready_tx.send(Err(error));
                    }
                }
            })
            .context("could not start music thread")?;
        let ready = ready_rx.recv().unwrap_or_else(|_| Err(anyhow::anyhow!("music thread exited")));
        if let Err(error) = ready {
            let _ = worker.join();
            return Err(error);
        }
        Ok(Self { stop, paused, muted, worker: Some(worker) })
    }

    /// Holds playback position; `set_paused(false)` continues where it stopped.
    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::Release);
    }

    /// Silences output while the position keeps advancing.
    pub fn set_muted(&self, muted: bool) {
        self.muted.store(muted, Ordering::Release);
    }
}

impl Drop for Music {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            worker.thread().unpark();
            let _ = worker.join();
        }
    }
}

/// 48 kHz PCM16 stereo, interleaved; loop bounds in frames, `loop_end` exclusive.
#[derive(Debug)]
struct Wave {
    samples: Vec<i16>,
    loop_start: usize,
    loop_end: usize,
}

const RATE: f64 = 48_000.0;

fn load_wave(path: &Path) -> Result<Wave> {
    let bytes = std::fs::read(path).with_context(|| format!("could not open background music {}", path.display()))?;
    parse_wave(&bytes)
}

fn u16_at(b: &[u8], i: usize) -> u16 {
    u16::from_le_bytes([b[i], b[i + 1]])
}

fn u32_at(b: &[u8], i: usize) -> u32 {
    u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}

fn parse_wave(bytes: &[u8]) -> Result<Wave> {
    if bytes.len() < 12 || &bytes[..4] != b"RIFF" || &bytes[8..12] != b"WAVE" {
        bail!("background music must be a RIFF/WAVE file");
    }
    let end = (u32_at(bytes, 4) as usize).checked_add(8).context("background music has an invalid RIFF length")?;
    if end < 12 || end > bytes.len() {
        bail!("background music has an invalid RIFF length");
    }
    let (mut at, mut format_ok, mut loop_range, mut data) = (12, None, None, None);
    while at < end {
        if end - at < 8 {
            bail!("background music has a truncated RIFF chunk");
        }
        let size = u32_at(bytes, at + 4) as usize;
        let body = at + 8;
        let next = body.checked_add(size + (size & 1)).filter(|&n| n <= end).context("background music RIFF chunk exceeds its container")?;
        match &bytes[at..at + 4] {
            b"fmt " if size == 16 => {
                // tag 1 = PCM, 2 channels, 48 kHz, 4 bytes per frame, 16 bits
                format_ok = Some(
                    u16_at(bytes, body) == 1
                        && u16_at(bytes, body + 2) == 2
                        && u32_at(bytes, body + 4) == 48_000
                        && u32_at(bytes, body + 8) == 192_000
                        && u16_at(bytes, body + 12) == 4
                        && u16_at(bytes, body + 14) == 16,
                );
            }
            b"smpl" if size >= 60 => {
                // 9 header words, then loops: cue id, type, start, end (inclusive), fraction, play count
                if u32_at(bytes, body + 28) != 1 || u32_at(bytes, body + 40) != 0 {
                    bail!("background music must have one forward loop");
                }
                let (start, last) = (u32_at(bytes, body + 44) as usize, u32_at(bytes, body + 48) as usize);
                loop_range = Some((start, last.checked_add(1).context("background music loop end overflows")?));
            }
            b"data" => data = Some(&bytes[body..body + size]),
            _ => {}
        }
        at = next;
    }
    match format_ok {
        Some(true) => {}
        Some(false) => bail!("background music must be 48 kHz PCM16 stereo"),
        None => bail!("background music is missing a PCM format"),
    }
    let data = data.context("background music is missing PCM data")?;
    if data.len() % 4 != 0 {
        bail!("background music PCM data is not frame-aligned");
    }
    let (loop_start, loop_end) = loop_range.context("background music is missing RIFF smpl loop metadata")?;
    if loop_start >= loop_end || loop_end > data.len() / 4 {
        bail!("background music loop is outside PCM data");
    }
    let samples = data.chunks_exact(2).map(|s| i16::from_le_bytes([s[0], s[1]])).collect();
    Ok(Wave { samples, loop_start, loop_end })
}

impl Wave {
    /// Source frame position after `pos`, wrapping the loop region.
    fn wrap(&self, pos: f64) -> f64 {
        let len = (self.loop_end - self.loop_start) as f64;
        if pos >= self.loop_end as f64 { pos - len } else { pos }
    }

    /// Linear-interpolated stereo frame at `pos` (frames), as f32 in [-1, 1].
    fn at(&self, pos: f64) -> [f32; 2] {
        let i = pos as usize;
        let j = if i + 1 >= self.loop_end { self.loop_start } else { i + 1 };
        let t = (pos - i as f64) as f32;
        let s = |frame: usize, ch: usize| f32::from(self.samples[frame * 2 + ch]) / 32768.0;
        [0, 1].map(|ch| s(i, ch) + (s(j, ch) - s(i, ch)) * t)
    }
}

fn open_stream(wave: Arc<Wave>, paused: Arc<AtomicBool>, muted: Arc<AtomicBool>) -> Result<cpal::Stream> {
    let device = cpal::default_host().default_output_device().context("no audio output device")?;
    let supported = device.default_output_config().context("could not query the audio output format")?;
    let config = supported.config();
    let format = supported.sample_format();
    let build = match format {
        SampleFormat::F32 => build::<f32>,
        SampleFormat::I16 => build::<i16>,
        SampleFormat::U16 => build::<u16>,
        SampleFormat::I32 => build::<i32>,
        other => bail!("unsupported audio output sample format {other:?}"),
    };
    let stream = build(&device, &config, wave, paused, muted)?;
    stream.play().context("could not start audio output")?;
    Ok(stream)
}

fn build<T: SizedSample + FromSample<f32>>(
    device: &cpal::Device,
    config: &cpal::StreamConfig,
    wave: Arc<Wave>,
    paused: Arc<AtomicBool>,
    muted: Arc<AtomicBool>,
) -> Result<cpal::Stream> {
    let channels = usize::from(config.channels);
    let step = RATE / f64::from(config.sample_rate.0);
    let mut pos = 0.0f64;
    device
        .build_output_stream(
            config,
            move |out: &mut [T], _| {
                let silent = paused.load(Ordering::Relaxed);
                let quiet = silent || muted.load(Ordering::Relaxed);
                for frame in out.chunks_mut(channels) {
                    let [l, r] = if silent { [0.0, 0.0] } else { wave.at(pos) };
                    if !silent {
                        pos = wave.wrap(pos + step);
                    }
                    let (l, r) = if quiet { (0.0, 0.0) } else { (l, r) };
                    for (ch, sample) in frame.iter_mut().enumerate() {
                        *sample = T::from_sample(match (channels, ch) {
                            (1, _) => (l + r) / 2.0,
                            (_, 0) => l,
                            (_, 1) => r,
                            _ => 0.0,
                        });
                    }
                }
            },
            |error| eprintln!("background music stream error: {error}"),
            None,
        )
        .context("could not open audio output")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn wav(frames: usize, loop_range: Option<(u32, u32)>, rate: u32) -> Vec<u8> {
        let mut chunks = Vec::new();
        let mut chunk = |id: &[u8], body: Vec<u8>| {
            chunks.extend_from_slice(id);
            chunks.extend_from_slice(&(body.len() as u32).to_le_bytes());
            chunks.extend_from_slice(&body);
        };
        let fmt = [1u16.to_le_bytes().to_vec(), 2u16.to_le_bytes().to_vec(), rate.to_le_bytes().to_vec(),
                   (rate * 4).to_le_bytes().to_vec(), 4u16.to_le_bytes().to_vec(), 16u16.to_le_bytes().to_vec()].concat();
        chunk(b"fmt ", fmt);
        if let Some((start, end)) = loop_range {
            let mut smpl = vec![0u8; 60];
            smpl[28..32].copy_from_slice(&1u32.to_le_bytes());
            smpl[44..48].copy_from_slice(&start.to_le_bytes());
            smpl[48..52].copy_from_slice(&end.to_le_bytes());
            chunk(b"smpl", smpl);
        }
        chunk(b"data", (0..frames * 2).flat_map(|i| (i as i16).to_le_bytes()).collect());
        let mut out = b"RIFF".to_vec();
        out.extend_from_slice(&(4 + chunks.len() as u32).to_le_bytes());
        out.extend_from_slice(b"WAVE");
        out.extend(chunks);
        out
    }

    #[test]
    fn parses_smpl_loop_points() {
        let w = parse_wave(&wav(100, Some((10, 99)), 48_000)).unwrap();
        assert_eq!((w.loop_start, w.loop_end, w.samples.len()), (10, 100, 200)); // smpl end is inclusive
    }

    #[test]
    fn plays_intro_then_loops_without_a_seam() {
        let w = parse_wave(&wav(100, Some((10, 59)), 48_000)).unwrap();
        let (mut pos, mut seen) = (0.0, Vec::new());
        for _ in 0..120 {
            seen.push(w.at(pos)[0] * 32768.0);
            pos = w.wrap(pos + 1.0);
        }
        assert_eq!(seen[9] as i32, 18); // intro frame 9 (left = 2 * frame)
        assert_eq!(seen[59] as i32, 118); // last loop frame
        assert_eq!(seen[60] as i32, 20); // back at loop start (frame 10), never past loop end
    }

    #[test]
    fn rejects_bad_files() {
        assert!(parse_wave(&wav(100, None, 48_000)).unwrap_err().to_string().contains("smpl"));
        assert!(parse_wave(&wav(100, Some((10, 99)), 44_100)).unwrap_err().to_string().contains("48 kHz"));
        assert!(parse_wave(&wav(100, Some((10, 100)), 48_000)).unwrap_err().to_string().contains("outside"));
        assert!(parse_wave(b"RIFX0000WAVE").is_err());
    }

    #[test]
    fn real_background_loop_points() {
        let dir = std::env::var_os("ASCNET_LAUNCHER_MEDIA")
            .map(std::path::PathBuf::from)
            .unwrap_or_else(|| std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../.runtime/launcher-media"));
        let Ok(bytes) = std::fs::read(dir.join("background.wav")) else {
            eprintln!("skipping: background.wav not found in {}", dir.display());
            return;
        };
        let w = parse_wave(&bytes).unwrap();
        assert_eq!((w.loop_start, w.loop_end, w.samples.len() / 2), (480_000, 4_512_000, 4_512_000));
    }
}
