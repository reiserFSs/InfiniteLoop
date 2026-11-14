#[cfg(windows)]
pub mod access;
pub mod download;
pub mod fps;
pub mod bundle;
pub mod media;
pub mod mods;
pub mod graphics_ini;
#[cfg(target_os = "macos")]
pub mod mac;
pub mod install;
pub mod local;
pub mod package;
mod msvc;
#[cfg(target_os = "macos")]
pub mod rosetta;
pub mod setup;
mod pgrbase;
pub mod updater;
