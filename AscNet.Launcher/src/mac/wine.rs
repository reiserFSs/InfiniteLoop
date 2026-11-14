//! Runs Windows programs in a CrossOver bottle through CrossOver's own `wineloader`, with the environment of
//! `launch-pgr-ascnet.sh` plus `version=n,b` and `ASCNET_PATCH_ORIGIN`, and tells whether PGR.exe is running.
use super::{bottle::Bottle, crossover::Installed};
use anyhow::{ensure, Context, Result};
use std::{
    fs,
    path::Path,
    process::{Child, Command, ExitStatus, Output, Stdio},
};

/// `WINEDLLOVERRIDES` of the launch script (D3DMetal/DXMT replace d3d11/dxgi) plus AscNet's `version.dll` proxy.
const DLL_OVERRIDES: &str = "d3d11,dxgi=n,b;version=n,b";
/// Where the AscNet server answers when nothing else is configured (`ASCNET_PATCH_ORIGIN` of the launch script).
pub const DEFAULT_PATCH_ORIGIN: &str = "http://127.0.0.1:8080";

/// `wineloader` with the minimal environment for CrossOver's Wine on `bottle` (registry edits, installers).
pub fn command(cx: &Installed, bottle: &Bottle) -> Command {
    let root = cx.root.display().to_string();
    let mut c = Command::new(cx.bin("wineloader"));
    c.env("CX_ROOT", &root)
        .env("WINEPREFIX", bottle.dir())
        .env("WINELOADER", cx.bin("wineloader"))
        .env("WINESERVER", cx.bin("wineserver"))
        .env("DYLD_FALLBACK_LIBRARY_PATH", dyld_path(&root))
        .env("WINEDEBUG", "-all")
        .env("WINEESYNC", "0")
        .env("WINEFSYNC", "0")
        .env("WINEMSYNC", "0");
    c
}

fn dyld_path(root: &str) -> String {
    match std::env::var("DYLD_FALLBACK_LIBRARY_PATH") {
        Ok(old) if !old.is_empty() => format!("{root}/lib64:{root}/lib:{old}"),
        _ => format!("{root}/lib64:{root}/lib"),
    }
}

/// [`command`] plus the PGR launch environment of `launch-pgr-ascnet.sh`, `version=n,b` and `ASCNET_PATCH_ORIGIN`.
/// Deliberately not copied: `MTL_HUD_ENABLED=1` (a Metal debug overlay) and the script's hard-coded paths.
pub fn game_command(cx: &Installed, bottle: &Bottle, patch_origin: &str) -> Command {
    let root = cx.root.display().to_string();
    let gst = format!("{root}/lib64/gstreamer-1.0");
    let registry = bottle.gst_registry();
    if let Some(dir) = registry.parent() {
        let _ = fs::create_dir_all(dir);
    }
    let mut c = command(cx, bottle);
    c.env("CX_APPLEGPTK_LIBD3DSHARED_PATH", format!("{root}/lib64/apple_gptk/external/libd3dshared.dylib"))
        .env("CX_GRAPHICS_BACKEND", std::env::var("CX_GRAPHICS_BACKEND").unwrap_or_else(|_| "d3dmetal".into()))
        .env("CX_WINELOADER", cx.bin("wineloader"))
        .env("GST_PLUGIN_PATH_1_0", &gst)
        .env("GST_PLUGIN_SYSTEM_PATH", &gst)
        .env("GST_PLUGIN_SYSTEM_PATH_1_0", &gst)
        .env("GST_REGISTRY", registry)
        .env("WINED3DMETAL", "1")
        .env("ASCNET_PATCH_ORIGIN", patch_origin)
        .env("WINEDLLOVERRIDES", DLL_OVERRIDES)
        .env("WINEDLLPATH", format!("{root}/lib/dxmt:{root}/lib/wine"))
        .env("WINEDLLPATH_PREPEND", format!("{root}/lib/dxmt"));
    c
}

/// Runs `wineloader <args>` in `bottle` to completion; an error carries its stderr.
pub fn run(cx: &Installed, bottle: &Bottle, args: &[&str]) -> Result<()> {
    let out = command(cx, bottle).args(args).stdin(Stdio::null()).output().with_context(|| format!("running wineloader {}", args.join(" ")))?;
    ensure!(out.status.success(), "wineloader {} failed ({}): {}", args.join(" "), out.status, String::from_utf8_lossy(&out.stderr).trim());
    Ok(())
}

/// `reg add HKCU\<key> /v <name> /t <kind> /d <data> /f`
pub fn reg_add(cx: &Installed, bottle: &Bottle, key: &str, name: &str, kind: &str, data: &str) -> Result<()> {
    run(cx, bottle, &["reg", "add", &format!(r"HKCU\{key}"), "/v", name, "/t", kind, "/d", data, "/f"])
}

/// Stops the bottle's `wineserver` (and with it every Windows process in the bottle); flushes the registry.
pub fn stop(cx: &Installed, bottle: &Bottle) -> Result<()> {
    // `-k` exits non-zero when no wineserver runs; that is the goal state, so only a failure to start is an error.
    Command::new(cx.bin("wineserver")).arg("-k").env("WINEPREFIX", bottle.dir()).env("CX_ROOT", &cx.root).output().context("running wineserver -k")?;
    Ok(())
}

/// A program started by [`launch`].
pub struct Game {
    child: Child,
}

impl Game {
    pub fn pid(&self) -> u32 {
        self.child.id()
    }

    /// `Some(status)` once it has exited (also reaps it).
    pub fn exited(&mut self) -> Result<Option<ExitStatus>> {
        Ok(self.child.try_wait()?)
    }

    pub fn wait(&mut self) -> Result<ExitStatus> {
        Ok(self.child.wait()?)
    }
}

/// Starts `exe` (a name or path relative to `dir`, e.g. `PGR.exe`) from the macOS folder `dir` in the game environment.
/// stdout and stderr are appended to `log`. The returned [`Game`] ends when the Windows process does.
pub fn launch(cx: &Installed, bottle: &Bottle, dir: &Path, exe: &str, args: &[String], patch_origin: &str, log: &Path) -> Result<Game> {
    ensure!(dir.join(exe).is_file(), "{} not found in {}", exe, dir.display());
    if let Some(parent) = log.parent() {
        fs::create_dir_all(parent)?;
    }
    let out = fs::OpenOptions::new().create(true).append(true).open(log).with_context(|| format!("opening {}", log.display()))?;
    let child = game_command(cx, bottle, patch_origin)
        .current_dir(dir)
        .arg(exe)
        .args(args)
        .stdin(Stdio::null())
        .stdout(out.try_clone()?)
        .stderr(out)
        .spawn()
        .with_context(|| format!("starting {exe}"))?;
    Ok(Game { child })
}

/// Like [`launch`] but waits and captures the output: for short programs such as `cmd /c ver`. `program` is looked up
/// by Wine (a Windows program name such as `cmd`, or a path relative to `dir`).
pub fn run_in_game_env(cx: &Installed, bottle: &Bottle, dir: &Path, program: &str, args: &[&str], patch_origin: &str) -> Result<Output> {
    game_command(cx, bottle, patch_origin).current_dir(dir).arg(program).args(args).stdin(Stdio::null()).output().with_context(|| format!("running {program}"))
}

/// Whether a `PGR.exe` Windows process is alive, from any launch (also after the launcher restarted).
/// Wine renames its processes to their Windows command line (`C:\…\PGR.exe`), which `ps` shows.
pub fn game_running() -> Result<bool> {
    let out = Command::new("/bin/ps").args(["-axww", "-o", "command="]).output().context("listing processes")?;
    Ok(String::from_utf8_lossy(&out.stdout).lines().any(is_game_command))
}

fn is_game_command(line: &str) -> bool {
    let line = line.trim_start().trim_start_matches('"').to_ascii_lowercase();
    // `PGR.exe …`, `C:\…\PGR.exe …` or `/…/PGR.exe …`: the program is the first word, ignoring spaces inside the path.
    line.match_indices("pgr.exe").any(|(at, _)| {
        let before_ok = at == 0 || matches!(line.as_bytes()[at - 1], b'\\' | b'/');
        let after = &line[at + "pgr.exe".len()..];
        before_ok && (after.is_empty() || after.starts_with([' ', '"']))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn recognises_the_game_process() {
        assert!(is_game_command(r"C:\Games\Punishing Gray Raven\PGR.exe"));
        assert!(is_game_command(r"PGR.exe -popupwindow"));
        assert!(is_game_command(r#""C:\Games\PGR.exe""#));
        assert!(!is_game_command(r"C:\Games\PGRBase.exe"));
        assert!(!is_game_command(r"C:\windows\system32\winedevice.exe"));
        assert!(!is_game_command("/usr/bin/vim notes-PGR.exe.txt"));
    }
}
