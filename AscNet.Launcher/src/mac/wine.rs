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

    /// The player's layout: a Sikarugir wrapper prefix (read from the real one: `dosdevices/x:` onto a Mac folder,
    /// `drive_c/users/Sikarugir` with `crossover`/`reiserfs` aliases). The game still runs on CrossOver's wineloader.
    #[test]
    fn an_adopted_wrapper_prefix_runs_on_crossovers_wine_with_the_launch_script_environment() {
        use std::{collections::BTreeMap, ffi::OsString, os::unix::fs::symlink};
        let root = std::env::temp_dir().join(format!("ascnet-wine-test-{}", uuid::Uuid::new_v4()));
        let root = { fs::create_dir_all(&root).unwrap(); root.canonicalize().unwrap() };
        let app = root.join("Applications/Sikarugir/Steam-AscNet.app");
        let prefix = app.join("Contents/SharedSupport/prefix");
        fs::create_dir_all(prefix.join("dosdevices")).unwrap();
        fs::create_dir_all(prefix.join("drive_c/users/Sikarugir/AppData/LocalLow")).unwrap();
        fs::create_dir_all(prefix.join("drive_c/users/Public")).unwrap();
        fs::write(prefix.join("system.reg"), "WINE REGISTRY Version 2\n").unwrap();
        symlink("../drive_c", prefix.join("dosdevices/c:")).unwrap();
        symlink("/", prefix.join("dosdevices/z:")).unwrap();
        for alias in ["crossover", "reiserfs"] {
            symlink("Sikarugir", prefix.join("drive_c/users").join(alias)).unwrap();
        }
        let games = root.join("Steam Games");
        let client = games.join("SteamLibrary/steamapps/common/Punishing Gray Raven");
        fs::create_dir_all(&client).unwrap();
        symlink(&games, prefix.join("dosdevices/x:")).unwrap();
        // The wrapper app ships its own Wine; it must never be used.
        fs::create_dir_all(app.join("Contents/SharedSupport/wine/bin")).unwrap();
        let cx = Installed { app: root.join("CrossOver.app"), root: root.join("CrossOver.app/Contents/SharedSupport/CrossOver"), version: "26.3.0".into() };

        let bottle = Bottle::adopted(prefix.clone());
        assert!(bottle.is_adopted());
        assert_eq!(bottle.to_windows(&client).as_deref(), Some(r"X:\SteamLibrary\steamapps\common\Punishing Gray Raven"));
        assert_eq!(bottle.local_low().unwrap(), prefix.canonicalize().unwrap().join("drive_c/users/Sikarugir/AppData/LocalLow"));

        let command = game_command(&cx, &bottle, "http://127.0.0.1:8080");
        assert_eq!(Path::new(command.get_program()), cx.bin("wineloader"), "CrossOver's wineloader, not the wrapper's Wine");
        let env: BTreeMap<String, Option<OsString>> = command.get_envs().map(|(k, v)| (k.to_string_lossy().into_owned(), v.map(OsString::from))).collect();
        let got = |key: &str| env.get(key).and_then(|v| v.as_ref()).map(|v| v.to_string_lossy().into_owned());
        let cxr = cx.root.display().to_string();
        for (key, want) in [
            ("WINEPREFIX", prefix.display().to_string()),
            ("WINELOADER", format!("{cxr}/bin/wineloader")),
            ("CX_WINELOADER", format!("{cxr}/bin/wineloader")),
            ("WINESERVER", format!("{cxr}/bin/wineserver")),
            ("CX_ROOT", cxr.clone()),
            ("CX_APPLEGPTK_LIBD3DSHARED_PATH", format!("{cxr}/lib64/apple_gptk/external/libd3dshared.dylib")),
            ("GST_PLUGIN_PATH_1_0", format!("{cxr}/lib64/gstreamer-1.0")),
            ("GST_PLUGIN_SYSTEM_PATH", format!("{cxr}/lib64/gstreamer-1.0")),
            ("GST_PLUGIN_SYSTEM_PATH_1_0", format!("{cxr}/lib64/gstreamer-1.0")),
            ("WINEDLLOVERRIDES", "d3d11,dxgi=n,b;version=n,b".into()),
            ("WINEDLLPATH", format!("{cxr}/lib/dxmt:{cxr}/lib/wine")),
            ("WINEDLLPATH_PREPEND", format!("{cxr}/lib/dxmt")),
            ("WINED3DMETAL", "1".into()),
            ("WINEDEBUG", "-all".into()),
            ("WINEESYNC", "0".into()),
            ("WINEFSYNC", "0".into()),
            ("WINEMSYNC", "0".into()),
            ("ASCNET_PATCH_ORIGIN", "http://127.0.0.1:8080".into()),
        ] {
            assert_eq!(got(key), Some(want), "{key}");
        }
        assert!(got("DYLD_FALLBACK_LIBRARY_PATH").unwrap().starts_with(&format!("{cxr}/lib64:{cxr}/lib")));
        assert!(got("CX_GRAPHICS_BACKEND").is_some());
        assert!(!env.contains_key("MTL_HUD_ENABLED"), "the script's Metal HUD is a debug overlay");
        // Nothing points into the wrapper app (its Wine) and the adopted prefix is not written to for the GStreamer cache.
        for (key, value) in &env {
            let value = value.as_ref().unwrap().to_string_lossy().into_owned();
            assert!(key == "WINEPREFIX" || !value.contains("Steam-AscNet.app"), "{key}={value}");
        }
        assert!(!bottle.gst_registry().starts_with(&prefix));
        let _ = fs::remove_dir_all(root);
    }
}
