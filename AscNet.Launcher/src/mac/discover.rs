//! Finds PGR installs that already run through CrossOver, so the launcher can use them instead of creating a bottle.
//!
//! Read-only: it lists folders and reads files, nothing is written. Prefixes come from CrossOver's bottles, wrapper
//! apps (`*.app/Contents/SharedSupport/prefix`, e.g. Sikarugir/Kegworks) under `~/Applications` and `/Applications`,
//! and the `WINEPREFIX` of a launch script when that is a plain assignment (see [`script_prefix`]). A prefix the player
//! picks by hand goes through [`in_prefix`]. In each prefix PGR.exe is looked for on `C:` (`Games`, `Program Files`)
//! and in every Steam library: the ones in the prefix's Steam `libraryfolders.vdf` plus `SteamLibrary`/`Steam`
//! folders on each `dosdevices` drive.
use super::bottle::Bottle;
use crate::{
    download::{self, Detected},
    install::{self, PatchState},
    package::PatchPackage,
    rosetta,
};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
};

/// Punishing: Gray Raven's Steam app id.
pub const STEAM_APP_ID: &str = "4125930";
const SCRIPT_NAME: &str = "launch-pgr-ascnet.sh";

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Kind {
    CrossOverBottle,
    /// A Wine prefix inside an app (Sikarugir, Kegworks…) or a plain folder; `maker` is the folder the app sits in.
    Wrapper { maker: Option<String> },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Candidate {
    /// `WINEPREFIX`.
    pub prefix: PathBuf,
    pub kind: Kind,
    /// Bottle name or wrapper app name.
    pub name: String,
    pub game_dir: PathBuf,
    /// `X:\SteamLibrary\steamapps\common\Punishing Gray Raven`
    pub windows_path: String,
    pub detected: Option<Detected>,
    /// `None` when no patch package is built yet, so the install can't be judged.
    pub patch: Option<PatchState>,
    pub rosetta: rosetta::RosettaState,
    /// Steam's `appmanifest_4125930.acf` sits beside it: Steam updates and verifies these files.
    pub steam: bool,
}

impl Candidate {
    /// "Steam-AscNet (Sikarugir)", "Games (CrossOver bottle)".
    pub fn display_name(&self) -> String {
        match &self.kind {
            Kind::CrossOverBottle => format!("{} (CrossOver)", self.name),
            Kind::Wrapper { maker: Some(maker) } => format!("{} ({maker})", self.name),
            Kind::Wrapper { maker: None } => self.name.clone(),
        }
    }

    /// Everything found, for the Details of the adoption prompt.
    pub fn report(&self) -> String {
        let region = self.detected.as_ref().and_then(|d| d.region).map_or("unidentified".to_owned(), |r| format!("{r:?}"));
        let version = self.detected.as_ref().and_then(|d| d.version.clone()).unwrap_or_else(|| "unknown".into());
        format!(
            "Prefix: {} ({})\nGame folder: {}\nWindows path: {}\nEdition {region}, version {version}\nAscNet patch files present: {}\nAscNet patch state: {}\nRosetta patches: {}\nSteam install: {}",
            self.prefix.display(),
            match self.kind {
                Kind::CrossOverBottle => "CrossOver bottle",
                Kind::Wrapper { .. } => "wrapper prefix",
            },
            self.game_dir.display(),
            self.windows_path,
            if self.detected.as_ref().is_some_and(|d| d.launcher_patched) { "yes" } else { "no" },
            self.patch.as_ref().map_or("not checked yet (Set up builds the patch to check against)".to_owned(), |p| format!("{p:?}")),
            match &self.rosetta {
                rosetta::RosettaState::Patched => "applied".to_owned(),
                rosetta::RosettaState::Needed => "not applied".to_owned(),
                rosetta::RosettaState::Unsupported(why) => format!("unsupported ({why})"),
            },
            if self.steam { "yes" } else { "no" },
        )
    }
}

/// Every PGR install in the player's CrossOver bottles, wrapper apps and launch script.
pub fn discover(package: Option<&PatchPackage>) -> Vec<Candidate> {
    let Some(home) = std::env::var_os("HOME").map(PathBuf::from) else { return Vec::new() };
    let apps = [home.join("Applications"), PathBuf::from("/Applications")];
    discover_in(&home, &apps, &launch_scripts(), package)
}

fn discover_in(home: &Path, app_roots: &[PathBuf], scripts: &[PathBuf], package: Option<&PatchPackage>) -> Vec<Candidate> {
    // The prefix of a launch script first: when two prefixes share one game folder, the one the player runs wins.
    let mut prefixes: Vec<PathBuf> = scripts.iter().filter_map(|s| fs::read_to_string(s).ok().and_then(|text| script_prefix(&text))).collect();
    // The launcher's own bottle is handled by the normal flow.
    for dir in subdirs(&home.join("Library/Application Support/CrossOver/Bottles")) {
        let own = dir.file_name().and_then(|n| n.to_str()).is_some_and(|n| n == super::bottle::NAME || n.starts_with("AscNet-test-"));
        if !own {
            prefixes.push(dir);
        }
    }
    for root in app_roots {
        for entry in subdirs(root) {
            // `Applications/X.app` and `Applications/Sikarugir/X.app`
            let apps = if is_app(&entry) { vec![entry] } else { subdirs(&entry).into_iter().filter(|d| is_app(d)).collect() };
            prefixes.extend(apps.into_iter().map(|app| app.join("Contents/SharedSupport/prefix")));
        }
    }
    let mut seen = Vec::new();
    let mut found = Vec::new();
    for prefix in prefixes {
        let Ok(real) = prefix.canonicalize() else { continue };
        if seen.contains(&real) {
            continue;
        }
        seen.push(real);
        for candidate in in_prefix(&prefix, package) {
            if !found.iter().any(|f: &Candidate| f.game_dir == candidate.game_dir) {
                found.push(candidate);
            }
        }
    }
    found
}

/// PGR installs inside one prefix; empty when `prefix` is not a Wine prefix.
pub fn in_prefix(prefix: &Path, package: Option<&PatchPackage>) -> Vec<Candidate> {
    if !prefix.join("system.reg").is_file() {
        return Vec::new();
    }
    let (kind, name) = classify(prefix);
    let bottle = Bottle::adopted(prefix.to_path_buf());
    games(&bottle)
        .into_iter()
        .map(|(game_dir, steam)| Candidate {
            prefix: prefix.to_path_buf(),
            kind: kind.clone(),
            name: name.clone(),
            windows_path: bottle.to_windows(&game_dir).unwrap_or_default(),
            detected: download::detect(&game_dir).ok(),
            patch: package.and_then(|p| install::inspect(&game_dir, p).ok()),
            rosetta: rosetta::inspect(&game_dir),
            game_dir,
            steam,
        })
        .collect()
}

fn classify(prefix: &Path) -> (Kind, String) {
    let file_name = |p: &Path| p.file_name().map(|n| n.to_string_lossy().into_owned());
    if prefix.parent().is_some_and(|p| p.ends_with("CrossOver/Bottles")) {
        return (Kind::CrossOverBottle, file_name(prefix).unwrap_or_default());
    }
    match prefix.ancestors().find(|a| is_app(a)) {
        Some(app) => {
            let maker = app.parent().and_then(file_name).filter(|m| m != "Applications");
            (Kind::Wrapper { maker }, app.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default())
        }
        None => (Kind::Wrapper { maker: None }, file_name(prefix).unwrap_or_default()),
    }
}

fn is_app(path: &Path) -> bool {
    path.extension().is_some_and(|e| e == "app")
}

fn subdirs(dir: &Path) -> Vec<PathBuf> {
    let mut dirs: Vec<PathBuf> = fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path()).filter(|p| p.is_dir()).collect();
    dirs.sort();
    dirs
}

/// Game folders (macOS paths) with whether Steam owns them.
fn games(bottle: &Bottle) -> Vec<(PathBuf, bool)> {
    let mut found: BTreeMap<PathBuf, bool> = BTreeMap::new();
    let mut add = |dir: PathBuf, steam: bool| {
        *found.entry(dir.canonicalize().unwrap_or(dir)).or_default() |= steam;
    };
    let mut plain = Vec::new();
    for rel in ["Games", "Program Files", "Program Files (x86)"] {
        find_pgr(&bottle.drive_c().join(rel), 2, &mut plain);
    }
    for dir in plain {
        add(dir, false);
    }
    // Z: is the whole Mac; a Steam library there is reached through its own drive or libraryfolders.vdf.
    let drives: Vec<PathBuf> = bottle.drives().into_iter().filter(|(letter, _)| letter != "z").map(|(_, target)| target).collect();
    let mut libraries: Vec<PathBuf> = Vec::new();
    for drive in &drives {
        for rel in ["", "SteamLibrary", "Steam", "Steam Library", "Program Files (x86)/Steam", "Program Files/Steam"] {
            let root = if rel.is_empty() { drive.clone() } else { drive.join(rel) };
            if !root.join("steamapps").is_dir() {
                continue;
            }
            if let Ok(text) = fs::read_to_string(root.join("steamapps/libraryfolders.vdf")) {
                libraries.extend(library_paths(&text).iter().filter_map(|w| bottle.to_mac(w)));
            }
            libraries.push(root);
        }
    }
    libraries.sort();
    libraries.dedup();
    for library in libraries {
        let apps = library.join("steamapps");
        let manifest = apps.join(format!("appmanifest_{STEAM_APP_ID}.acf"));
        let install_dir = fs::read_to_string(&manifest).ok().and_then(|text| install_dir(&text)).unwrap_or_else(|| "Punishing Gray Raven".into());
        let dir = apps.join("common").join(install_dir);
        if dir.join("PGR.exe").is_file() {
            add(dir, manifest.is_file());
        }
    }
    found.into_iter().collect()
}

/// Folders (up to `depth` below `dir`) that contain PGR.exe.
fn find_pgr(dir: &Path, depth: u32, out: &mut Vec<PathBuf>) {
    if dir.join("PGR.exe").is_file() {
        out.push(dir.to_path_buf());
    } else if depth > 0 {
        for sub in subdirs(dir) {
            find_pgr(&sub, depth - 1, out);
        }
    }
}

/// The quoted strings of a Valve KeyValues file, in order, with `\\` and `\"` unescaped (braces and comments are ignored).
fn quoted(text: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut chars = text.chars();
    while let Some(c) = chars.next() {
        if c != '"' {
            continue;
        }
        let mut value = String::new();
        while let Some(c) = chars.next() {
            match c {
                '"' => break,
                '\\' => value.extend(chars.next()),
                c => value.push(c),
            }
        }
        out.push(value);
    }
    out
}

/// The `path` entries (Windows paths) of a `libraryfolders.vdf`.
fn library_paths(vdf: &str) -> Vec<String> {
    let tokens = quoted(vdf);
    tokens.windows(2).filter(|p| p[0].eq_ignore_ascii_case("path")).map(|p| p[1].clone()).collect()
}

fn install_dir(acf: &str) -> Option<String> {
    let tokens = quoted(acf);
    tokens.windows(2).find(|p| p[0].eq_ignore_ascii_case("installdir")).map(|p| p[1].clone())
}

/// `launch-pgr-ascnet.sh` beside the launcher or the working folder (or any parent), and `ASCNET_LAUNCH_SCRIPT`.
fn launch_scripts() -> Vec<PathBuf> {
    let mut found: Vec<PathBuf> = std::env::var_os("ASCNET_LAUNCH_SCRIPT").map(PathBuf::from).into_iter().collect();
    let starts = [std::env::current_exe().ok(), std::env::current_dir().ok()];
    for start in starts.into_iter().flatten() {
        found.extend(start.ancestors().map(|a| a.join(SCRIPT_NAME)).filter(|p| p.is_file()));
    }
    found
}

/// The `WINEPREFIX` a shell script sets, when it is a plain assignment: `NAME="literal"` lines, `$VAR`/`${VAR}`/
/// `${VAR:-default}` expansion from earlier lines and the environment, nothing else. Command substitution or an
/// unknown variable means "not trivially parseable": `None`, and the player picks the prefix folder instead.
pub fn script_prefix(script: &str) -> Option<PathBuf> {
    let mut vars: BTreeMap<String, String> = BTreeMap::new();
    for line in script.lines() {
        let line = line.trim().trim_start_matches("export ").trim();
        let Some((name, value)) = line.split_once('=') else { continue };
        if name.is_empty() || !name.chars().all(|c| c.is_ascii_alphanumeric() || c == '_') {
            continue;
        }
        let value = value.trim();
        let value = value.strip_prefix('"').and_then(|v| v.strip_suffix('"')).or_else(|| value.strip_prefix('\'').and_then(|v| v.strip_suffix('\''))).unwrap_or(value);
        match expand(value, &vars) {
            Some(value) => {
                vars.insert(name.to_owned(), value);
            }
            None if name == "WINEPREFIX" => return None,
            None => {
                vars.remove(name);
            }
        }
    }
    vars.get("WINEPREFIX").filter(|p| p.starts_with('/')).map(PathBuf::from)
}

fn expand(value: &str, vars: &BTreeMap<String, String>) -> Option<String> {
    let lookup = |name: &str| vars.get(name).cloned().or_else(|| std::env::var(name).ok());
    let mut out = String::new();
    let mut rest = value;
    while let Some(at) = rest.find(['$', '`']) {
        out.push_str(&rest[..at]);
        rest = &rest[at..];
        if rest.starts_with('`') || rest.starts_with("$(") {
            return None;
        }
        rest = &rest[1..];
        if let Some(inner) = rest.strip_prefix('{') {
            let end = inner.find('}')?;
            let (name, default) = match inner[..end].split_once(":-") {
                Some((name, default)) => (name, Some(default)),
                None => (&inner[..end], None),
            };
            match (lookup(name).filter(|v| !v.is_empty()), default) {
                (Some(v), _) => out.push_str(&v),
                (None, Some(default)) => out.push_str(&expand(default, vars)?),
                (None, None) => return None,
            }
            rest = &inner[end + 1..];
        } else {
            let len = rest.find(|c: char| !(c.is_ascii_alphanumeric() || c == '_')).unwrap_or(rest.len());
            out.push_str(&lookup(&rest[..len])?);
            rest = &rest[len..];
        }
    }
    out.push_str(rest);
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::os::unix::fs::symlink;

    struct Tmp(PathBuf);
    impl Tmp {
        fn new() -> Tmp {
            let dir = std::env::temp_dir().join(format!("ascnet-discover-{}", uuid::Uuid::new_v4()));
            fs::create_dir_all(&dir).unwrap();
            // Canonical, so /var vs /private/var never makes path comparisons fail.
            Tmp(dir.canonicalize().unwrap())
        }
        fn dir(&self, rel: &str) -> PathBuf {
            let p = self.0.join(rel);
            fs::create_dir_all(&p).unwrap();
            p
        }
    }
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    /// A prefix with `system.reg`, `drive_c` and `dosdevices/c:`.
    fn prefix(at: &Path) -> PathBuf {
        fs::create_dir_all(at.join("drive_c")).unwrap();
        fs::create_dir_all(at.join("dosdevices")).unwrap();
        fs::write(at.join("system.reg"), "WINE REGISTRY Version 2\n").unwrap();
        symlink("../drive_c", at.join("dosdevices/c:")).unwrap();
        at.to_path_buf()
    }

    fn pgr(dir: &Path) {
        fs::create_dir_all(dir).unwrap();
        fs::write(dir.join("PGR.exe"), b"MZ").unwrap();
    }

    fn scan(t: &Tmp) -> Vec<Candidate> {
        discover_in(&t.0.join("home"), &[t.0.join("home/Applications"), t.0.join("Applications")], &[], None)
    }

    #[test]
    fn crossover_bottle_with_game_on_c() {
        let t = Tmp::new();
        let p = prefix(&t.dir("home/Library/Application Support/CrossOver/Bottles/Games"));
        pgr(&p.join("drive_c/Program Files/Kuro/Punishing Gray Raven"));
        let found = scan(&t);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].kind, Kind::CrossOverBottle);
        assert_eq!(found[0].display_name(), "Games (CrossOver)");
        assert_eq!(found[0].windows_path, r"C:\Program Files\Kuro\Punishing Gray Raven");
        assert!(!found[0].steam);
        assert!(found[0].patch.is_none());
    }

    #[test]
    fn the_launchers_own_bottle_is_not_offered() {
        let t = Tmp::new();
        let p = prefix(&t.dir("home/Library/Application Support/CrossOver/Bottles/AscNet"));
        pgr(&p.join("drive_c/Games/Punishing Gray Raven"));
        assert!(scan(&t).is_empty());
    }

    #[test]
    fn wrapper_prefix_with_steam_library_on_a_mapped_drive() {
        let t = Tmp::new();
        let p = prefix(&t.dir("home/Applications/Sikarugir/Steam-AscNet.app/Contents/SharedSupport/prefix"));
        // Steam in the prefix names the library on drive X:, which maps to a Mac folder.
        let steam = p.join("drive_c/Program Files (x86)/Steam/steamapps");
        fs::create_dir_all(&steam).unwrap();
        fs::write(steam.join("libraryfolders.vdf"), r#""libraryfolders" { "0" { "path" "X:\\SteamLibrary" } }"#).unwrap();
        let mapped = t.dir("Games Drive");
        symlink(&mapped, p.join("dosdevices/x:")).unwrap();
        let library = mapped.join("SteamLibrary/steamapps");
        fs::create_dir_all(&library).unwrap();
        fs::write(library.join("appmanifest_4125930.acf"), r#""AppState" { "appid" "4125930" "installdir" "Punishing Gray Raven" }"#).unwrap();
        pgr(&library.join("common/Punishing Gray Raven"));
        let found = scan(&t);
        assert_eq!(found.len(), 1, "{found:?}");
        assert_eq!(found[0].display_name(), "Steam-AscNet (Sikarugir)");
        assert_eq!(found[0].windows_path, r"X:\SteamLibrary\steamapps\common\Punishing Gray Raven");
        assert_eq!(found[0].game_dir, library.join("common/Punishing Gray Raven"));
        assert!(found[0].steam);
        assert!(found[0].report().contains("Steam install: yes"));
    }

    #[test]
    fn a_mapped_library_without_vdf_and_no_game() {
        let t = Tmp::new();
        let p = prefix(&t.dir("Applications/Plain.app/Contents/SharedSupport/prefix"));
        let mapped = t.dir("Disk");
        symlink(&mapped, p.join("dosdevices/d:")).unwrap();
        fs::create_dir_all(mapped.join("SteamLibrary/steamapps/common/Other Game")).unwrap();
        assert!(scan(&t).is_empty(), "no PGR.exe anywhere");
        pgr(&mapped.join("SteamLibrary/steamapps/common/Punishing Gray Raven"));
        let found = scan(&t);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].display_name(), "Plain");
        assert!(!found[0].steam, "no appmanifest, so Steam doesn't own it");
    }

    #[test]
    fn two_installs_are_two_candidates() {
        let t = Tmp::new();
        let a = prefix(&t.dir("home/Library/Application Support/CrossOver/Bottles/One"));
        let b = prefix(&t.dir("home/Applications/Two.app/Contents/SharedSupport/prefix"));
        pgr(&a.join("drive_c/Games/PGR"));
        pgr(&b.join("drive_c/Games/Punishing Gray Raven"));
        pgr(&b.join("drive_c/Games/Copy/PGR"));
        let found = scan(&t);
        assert_eq!(found.len(), 3);
        assert_eq!(found.iter().filter(|c| c.kind == Kind::CrossOverBottle).count(), 1);
    }

    #[test]
    fn folders_that_are_not_prefixes_are_skipped() {
        let t = Tmp::new();
        pgr(&t.dir("home/Library/Application Support/CrossOver/Bottles/Broken/drive_c/Games/PGR"));
        t.dir("home/Applications/Safari.app/Contents/MacOS");
        assert!(scan(&t).is_empty());
        assert!(in_prefix(&t.0, None).is_empty());
    }

    #[test]
    fn picked_prefix_outside_any_app() {
        let t = Tmp::new();
        let p = prefix(&t.dir("somewhere/MyPrefix"));
        pgr(&p.join("drive_c/Games/PGR"));
        let found = in_prefix(&p, None);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].kind, Kind::Wrapper { maker: None });
        assert_eq!(found[0].name, "MyPrefix");
    }

    #[test]
    fn reads_a_plain_launch_script_prefix() {
        let script = "#!/usr/bin/env bash\nset -euo pipefail\nASCNET_PREFIX=\"/Users/x/Applications/Sikarugir/Steam-AscNet.app/Contents/SharedSupport/prefix\"\nexport WINEPREFIX=\"$ASCNET_PREFIX\"\n";
        assert_eq!(script_prefix(script), Some(PathBuf::from("/Users/x/Applications/Sikarugir/Steam-AscNet.app/Contents/SharedSupport/prefix")));
        assert_eq!(script_prefix("WINEPREFIX=${NOPE_UNSET_VAR:-/opt/p}/x\n"), Some(PathBuf::from("/opt/p/x")));
        assert_eq!(script_prefix("export WINEPREFIX=\"$(cat ~/.prefix)\"\n"), None);
        assert_eq!(script_prefix("export WINEPREFIX=\"$NOPE_UNSET_VAR/p\"\n"), None);
        assert_eq!(script_prefix("echo hi\n"), None);
    }

    /// Read-only report on this machine: `cargo test --offline real_machine -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn real_machine_report() {
        let package = crate::local::load_build().ok().flatten().and_then(|b| crate::package::load_package(&b.patch_directory).ok());
        eprintln!("patch package available: {}", package.is_some());
        for c in discover(package.as_ref()) {
            eprintln!("--- {}\n{}", c.display_name(), c.report());
        }
    }
}
