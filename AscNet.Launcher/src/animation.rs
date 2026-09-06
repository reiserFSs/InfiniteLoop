use anyhow::{anyhow, Context, Result};
use std::{
    fs::File,
    io::Read,
    path::Path,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows::{
    core::{Interface, GUID, HSTRING, PROPVARIANT, PSTR},
    Win32::{
        Foundation::BOOL,
        Media::{Audio::*, MediaFoundation::*},
        System::Com::{CoInitializeEx, CoUninitialize, COINIT_MULTITHREADED},
    },
};

const MAX_DIMENSION: u32 = 16_384;
const MAX_FRAME_BYTES: usize = 512 * 1024 * 1024;

pub struct Music {
    output: HWAVEOUT,
    intro: Box<WAVEHDR>,
    looped: Box<WAVEHDR>,
    // Kept after the headers so their PCM pointers remain valid through unprepare.
    _samples: Vec<u8>,
}

impl Music {
    pub fn start(path: &Path) -> Result<Self> {
        let wave = load_wave(path)?;
        let mut output = HWAVEOUT::default();
        if unsafe {
            waveOutOpen(
                Some(&mut output),
                WAVE_MAPPER,
                &wave.format,
                0,
                0,
                CALLBACK_NULL,
            )
        } != 0
        {
            anyhow::bail!("could not open default audio device");
        }

        let mut intro = Box::new(header(&wave.samples, 0, wave.loop_start, 0));
        let mut looped = Box::new(header(
            &wave.samples,
            wave.loop_start,
            wave.loop_end,
            WHDR_BEGINLOOP | WHDR_ENDLOOP,
        ));
        looped.dwLoops = u32::MAX;
        let mut intro_prepared = false;
        let mut looped_prepared = false;
        let result = unsafe {
            prepare(output, &mut intro).inspect(|_| intro_prepared = true)
                .and_then(|()| prepare(output, &mut looped).inspect(|_| looped_prepared = true))
                .and_then(|()| write(output, &mut intro))
                .and_then(|()| write(output, &mut looped))
        };
        if let Err(error) = result {
            unsafe { close_music(output, &mut intro, &mut looped, intro_prepared, looped_prepared) };
            return Err(error);
        }
        Ok(Self {
            output,
            intro,
            looped,
            _samples: wave.samples,
        })
    }

}

impl Drop for Music {
    fn drop(&mut self) {
        unsafe { close_music(self.output, &mut self.intro, &mut self.looped, true, true) }
    }
}

unsafe fn close_music(
    output: HWAVEOUT,
    intro: &mut WAVEHDR,
    looped: &mut WAVEHDR,
    intro_prepared: bool,
    looped_prepared: bool,
) {
    waveOutReset(output);
    if intro_prepared {
        waveOutUnprepareHeader(output, intro, std::mem::size_of::<WAVEHDR>() as u32);
    }
    if looped_prepared {
        waveOutUnprepareHeader(output, looped, std::mem::size_of::<WAVEHDR>() as u32);
    }
    waveOutClose(output);
}

struct Wave {
    format: WAVEFORMATEX,
    samples: Vec<u8>,
    loop_start: usize,
    loop_end: usize,
}

fn load_wave(path: &Path) -> Result<Wave> {
    let mut file = File::open(path).with_context(|| format!("could not open background music {}", path.display()))?;
    let mut header = [0; 12];
    file.read_exact(&mut header).context("background music is not a RIFF/WAVE file")?;
    if &header[..4] != b"RIFF" || &header[8..] != b"WAVE" {
        anyhow::bail!("background music must be a RIFF/WAVE file");
    }
    let mut remaining = u32::from_le_bytes(header[4..8].try_into().unwrap()) as usize;
    if remaining < 4 {
        anyhow::bail!("background music has an invalid RIFF length");
    }
    remaining -= 4; // WAVE type
    let mut format = None;
    let mut loop_range = None;
    let mut samples = None;
    while remaining != 0 {
        if remaining < 8 {
            anyhow::bail!("background music has a truncated RIFF chunk");
        }
        let mut chunk = [0; 8];
        file.read_exact(&mut chunk)?;
        remaining -= 8;
        let size = u32::from_le_bytes(chunk[4..].try_into().unwrap()) as usize;
        let padded_size = size.checked_add(size & 1).context("background music chunk length overflows")?;
        if padded_size > remaining {
            anyhow::bail!("background music RIFF chunk exceeds its container");
        }
        match &chunk[..4] {
            b"fmt " if size == 16 => {
                let mut data = [0; 16];
                file.read_exact(&mut data)?;
                format = Some(WAVEFORMATEX {
                    wFormatTag: u16::from_le_bytes(data[0..2].try_into().unwrap()),
                    nChannels: u16::from_le_bytes(data[2..4].try_into().unwrap()),
                    nSamplesPerSec: u32::from_le_bytes(data[4..8].try_into().unwrap()),
                    nAvgBytesPerSec: u32::from_le_bytes(data[8..12].try_into().unwrap()),
                    nBlockAlign: u16::from_le_bytes(data[12..14].try_into().unwrap()),
                    wBitsPerSample: u16::from_le_bytes(data[14..16].try_into().unwrap()),
                    cbSize: 0,
                });
            }
            b"smpl" if size >= 60 => {
                let mut data = vec![0; size];
                file.read_exact(&mut data)?;
                if u32::from_le_bytes(data[28..32].try_into().unwrap()) != 1
                    || u32::from_le_bytes(data[40..44].try_into().unwrap()) != 0 {
                    anyhow::bail!("background music must have one forward loop");
                }
                let start = u32::from_le_bytes(data[44..48].try_into().unwrap()) as usize;
                let end = u32::from_le_bytes(data[48..52].try_into().unwrap()) as usize;
                loop_range = Some((start, end.checked_add(1).context("background music loop end overflows")?));
            }
            b"data" => {
                let mut data = vec![0; size];
                file.read_exact(&mut data)?;
                samples = Some(data);
            }
            _ => {
                if std::io::copy(&mut file.by_ref().take(size as u64), &mut std::io::sink())? != size as u64 {
                    anyhow::bail!("background music has a truncated RIFF chunk");
                }
            }
        }
        if size & 1 != 0 {
            let mut padding = [0];
            file.read_exact(&mut padding)?;
        }
        remaining -= padded_size;
    }
    let format = format.context("background music is missing a PCM format")?;
    if format.wFormatTag != 1
        || format.wBitsPerSample != 16
        || format.nChannels != 2
        || format.nSamplesPerSec != 48_000
        || format.nBlockAlign != 4
        || format.nAvgBytesPerSec != 192_000
    {
        anyhow::bail!("background music must be 48 kHz PCM16 stereo");
    }
    let samples = samples.context("background music is missing PCM data")?;
    if samples.len() % format.nBlockAlign as usize != 0 {
        anyhow::bail!("background music PCM data is not frame-aligned");
    }
    let (start, end) = loop_range.context("background music is missing RIFF smpl loop metadata")?;
    let loop_start = start.checked_mul(format.nBlockAlign as usize).context("background music loop offset overflows")?;
    let loop_end = end.checked_mul(format.nBlockAlign as usize).context("background music loop offset overflows")?;
    if loop_start >= loop_end || loop_end > samples.len() {
        anyhow::bail!("background music loop is outside PCM data");
    }
    Ok(Wave { format, samples, loop_start, loop_end })
}

fn header(samples: &[u8], start: usize, end: usize, flags: u32) -> WAVEHDR {
    WAVEHDR {
        lpData: PSTR(unsafe { samples.as_ptr().add(start) as *mut u8 }),
        dwBufferLength: (end - start) as u32,
        dwFlags: flags,
        ..Default::default()
    }
}

unsafe fn prepare(output: HWAVEOUT, header: &mut WAVEHDR) -> Result<()> {
    if waveOutPrepareHeader(output, header, std::mem::size_of::<WAVEHDR>() as u32) != 0 {
        anyhow::bail!("could not prepare background music buffer");
    }
    Ok(())
}

unsafe fn write(output: HWAVEOUT, header: &mut WAVEHDR) -> Result<()> {
    if waveOutWrite(output, header, std::mem::size_of::<WAVEHDR>() as u32) != 0 {
        anyhow::bail!("could not queue background music buffer");
    }
    Ok(())
}
struct Frame {
    sequence: u64,
    width: i32,
    height: i32,
    pixels: Vec<u8>,
    error: Option<String>,
}

pub struct Animation {
    shared: Arc<Mutex<Frame>>,
    stop: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

impl Animation {
    pub fn start(path: &Path) -> Result<Self> {
        let path = path.to_owned();
        let shared = Arc::new(Mutex::new(Frame {
            sequence: 0,
            width: 0,
            height: 0,
            pixels: Vec::new(),
            error: None,
        }));
        let stop = Arc::new(AtomicBool::new(false));
        let paused = Arc::new(AtomicBool::new(false));
        let worker_shared = Arc::clone(&shared);
        let worker_stop = Arc::clone(&stop);
        let worker_paused = Arc::clone(&paused);
        let worker = thread::Builder::new()
            .name("launcher-video".into())
            .spawn(move || {
                if let Err(error) = decode(&path, &worker_shared, &worker_stop, &worker_paused) {
                    worker_shared
                        .lock()
                        .unwrap_or_else(|poisoned| poisoned.into_inner())
                        .error = Some(format!("{error:#}"));
                }
            })
            .context("could not start video decoder thread")?;
        Ok(Self {
            shared,
            stop,
            paused,
            worker: Some(worker),
        })
    }

    pub fn with_frame(
        &self,
        after: u64,
        consume: impl FnOnce(u64, i32, i32, &[u8]),
    ) -> Result<bool> {
        let frame = self
            .shared
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if let Some(error) = &frame.error {
            return Err(anyhow!(error.clone()));
        }
        if frame.sequence <= after {
            return Ok(false);
        }
        consume(frame.sequence, frame.width, frame.height, &frame.pixels);
        Ok(true)
    }

    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::Release);
        if let Some(worker) = &self.worker {
            worker.thread().unpark();
        }
    }
}

impl Drop for Animation {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            worker.thread().unpark();
            // ReadSample is synchronous and cannot be cancelled safely. Detaching keeps Drop bounded;
            // the worker owns its COM/MF objects and releases them when that read returns.
            drop(worker);
        }
    }
}

fn decode(
    path: &Path,
    shared: &Mutex<Frame>,
    stop: &AtomicBool,
    paused: &AtomicBool,
) -> Result<()> {
    unsafe {
        CoInitializeEx(None, COINIT_MULTITHREADED)
            .ok()
            .context("could not initialize COM for video")?;
        let result = decode_mf(path, shared, stop, paused);
        CoUninitialize();
        result
    }
}

unsafe fn decode_mf(
    path: &Path,
    shared: &Mutex<Frame>,
    stop: &AtomicBool,
    paused: &AtomicBool,
) -> Result<()> {
    MFStartup(MF_VERSION, MFSTARTUP_FULL).context("could not start Media Foundation")?;
    let result = run_reader(path, shared, stop, paused);
    MFShutdown().context("could not shut down Media Foundation")?;
    result
}

unsafe fn run_reader(
    path: &Path,
    shared: &Mutex<Frame>,
    stop: &AtomicBool,
    paused: &AtomicBool,
) -> Result<()> {
    let mut attributes = None;
    MFCreateAttributes(&mut attributes, 1).context("could not create video reader attributes")?;
    let attributes = attributes.context("Media Foundation returned no video reader attributes")?;
    attributes
        .SetUINT32(&MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1)
        .context("could not enable Media Foundation video processing")?;

    let url = HSTRING::from(path.as_os_str());
    let reader = MFCreateSourceReaderFromURL(&url, Some(&attributes))
        .with_context(|| format!("could not open background video {}", path.display()))?;
    reader
        .SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS.0 as u32, BOOL(0))
        .context("could not disable video source streams")?;
    let stream = MF_SOURCE_READER_FIRST_VIDEO_STREAM.0 as u32;
    reader
        .SetStreamSelection(stream, BOOL(1))
        .context("background video has no selectable video stream")?;

    let requested = MFCreateMediaType().context("could not create RGB32 video type")?;
    requested.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
    requested.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_RGB32)?;
    reader
        .SetCurrentMediaType(stream, None, &requested)
        .context("background video cannot be decoded as RGB32")?;

    let mut format = video_format(&reader, stream)?;
    let duration = i64::try_from(
        &reader
            .GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE.0 as u32, &MF_PD_DURATION)
            .context("background video has no duration")?,
    )
    .context("background video duration is invalid")?;
    if duration <= 0 {
        anyhow::bail!("background video duration must be positive");
    }
    let mut decoded = Vec::new();
    let mut sequence = 0u64;
    let mut loop_start = Instant::now();
    let mut first_timestamp = None;

    while !stop.load(Ordering::Acquire) {
        loop_start += wait_while_paused(paused, stop);
        if stop.load(Ordering::Acquire) {
            break;
        }
        let mut flags = 0u32;
        let mut timestamp = 0i64;
        let mut sample = None;
        reader
            .ReadSample(
                stream,
                0,
                None,
                Some(&mut flags),
                Some(&mut timestamp),
                Some(&mut sample),
            )
            .context("could not decode background video frame")?;

        if flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED.0 as u32 != 0 {
            format = video_format(&reader, stream)?;
            decoded.clear();
        }
        if flags & MF_SOURCE_READERF_ENDOFSTREAM.0 as u32 != 0 {
            wait_until(loop_start + media_duration(duration), paused, stop);
            if stop.load(Ordering::Acquire) {
                break;
            }
            reader
                .SetCurrentPosition(&GUID::zeroed(), &PROPVARIANT::from(0i64))
                .context("could not loop background video")?;
            loop_start = Instant::now();
            first_timestamp = None;
            continue;
        }
        let Some(sample) = sample else { continue };
        let origin = *first_timestamp.get_or_insert(timestamp);
        let due = loop_start + media_duration(timestamp.saturating_sub(origin));
        loop_start += wait_until(due, paused, stop);
        if stop.load(Ordering::Acquire) {
            break;
        }

        let buffer = sample
            .ConvertToContiguousBuffer()
            .context("could not access decoded video frame")?;
        copy_rgb32(&buffer, format, &mut decoded)?;
        sequence = sequence
            .checked_add(1)
            .ok_or_else(|| anyhow!("video frame sequence overflow"))?;
        let mut frame = shared
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        std::mem::swap(&mut frame.pixels, &mut decoded);
        frame.width = format.0 as i32;
        frame.height = format.1 as i32;
        frame.sequence = sequence;
    }
    Ok(())
}

fn media_duration(units_100ns: i64) -> Duration {
    Duration::from_nanos((units_100ns.max(0) as u64).saturating_mul(100))
}

fn wait_until(mut due: Instant, paused: &AtomicBool, stop: &AtomicBool) -> Duration {
    let mut paused_for = Duration::ZERO;
    while !stop.load(Ordering::Acquire) {
        let delay = wait_while_paused(paused, stop);
        due += delay;
        paused_for += delay;
        let now = Instant::now();
        if now >= due {
            break;
        }
        thread::park_timeout((due - now).min(Duration::from_millis(20)));
    }
    paused_for
}

fn wait_while_paused(paused: &AtomicBool, stop: &AtomicBool) -> Duration {
    if !paused.load(Ordering::Acquire) {
        return Duration::ZERO;
    }
    let began = Instant::now();
    while paused.load(Ordering::Acquire) && !stop.load(Ordering::Acquire) {
        thread::park_timeout(Duration::from_millis(20));
    }
    began.elapsed()
}

unsafe fn video_format(reader: &IMFSourceReader, stream: u32) -> Result<(u32, u32, i32)> {
    let media_type = reader
        .GetCurrentMediaType(stream)
        .context("could not read video format")?;
    let subtype = media_type
        .GetGUID(&MF_MT_SUBTYPE)
        .context("video has no pixel subtype")?;
    if subtype != MFVideoFormat_RGB32 {
        anyhow::bail!("video decoder returned an unexpected pixel subtype");
    }
    let packed = media_type
        .GetUINT64(&MF_MT_FRAME_SIZE)
        .context("video has no frame dimensions")?;
    let width = (packed >> 32) as u32;
    let height = packed as u32;
    let bytes = (width as usize)
        .checked_mul(height as usize)
        .and_then(|pixels| pixels.checked_mul(4))
        .filter(|&bytes| bytes <= MAX_FRAME_BYTES)
        .ok_or_else(|| anyhow!("video frame allocation is too large: {width}x{height}"))?;
    if width == 0 || height == 0 || width > MAX_DIMENSION || height > MAX_DIMENSION || bytes == 0 {
        anyhow::bail!("invalid video frame dimensions: {width}x{height}");
    }
    // Some decoders retain the encoded YUV stride on the converted media type. Lock2D's
    // per-buffer pitch is authoritative; this value is used only by the raw-buffer fallback.
    let stride = media_type
        .GetUINT32(&MF_MT_DEFAULT_STRIDE)
        .map(|value| value as i32)
        .unwrap_or((width * 4) as i32);
    Ok((width, height, stride))
}

unsafe fn copy_rgb32(
    buffer: &IMFMediaBuffer,
    format: (u32, u32, i32),
    output: &mut Vec<u8>,
) -> Result<()> {
    let (width, height, default_stride) = format;
    let row_bytes = width as usize * 4;
    let frame_bytes = row_bytes * height as usize;
    output.resize(frame_bytes, 0);

    if let Ok(buffer_2d) = buffer.cast::<IMF2DBuffer>() {
        let mut scanline = std::ptr::null_mut();
        let mut pitch = 0i32;
        buffer_2d
            .Lock2D(&mut scanline, &mut pitch)
            .context("could not lock decoded video frame")?;
        let result = copy_rows(scanline, pitch, width, height, output);
        let unlock = buffer_2d
            .Unlock2D()
            .context("could not unlock decoded video frame");
        result.and(unlock)
    } else {
        let mut data = std::ptr::null_mut();
        let mut maximum = 0u32;
        let mut current = 0u32;
        buffer
            .Lock(&mut data, Some(&mut maximum), Some(&mut current))
            .context("could not lock decoded video buffer")?;
        let declared_stride = default_stride.unsigned_abs() as usize;
        let current = current as usize;
        let pitch = if declared_stride >= row_bytes
            && declared_stride
                .checked_mul(height as usize)
                .is_some_and(|needed| needed <= current)
        {
            default_stride
        } else if frame_bytes == current {
            row_bytes as i32
        } else {
            0
        };
        let result = if pitch == 0 {
            Err(anyhow!(
                "decoded RGB32 buffer has invalid layout: {current} bytes, stride {default_stride}, frame {width}x{height}"
            ))
        } else {
            let scanline = if pitch < 0 {
                data.add(pitch.unsigned_abs() as usize * (height as usize - 1))
            } else {
                data
            };
            copy_rows(scanline, pitch, width, height, output)
        };
        let unlock = buffer
            .Unlock()
            .context("could not unlock decoded video buffer");
        result.and(unlock)
    }
}

unsafe fn copy_rows(
    source: *mut u8,
    pitch: i32,
    width: u32,
    height: u32,
    output: &mut [u8],
) -> Result<()> {
    let row_bytes = width as usize * 4;
    let stride = pitch.unsigned_abs() as usize;
    if source.is_null() || stride < row_bytes {
        anyhow::bail!("invalid decoded video stride: {pitch} for {width} pixels");
    }
    for y in 0..height as usize {
        std::ptr::copy_nonoverlapping(
            source.offset(y as isize * pitch as isize),
            output.as_mut_ptr().add(y * row_bytes),
            row_bytes,
        );
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn memory_buffer_ignores_stale_stride_and_normalizes_negative_pitch() {
        unsafe {
            MFStartup(MF_VERSION, MFSTARTUP_FULL).unwrap();
            let buffer = MFCreateMemoryBuffer(16).unwrap();
            let pixels = [
                1, 2, 3, 4, 5, 6, 7, 8, // first physical row
                9, 10, 11, 12, 13, 14, 15, 16, // second physical row
            ];
            let mut data = std::ptr::null_mut();
            buffer.Lock(&mut data, None, None).unwrap();
            std::ptr::copy_nonoverlapping(pixels.as_ptr(), data, pixels.len());
            buffer.Unlock().unwrap();
            buffer.SetCurrentLength(pixels.len() as u32).unwrap();

            let mut packed = Vec::new();
            copy_rgb32(&buffer, (2, 2, 2), &mut packed).unwrap();
            let mut negative = Vec::new();
            copy_rgb32(&buffer, (2, 2, -8), &mut negative).unwrap();
            MFShutdown().unwrap();

            assert_eq!(packed, pixels);
            assert_eq!(negative, [&pixels[8..], &pixels[..8]].concat());
        }
    }
    #[test]
    fn wave_data_before_smpl_keeps_pcm_and_inclusive_loop_end() {
        let mut body = b"WAVE".to_vec();
        let mut chunk = |name: &[u8; 4], data: &[u8]| {
            body.extend_from_slice(name);
            body.extend_from_slice(&(data.len() as u32).to_le_bytes());
            body.extend_from_slice(data);
            if data.len() & 1 != 0 {
                body.push(0);
            }
        };
        let mut format = Vec::new();
        format.extend_from_slice(&1u16.to_le_bytes());
        format.extend_from_slice(&2u16.to_le_bytes());
        format.extend_from_slice(&48_000u32.to_le_bytes());
        format.extend_from_slice(&192_000u32.to_le_bytes());
        format.extend_from_slice(&4u16.to_le_bytes());
        format.extend_from_slice(&16u16.to_le_bytes());
        chunk(b"fmt ", &format);
        chunk(b"data", &[0; 16]);
        let mut smpl = [0; 60];
        smpl[28..32].copy_from_slice(&1u32.to_le_bytes());
        smpl[40..44].copy_from_slice(&0u32.to_le_bytes());
        smpl[44..48].copy_from_slice(&1u32.to_le_bytes());
        smpl[48..52].copy_from_slice(&2u32.to_le_bytes());
        chunk(b"smpl", &smpl);
        let mut wave = b"RIFF".to_vec();
        wave.extend_from_slice(&(body.len() as u32).to_le_bytes());
        wave.extend_from_slice(&body);
        let path = std::env::temp_dir().join(format!(
            "ascnet-wave-{}-{}.wav",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        std::fs::write(&path, wave).unwrap();

        let parsed = load_wave(&path).unwrap();
        std::fs::remove_file(path).unwrap();
        let header = Box::new(header(&parsed.samples, parsed.loop_start, parsed.loop_end, 0));
        let expected = unsafe { parsed.samples.as_ptr().add(parsed.loop_start) as *mut u8 };
        let pointer = header.lpData.0;
        let length = header.dwBufferLength;
        assert_eq!(pointer, expected);
        assert_eq!(length, 8);
        assert_eq!(parsed.samples.len(), 16);
        assert_eq!((parsed.loop_start, parsed.loop_end), (4, 12));
    }

}
