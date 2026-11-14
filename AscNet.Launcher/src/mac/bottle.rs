//! The AscNet CrossOver bottle: create it, fill it with what PGR needs, say whether it needs repair, and map paths.
//!
//! Commands used (all from CrossOver's own `SharedSupport/CrossOver/bin`):
//! - `cxbottle --bottle AscNet --create --template win10_64`: CrossOver's documented CLI. It has no component installer
//!   (CrossTie installs exist only inside CrossOver.app), so the components are installed the way CrossOver's own
//!   profiles do:
//! - **vcrun2015-2022**: Microsoft's `vc_redist.x64.exe` (`https://aka.ms/vc14/vc_redist.x64.exe`; the redirect target
//!   embeds the file's SHA-256, which is checked) run with `/install /quiet /norestart` through `wineloader`.
//! - **corefonts**: the eleven Microsoft core-font installers (SourceForge `corefonts` project, SHA-256 pinned below),
//!   unpacked with CrossOver's `cabextract` into `C:\windows\Fonts`.
//! - **d3dcompiler_47**: CrossOver's Wine ships it as a builtin DLL and `cxbottle` puts it in `system32`; it is only checked.
//! - `wineloader reg add …` for `RetinaMode=N` and the 1920x1080 windowed Unity player prefs.
use super::{
    crossover::{self, Cancelled, Installed},
    wine,
};
use anyhow::{bail, ensure, Context, Result};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::{AtomicBool, Ordering::SeqCst},
};

pub const NAME: &str = "AscNet";
const TEMPLATE: &str = "win10_64";
const VC_REDIST_URL: &str = "https://aka.ms/vc14/vc_redist.x64.exe";
/// Present only after the real redistributable ran (the bottle's builtin runtime lacks it).
const VC_MARKER: &str = "vcruntime140_threads.dll";
const FONT_MARKER: &str = "arial.ttf";
const MAC_DRIVER: &str = r"Software\Wine\Mac Driver";
const PGR_PREFS: &str = r"Software\kurogame\PGR";
const SETUP_FILE: &str = "ascnet-bottle.json";

/// (installer, SHA-256), from `https://downloads.sourceforge.net/project/corefonts/the%20fonts/final/<name>`.
const COREFONTS: [(&str, &str); 11] = [
    ("andale32.exe", "0524fe42951adc3a7eb870e32f0920313c71f170c859b5f770d82b4ee111e970"),
    ("arial32.exe", "85297a4d146e9c87ac6f74822734bdee5f4b2a722d7eaa584b7f2cbf76f478f6"),
    ("arialb32.exe", "a425f0ffb6a1a5ede5b979ed6177f4f4f4fdef6ae7c302a7b7720ef332fec0a8"),
    ("comic32.exe", "9c6df3feefde26d4e41d4a4fe5db2a89f9123a772594d7f59afd062625cd204e"),
    ("courie32.exe", "bb511d861655dde879ae552eb86b134d6fae67cb58502e6ff73ec5d9151f3384"),
    ("georgi32.exe", "2c2c7dcda6606ea5cf08918fb7cd3f3359e9e84338dc690013f20cd42e930301"),
    ("impact32.exe", "6061ef3b7401d9642f5dfdb5f2b376aa14663f6275e60a51207ad4facf2fccfb"),
    ("times32.exe", "db56595ec6ef5d3de5c24994f001f03b2a13e37cee27bc25c58f6f43e8f807ab"),
    ("trebuc32.exe", "5a690d9bb8510be1b8b4fe49f1f2319651fe51bbe54775ddddd8ef0bd07fdac9"),
    ("verdan32.exe", "c1cb61255e363166794e47664e2f21af8e3a26cb6346eb8d2ae2fa85dd5aad96"),
    ("webdin32.exe", "64595b5abc1080fba8610c5c34fab5863408e806aafe84653ca8575bed17d75a"),
];

/// Windows prefs the starter enforced: windowed, 1920x1080, not "native resolution", monitor 0.
const PGR_DWORDS: [(&str, u32); 7] = [
    ("Screenmanager Fullscreen mode_h3630240806", 3),
    ("Screenmanager Resolution Use Native_h1405027254", 0),
    ("Screenmanager Resolution Width_h182942802", 1920),
    ("Screenmanager Resolution Height_h2627697771", 1080),
    ("Screenmanager Resolution Window Width_h2524650974", 1920),
    ("Screenmanager Resolution Window Height_h1684712807", 1080),
    ("UnitySelectMonitor_h17969598", 0),
];

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Bottle {
    dir: PathBuf,
    /// A prefix that already existed (CrossOver bottle or Wine/wrapper prefix) and was chosen by the player: it is
    /// used as is, never created, repaired or reconfigured.
    adopted: bool,
}

/// The adopted prefix of this run, set by the controller from its settings.
static ADOPTED: std::sync::Mutex<Option<PathBuf>> = std::sync::Mutex::new(None);

/// What is wrong with a bottle that exists. `Display` is player-facing.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Problem {
    Fonts,
    VcRuntime,
    D3dCompiler,
    RetinaMode,
    /// The one-time 1920x1080 window prefs were never written.
    WindowSettings,
}

impl std::fmt::Display for Problem {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Problem::Fonts => "the Microsoft core fonts are missing",
            Problem::VcRuntime => "the Visual C++ runtime is missing",
            Problem::D3dCompiler => "the Direct3D shader compiler is missing",
            Problem::RetinaMode => "Retina mode is on",
            Problem::WindowSettings => "the 1920x1080 window settings are not set",
        })
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Status {
    /// No bottle yet: run [`Bottle::ensure`].
    Missing,
    /// Run [`Bottle::ensure`] again; it redoes exactly these.
    NeedsRepair(Vec<Problem>),
    Ready,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Step {
    Creating,
    Fonts,
    VcRuntime,
    Settings,
}

#[derive(Clone, Copy, Debug)]
pub struct Progress {
    pub step: Step,
    /// Bytes downloaded in this step (0 when not downloading).
    pub done: u64,
    pub total: u64,
}

#[derive(Default, Serialize, Deserialize)]
struct Setup {
    window_settings: bool,
}

impl Bottle {
    /// `~/Library/Application Support/CrossOver/Bottles/AscNet`. For testing, `ASCNET_BOTTLE=AscNet-test-<n>`
    /// selects a throwaway bottle instead (any other value is ignored, so a real bottle can never be redirected).
    pub fn ascnet() -> Result<Bottle> {
        match std::env::var("ASCNET_BOTTLE") {
            Ok(name) if name.starts_with("AscNet-test-") => Bottle::named(&name),
            _ => Bottle::named(NAME),
        }
    }

    /// The adopted prefix when the player chose one, else [`Bottle::ascnet`].
    pub fn active() -> Result<Bottle> {
        match ADOPTED.lock().unwrap_or_else(|e| e.into_inner()).clone() {
            Some(dir) => Ok(Bottle::adopted(dir)),
            None => Bottle::ascnet(),
        }
    }

    /// Makes [`Bottle::active`] return `prefix` (`None` = the AscNet bottle again).
    pub fn set_adopted(prefix: Option<PathBuf>) {
        *ADOPTED.lock().unwrap_or_else(|e| e.into_inner()) = prefix;
    }

    pub fn adopted(dir: PathBuf) -> Bottle {
        Bottle { dir, adopted: true }
    }

    pub fn is_adopted(&self) -> bool {
        self.adopted
    }

    pub fn named(name: &str) -> Result<Bottle> {
        ensure!(!name.is_empty() && !name.contains(['/', '\\']), "bad bottle name {name:?}");
        let home = std::env::var_os("HOME").context("HOME is not set")?;
        Ok(Bottle { dir: Path::new(&home).join("Library/Application Support/CrossOver/Bottles").join(name), adopted: false })
    }

    pub fn name(&self) -> &str {
        self.dir.file_name().and_then(|n| n.to_str()).unwrap_or(NAME)
    }

    /// The bottle folder (`WINEPREFIX`).
    pub fn dir(&self) -> &Path {
        &self.dir
    }

    pub fn drive_c(&self) -> PathBuf {
        self.dir.join("drive_c")
    }

    /// Where the pinned client lives: `C:\Games\Punishing Gray Raven` (for `download` and `install.rs`).
    pub fn game_dir(&self) -> PathBuf {
        self.drive_c().join("Games/Punishing Gray Raven")
    }

    /// GStreamer's plugin cache. An adopted prefix is not written to, so its cache lives in the user's caches folder.
    pub fn gst_registry(&self) -> PathBuf {
        match (self.adopted, std::env::var_os("HOME")) {
            (true, Some(home)) => Path::new(&home).join("Library/Caches/AscNet Launcher/gstreamer-registry.bin"),
            _ => self.dir.join("ascnet-gstreamer-registry.bin"),
        }
    }

    pub fn status(&self) -> Status {
        if self.adopted {
            return if self.dir.join("system.reg").is_file() { Status::Ready } else { Status::Missing };
        }
        if !self.dir.join("cxbottle.conf").is_file() || !self.dir.join("system.reg").is_file() {
            return Status::Missing;
        }
        let sys = self.drive_c().join("windows/system32");
        let mut problems = Vec::new();
        if !self.drive_c().join("windows/Fonts").join(FONT_MARKER).is_file() {
            problems.push(Problem::Fonts);
        }
        if !sys.join(VC_MARKER).is_file() {
            problems.push(Problem::VcRuntime);
        }
        if !sys.join("d3dcompiler_47.dll").is_file() {
            problems.push(Problem::D3dCompiler);
        }
        let user_reg = fs::read_to_string(self.dir.join("user.reg")).unwrap_or_default();
        if reg_value(&user_reg, MAC_DRIVER, "RetinaMode").as_deref() != Some("\"N\"") {
            problems.push(Problem::RetinaMode);
        }
        if !self.setup().window_settings {
            problems.push(Problem::WindowSettings);
        }
        if problems.is_empty() {
            Status::Ready
        } else {
            Status::NeedsRepair(problems)
        }
    }

    fn setup(&self) -> Setup {
        fs::read(self.dir.join(SETUP_FILE)).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default()
    }

    /// Creates the bottle if needed and redoes whatever [`Bottle::status`] reports. Idempotent. Blocking; ends with
    /// `Err(Cancelled)` when `cancel` is raised. Must not run while the game is.
    pub fn ensure(&self, cx: &Installed, cancel: &AtomicBool, progress: &mut dyn FnMut(Progress)) -> Result<()> {
        let mut report = |step, done, total| progress(Progress { step, done, total });
        ensure!(!self.adopted, "{} is an existing prefix; AscNet does not create or repair it", self.dir.display());
        let stop = || if cancel.load(SeqCst) { Err(anyhow::Error::from(Cancelled)) } else { Ok(()) };
        if self.status() == Status::Missing {
            report(Step::Creating, 0, 0);
            // A half-made bottle (killed mid-create) has no cxbottle.conf; start clean.
            if self.dir.exists() {
                fs::remove_dir_all(&self.dir).with_context(|| format!("removing the incomplete bottle {}", self.dir.display()))?;
            }
            let out = Command::new(cx.bin("cxbottle"))
                .args(["--bottle", self.name(), "--create", "--template", TEMPLATE, "--description", "AscNet (Punishing Gray Raven)"])
                .output()
                .context("running cxbottle")?;
            ensure!(out.status.success(), "cxbottle --create failed: {}", String::from_utf8_lossy(&out.stderr).trim());
            wine::run(cx, self, &["wineboot", "-u"]).context("initialising the bottle")?;
        }
        stop()?;
        let Status::NeedsRepair(problems) = self.status() else { return Ok(()) };
        let scratch = std::env::temp_dir().join(format!("ascnet-bottle-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&scratch)?;
        let result = (|| -> Result<()> {
            if problems.contains(&Problem::Fonts) {
                report(Step::Fonts, 0, 0);
                self.install_fonts(cx, &scratch, cancel)?;
                stop()?;
            }
            if problems.contains(&Problem::VcRuntime) {
                report(Step::VcRuntime, 0, 0);
                self.install_vcrun(cx, &scratch, cancel, &mut |d, t| report(Step::VcRuntime, d, t))?;
                stop()?;
            }
            if problems.contains(&Problem::D3dCompiler) {
                // The bottle lost a builtin; `wineboot -u` restores it from CrossOver's Wine.
                wine::run(cx, self, &["wineboot", "-u"])?;
            }
            if problems.contains(&Problem::RetinaMode) || problems.contains(&Problem::WindowSettings) {
                report(Step::Settings, 0, 0);
                self.apply_settings(cx)?;
            }
            Ok(())
        })();
        let _ = fs::remove_dir_all(&scratch);
        // Registry hives are written when wineserver exits.
        let _ = wine::stop(cx, self);
        result?;
        match self.status() {
            Status::Ready => Ok(()),
            Status::NeedsRepair(left) => bail!("the AscNet bottle is still not ready: {}", left.iter().map(|p| p.to_string()).collect::<Vec<_>>().join("; ")),
            Status::Missing => bail!("the AscNet bottle disappeared"),
        }
    }

    fn install_fonts(&self, cx: &Installed, scratch: &Path, cancel: &AtomicBool) -> Result<()> {
        let fonts = self.drive_c().join("windows/Fonts");
        fs::create_dir_all(&fonts)?;
        for (name, sha) in COREFONTS {
            let exe = scratch.join(name);
            let (_, got) = crossover::download(&format!("https://downloads.sourceforge.net/project/corefonts/the%20fonts/final/{name}"), &exe, cancel, &mut |_, _| {})?;
            ensure!(got == sha, "{name} does not match its pinned SHA-256 (got {got})");
            // -L lower-cases the names; the fonts are TrueType files inside the self-extracting cabinet.
            let out = Command::new(cx.bin("cabextract")).args(["-q", "-L", "-F", "*.ttf", "-d"]).arg(&fonts).arg(&exe).output().context("running cabextract")?;
            ensure!(out.status.success(), "cabextract {name} failed: {}", String::from_utf8_lossy(&out.stderr).trim());
        }
        Ok(())
    }

    fn install_vcrun(&self, cx: &Installed, scratch: &Path, cancel: &AtomicBool, on_bytes: &mut dyn FnMut(u64, u64)) -> Result<()> {
        let exe = scratch.join("vc_redist.x64.exe");
        let (url, sha) = crossover::download(VC_REDIST_URL, &exe, cancel, on_bytes)?;
        // Microsoft's CDN path is `…/<GUID>/<SHA-256 in upper case>/VC_redist.x64.exe`.
        ensure!(url.starts_with("https://download.visualstudio.microsoft.com/"), "unexpected redistributable host {url}");
        let pinned = url.rsplit('/').nth(1).unwrap_or_default().to_ascii_lowercase();
        ensure!(pinned.len() == 64 && pinned == sha, "the Visual C++ redistributable does not match the hash in its URL ({sha})");
        let status = wine::command(cx, self).arg(&exe).args(["/install", "/quiet", "/norestart"]).status().context("running the Visual C++ installer")?;
        // 3010 = success, restart suggested.
        ensure!(matches!(status.code(), Some(0 | 3010)), "the Visual C++ installer failed ({status})");
        Ok(())
    }

    fn apply_settings(&self, cx: &Installed) -> Result<()> {
        wine::reg_add(cx, self, MAC_DRIVER, "RetinaMode", "REG_SZ", "N")?;
        if !self.setup().window_settings {
            for (key, value) in PGR_DWORDS {
                wine::reg_add(cx, self, PGR_PREFS, key, "REG_DWORD", &value.to_string())?;
            }
            fs::write(self.dir.join(SETUP_FILE), serde_json::to_vec(&Setup { window_settings: true })?)?;
        }
        Ok(())
    }

    /// Deletes the bottle (`cxbottle --delete --force`).
    pub fn delete(&self, cx: &Installed) -> Result<()> {
        let _ = wine::stop(cx, self);
        let out = Command::new(cx.bin("cxbottle")).args(["--bottle", self.name(), "--delete", "--force"]).output().context("running cxbottle")?;
        ensure!(out.status.success(), "cxbottle --delete failed: {}", String::from_utf8_lossy(&out.stderr).trim());
        Ok(())
    }

    /// `C:\…` (or `Z:\…`) for a macOS path, through the bottle's `dosdevices` drives (longest matching target wins).
    pub fn to_windows(&self, mac: &Path) -> Option<String> {
        let mac = canonical(mac);
        let best = self
            .drives()
            .into_iter()
            .filter_map(|(letter, target)| mac.strip_prefix(&target).ok().map(|rest| (target.as_os_str().len(), letter, rest.to_path_buf())))
            .max_by_key(|(len, ..)| *len)?;
        let rest: Vec<_> = best.2.components().map(|c| c.as_os_str().to_string_lossy().into_owned()).collect();
        Some(format!("{}:\\{}", best.1.to_ascii_uppercase(), rest.join("\\")))
    }

    /// The macOS path for a Windows path such as `C:\Games\PGR\PGR.exe`.
    pub fn to_mac(&self, windows: &str) -> Option<PathBuf> {
        let (letter, rest) = windows.split_once(':')?;
        let letter = letter.to_ascii_lowercase();
        let root = self.drives().into_iter().find(|(l, _)| *l == letter)?.1;
        let mut path = root;
        for part in rest.split(['\\', '/']).filter(|p| !p.is_empty()) {
            path.push(part);
        }
        Some(path)
    }

    /// `(letter, canonical macOS target)` for each `dosdevices/<letter>:` symlink.
    pub(super) fn drives(&self) -> Vec<(String, PathBuf)> {
        let Ok(entries) = fs::read_dir(self.dir.join("dosdevices")) else { return Vec::new() };
        entries
            .flatten()
            .filter_map(|e| {
                let name = e.file_name().to_string_lossy().into_owned();
                let letter = name.strip_suffix(':').filter(|l| l.len() == 1)?.to_ascii_lowercase();
                Some((letter, e.path().canonicalize().ok()?))
            })
            .collect()
    }
}

/// `canonicalize` that also works for a path that does not exist yet: resolves the longest existing ancestor.
fn canonical(path: &Path) -> PathBuf {
    let mut tail = Vec::new();
    let mut head = path;
    loop {
        if let Ok(real) = head.canonicalize() {
            return tail.iter().rev().fold(real, |p, part| p.join(part));
        }
        match (head.parent(), head.file_name()) {
            (Some(parent), Some(name)) => {
                tail.push(name);
                head = parent;
            }
            _ => return path.to_path_buf(),
        }
    }
}

/// Value text (e.g. `"N"` with quotes, or `dword:00000780`) of `key` in `[section]` of a Wine `.reg` file.
fn reg_value(reg: &str, section: &str, key: &str) -> Option<String> {
    let header = format!("[{}]", section.replace('\\', "\\\\")).to_ascii_lowercase();
    let mut inside = false;
    for line in reg.lines() {
        if line.starts_with('[') {
            inside = line.to_ascii_lowercase().starts_with(&header);
        } else if inside {
            if let Some(rest) = line.strip_prefix(&format!("\"{key}\"=")) {
                return Some(rest.to_string());
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_wine_registry_values() {
        let reg = "[Software\\\\Wine\\\\Mac Driver] 1779741344\n#time=1\n\"RetinaMode\"=\"N\"\n\n[Software\\\\Other] 1\n\"RetinaMode\"=\"Y\"\n";
        assert_eq!(reg_value(reg, MAC_DRIVER, "RetinaMode").as_deref(), Some("\"N\""));
        assert_eq!(reg_value(reg, MAC_DRIVER, "Nope"), None);
    }

    #[test]
    fn maps_paths_through_dosdevices() {
        let tmp = std::env::temp_dir().join(format!("ascnet-bottle-map-{}", uuid::Uuid::new_v4()));
        let b = Bottle { dir: tmp.join("B"), adopted: false };
        fs::create_dir_all(b.dir.join("dosdevices")).unwrap();
        fs::create_dir_all(b.drive_c().join("Games/PGR")).unwrap();
        fs::create_dir_all(tmp.join("extern")).unwrap();
        std::os::unix::fs::symlink("../drive_c", b.dir.join("dosdevices/c:")).unwrap();
        std::os::unix::fs::symlink("/", b.dir.join("dosdevices/z:")).unwrap();
        std::os::unix::fs::symlink(tmp.join("extern"), b.dir.join("dosdevices/d:")).unwrap();
        assert_eq!(b.to_windows(&b.drive_c().join("Games/PGR/PGR.exe")).unwrap(), r"C:\Games\PGR\PGR.exe");
        assert_eq!(b.to_windows(&tmp.join("extern/x/y.bin")).unwrap(), r"D:\x\y.bin");
        assert!(b.to_windows(Path::new("/usr/bin/true")).unwrap().starts_with(r"Z:\usr"));
        assert_eq!(b.to_mac(r"c:\Games\PGR\PGR.exe").unwrap(), b.drive_c().canonicalize().unwrap().join("Games/PGR/PGR.exe"));
        assert!(b.to_mac(r"Q:\x").is_none());
        fs::remove_dir_all(&tmp).unwrap();
    }

    #[test]
    fn missing_bottle_reports_missing() {
        let b = Bottle::named("AscNet-test-does-not-exist").unwrap();
        assert_eq!(b.status(), Status::Missing);
        assert!(Bottle::named("a/b").is_err());
    }

    /// The acceptance run: real throwaway bottle, components, a Windows program through the launch path, deletion.
    /// `cargo test --offline mac::bottle -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn throwaway_bottle_end_to_end() {
        let cx = crossover::detect().expect("CrossOver is not installed");
        let bottle = Bottle::named(&format!("AscNet-test-{}", std::process::id())).unwrap();
        let cancel = AtomicBool::new(false);
        let guard = scopeguard(&cx, &bottle);
        assert_eq!(bottle.status(), Status::Missing);
        bottle.ensure(&cx, &cancel, &mut |p| eprintln!("{p:?}")).unwrap();
        assert_eq!(bottle.status(), Status::Ready);
        let before = fs::metadata(bottle.dir().join("user.reg")).unwrap().modified().unwrap();
        bottle.ensure(&cx, &cancel, &mut |p| panic!("second ensure redid {p:?}")).unwrap();
        assert_eq!(fs::metadata(bottle.dir().join("user.reg")).unwrap().modified().unwrap(), before);
        // Repair: undo Retina mode, remove a font.
        wine::reg_add(&cx, &bottle, MAC_DRIVER, "RetinaMode", "REG_SZ", "Y").unwrap();
        wine::stop(&cx, &bottle).unwrap();
        fs::remove_file(bottle.drive_c().join("windows/Fonts/arial.ttf")).unwrap();
        assert_eq!(bottle.status(), Status::NeedsRepair(vec![Problem::Fonts, Problem::RetinaMode]));
        bottle.ensure(&cx, &cancel, &mut |p| eprintln!("repair {p:?}")).unwrap();
        assert_eq!(bottle.status(), Status::Ready);

        let out = wine::run_in_game_env(&cx, &bottle, Path::new("/tmp"), "cmd", &["/c", "ver"], wine::DEFAULT_PATCH_ORIGIN).unwrap();
        let text = String::from_utf8_lossy(&out.stdout);
        eprintln!("cmd /c ver -> {text}");
        assert!(out.status.success() && text.contains("Microsoft Windows"), "{text}");

        // Track a long-running "game": ping.exe copied to PGR.exe.
        let dir = bottle.game_dir();
        fs::create_dir_all(&dir).unwrap();
        fs::copy(bottle.drive_c().join("windows/system32/ping.exe"), dir.join("PGR.exe")).unwrap_or_else(|e| panic!("no ping.exe: {e}"));
        assert!(!wine::game_running().unwrap(), "a real PGR.exe is running; refusing to test");
        let log = bottle.dir().join("ascnet-test.log");
        let mut game = wine::launch(&cx, &bottle, &dir, "PGR.exe", &["-n".into(), "8".into(), "127.0.0.1".into()], "http://127.0.0.1:8080", &log).unwrap();
        std::thread::sleep(std::time::Duration::from_secs(4));
        assert!(game.exited().unwrap().is_none());
        assert!(wine::game_running().unwrap(), "PGR.exe not seen in ps");
        assert!(game.wait().unwrap().success());
        std::thread::sleep(std::time::Duration::from_secs(1));
        assert!(!wine::game_running().unwrap());
        eprintln!("game log: {}", fs::read_to_string(&log).unwrap());
        assert_eq!(bottle.to_windows(&dir.join("PGR.exe")).unwrap(), r"C:\Games\Punishing Gray Raven\PGR.exe");
        drop(guard);
        assert!(!bottle.dir().exists());
    }

    struct Cleanup(Installed, Bottle);
    impl Drop for Cleanup {
        fn drop(&mut self) {
            if self.1.dir().exists() {
                let _ = self.1.delete(&self.0);
            }
        }
    }
    fn scopeguard(cx: &Installed, b: &Bottle) -> Cleanup {
        Cleanup(cx.clone(), b.clone())
    }
}
