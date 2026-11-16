use openh264::decoder::Decoder;
use openh264::formats::YUVSource;
use slint::{ComponentHandle, Image, Rgba8Pixel, SharedPixelBuffer};
use slint::winit_030::WinitWindowAccessor;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

slint::include_modules!();

struct Frame(SharedPixelBuffer<Rgba8Pixel>);

// ponytail: loads the whole mp4 into RAM (10 MB); real launcher would stream from disk.
fn video_loop(path: String, ui: slint::Weak<App>) -> Result<(), Box<dyn std::error::Error>> {
    let data = std::fs::read(path)?;
    let size = data.len() as u64;
    let mut rd = mp4::Mp4Reader::read_header(std::io::Cursor::new(data), size)?;
    let (tid, sps, pps) = {
        let t = rd.tracks().values().find(|t| t.track_type().ok() == Some(mp4::TrackType::Video)).ok_or("no video")?;
        (t.track_id(), t.sequence_parameter_set()?.to_vec(), t.picture_parameter_set()?.to_vec())
    };
    let count = rd.sample_count(tid)?;
    let pending = Arc::new(AtomicBool::new(false));
    let (mut shown, mut t0, mut dec_total) = (0u32, Instant::now(), Duration::ZERO);
    let frame_dt = Duration::from_micros(33_333);
    loop {
        let mut dec = Decoder::new()?;
        let mut next = Instant::now();
        for id in 1..=count {
            let s = rd.read_sample(tid, id)?.ok_or("empty sample")?;
            let mut annexb = Vec::with_capacity(s.bytes.len() + 64);
            if id == 1 {
                for ps in [&sps, &pps] { annexb.extend_from_slice(&[0, 0, 0, 1]); annexb.extend_from_slice(ps); }
            }
            let (mut i, b) = (0, &s.bytes[..]);
            while i + 4 <= b.len() { // AVCC 4-byte length prefixes -> Annex B
                let n = u32::from_be_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]) as usize;
                annexb.extend_from_slice(&[0, 0, 0, 1]);
                annexb.extend_from_slice(&b[i + 4..i + 4 + n]);
                i += 4 + n;
            }
            let t = Instant::now();
            let r = dec.decode(&annexb).map_err(|e| eprintln!("sample {id}: {e}")).ok().flatten();
            if let Some(yuv) = r {
                let (w, h) = yuv.dimensions();
                let mut buf = SharedPixelBuffer::<Rgba8Pixel>::new(w as u32, h as u32);
                yuv.write_rgba8(buf.make_mut_bytes());
                dec_total += t.elapsed();
                next += frame_dt;
                if let Some(d) = next.checked_duration_since(Instant::now()) { std::thread::sleep(d); }
                if !pending.swap(true, Ordering::AcqRel) {
                    let (ui, pending, f) = (ui.clone(), pending.clone(), Frame(buf));
                    ui.upgrade_in_event_loop(move |app| { app.set_art(Image::from_rgba8(f.0)); pending.store(false, Ordering::Release); })?;
                    shown += 1;
                }
                if t0.elapsed() >= Duration::from_secs(2) {
                    eprintln!("video: {:.1} fps shown, {:.1} ms/frame decode+convert", shown as f32 / t0.elapsed().as_secs_f32(), dec_total.as_secs_f32() * 1000.0 / (shown.max(1) as f32));
                    (shown, t0, dec_total) = (0, Instant::now(), Duration::ZERO);
                }
            }
        }
    }
}

fn main() {
    let lang = std::env::var("ASCNET_LANG").unwrap_or_default();
    let font = match lang.as_str() { "zh-CN" => "Noto Sans SC", "ja" => "Noto Sans JP", _ => "Noto Sans" };
    let app = App::new().unwrap();
    if !lang.is_empty() { slint::select_bundled_translation(&lang).unwrap(); }
    // ASCNET_FONT=system -> leave default-font-family empty to test OS/fontique fallback
    match std::env::var("ASCNET_FONT").as_deref() {
        Ok("system") => app.set_ui_font("".into()),
        _ => app.set_ui_font(font.into()),
    }
    // ASCNET_SIZE=WxH: test cover-scaling at other window sizes
    if let Some((w, h)) = std::env::var("ASCNET_SIZE").ok().and_then(|v| v.split_once('x').map(|(a, b)| (a.parse().unwrap_or(1280.0), b.parse().unwrap_or(720.0)))) {
        app.window().set_size(slint::LogicalSize::new(w, h));
    }
    let w = app.as_weak();
    app.on_minimize(move || w.upgrade().unwrap().window().set_minimized(true));
    app.on_quit(|| { slint::quit_event_loop().ok(); });
    let w = app.as_weak();
    app.on_drag(move || { w.upgrade().unwrap().window().with_winit_window(|ww| { let _ = ww.drag_window(); }); });
    if std::env::var("ASCNET_NOVIDEO").is_err() {
        let path = std::env::var("ASCNET_VIDEO").unwrap_or_else(|_| "assets/background.mp4".into());
        let weak = app.as_weak();
        std::thread::spawn(move || { if let Err(e) = video_loop(path, weak) { eprintln!("video error: {e}"); } });
    }
    app.run().unwrap();
}
