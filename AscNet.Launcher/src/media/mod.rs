//! Pure-Rust launcher background media: H.264 video (mp4 demuxer + OpenH264) and looping WAV music (cpal).
//! No Media Foundation, AVFoundation or GStreamer, so Windows, Wine and macOS share one path.

pub mod music;
pub mod video;

pub use music::Music;
pub use video::{Frame, Video};
