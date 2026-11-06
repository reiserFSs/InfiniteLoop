use crate::package::{client_region, is_cn_layout, compare_versions, sha256_file, validate_server_origin, PatchPackage, Region, KRSDK};
use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::path::{Component, Path, PathBuf};
use std::process::Command;
use uuid::Uuid;

const STATE_DIR: &str = ".ascnet-launcher";
const STATE_FILE: &str = "state.json";
const JOURNAL_FILE: &str = "journal.json";
/// Unmanaged client binaries, validated on disk and tracked in state; the SDK slot is region-specific (`KRSDK`).
const ORIGINAL_KEYS: [&str; 2] = ["PGR.exe", "GameAssembly.dll"];

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PatchState {
    Unpatched,
    Current,
    AdoptionRequired,
    UpdateAvailable,
    Unsupported(String),
    RepairRequired(String),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct State {
    schema_version: u32,
    release_version: String,
    originals: BTreeMap<String, String>,
    files: BTreeMap<String, FileState>,
    /// A leftover `KRSDK.dll` moved out of a CN folder; `restore` puts it back.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    stray: Option<Stray>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Stray {
    sha256: String,
    backup: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FileState {
    original: Option<String>,
    installed: String,
    backup: Option<String>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Journal {
    schema_version: u32,
    files: BTreeMap<String, RollbackFile>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RollbackFile {
    hash: Option<String>,
    backup: Option<String>,
}

#[derive(Deserialize)]
struct LegacyManifest {
    client: String,
    pinned_client: BTreeMap<String, String>,
    files: BTreeMap<String, LegacyFile>,
}
#[derive(Deserialize)]
struct LegacyFile {
    original: Option<String>,
    installed: String,
}

struct PreparedPgrBase {
    original: Vec<u8>,
    patched: Vec<u8>,
    observed: String,
}

/// Global clients ship KRSDK.dll (replaced by the patch); CN ones never have a retail one, so a CN-layout folder's
/// KRSDK.dll is a leftover (`prepare_pgrbase` validated the SDK pair).
fn is_global(client: &Path) -> bool {
    !is_cn_layout(client) && client.join(KRSDK).exists()
}

/// A leftover KRSDK.dll in a CN folder: never retail, removed (and backed up) by install.
fn has_stray(client: &Path) -> bool {
    is_cn_layout(client) && client.join(KRSDK).exists()
}

/// Managed paths whose file is now a stock one the game's updater wrote; restore leaves them alone.
fn restored_paths(manifest: &crate::package::Manifest, client: &Path, state: &State) -> Result<std::collections::BTreeSet<String>> {
    let mut paths = std::collections::BTreeSet::new();
    for (relative, record) in &state.files {
        if game_restored(&manifest, relative, file_hash(&client.join(relative))?.as_deref(), record) {
            paths.insert(relative.clone());
        }
    }
    Ok(paths)
}

/// The game's own updater wrote a stock file over a managed one: neither our patch nor the saved original, but a
/// known stock file. That is a game update, not tampering.
fn game_restored(manifest: &crate::package::Manifest, path: &str, actual: Option<&str>, record: &FileState) -> bool {
    actual != Some(record.installed.as_str()) && actual != record.original.as_deref() && manifest.is_stock(path, actual)
}

fn modified_message(path: &str) -> String {
    format!(
        "managed patch file {path} was modified or removed (it is not a known game or AscNet file). \
         Run Verify & Repair on the game client, then Setup; or delete the {STATE_DIR} folder in the game directory to start over"
    )
}

/// Stock PGRBase.dll candidate for `current`: the first known stock export entry whose recovery hashes into `originals`.
fn recover_stock(build: &crate::package::PgrBaseBuild, current: &[u8], game: &[u8], unity: &[u8]) -> Result<Vec<u8>> {
    use sha2::{Digest, Sha256};
    for jump in &build.original_export_jumps {
        let original = crate::pgrbase::original(current, game, unity, jump)?;
        if build.originals.contains(&format!("{:x}", Sha256::digest(&original))) {
            return Ok(original);
        }
    }
    bail!("unsupported PGRBase.dll: exact stock reconstruction hash does not match")
}

/// Applies the folder's region to the file list (CN drops the KRSDK.dll payload) and adds the generated PGRBase.dll.
fn prepare_pgrbase(client: &Path, package: &PatchPackage) -> Result<(PatchPackage, Option<PreparedPgrBase>)> {
    use sha2::{Digest, Sha256};
    let mut package = package.clone();
    if client_region(&package.manifest.application_version, &package.manifest.originals, client)? == Region::Cn {
        package.manifest.files.retain(|file| file.path != KRSDK);
    }
    let Some(build) = &package.manifest.pgr_base else {
        if read_state(client)?.is_some_and(|state| state.files.contains_key("PGRBase.dll")) {
            bail!("this package cannot update a managed PGRBase startup patch; use restore or a package with verified PGRBase metadata");
        }
        return Ok((package, None));
    };
    let read_client = |relative: &str| -> Result<Vec<u8>> {
        let path = client.join(relative);
        check_target(client, &path)?;
        Ok(fs::read(path)?)
    };
    let game = read_client("PGR.exe")?;
    let observed = format!("{:x}", Sha256::digest(&game));
    if !package.manifest.accepts_original("PGR.exe", Some(&observed)) {
        bail!("{}", crate::package::unsupported_client(&package.manifest.application_version, "PGR.exe", Some(&observed)));
    }
    let unity = read_client("UnityPlayer.dll")?;
    if !build.unity_players.contains(&format!("{:x}", Sha256::digest(&unity))) {
        bail!("unsupported UnityPlayer.dll for PGRBase startup patch");
    }
    let current = read_client("PGRBase.dll")?;
    let observed = format!("{:x}", Sha256::digest(&current));
    if !build.originals.contains(&observed) {
        recover_stock(build, &current, &game, &unity)?;
    }
    // Keep any verified pre-existing Wine NOP; AscNet only installs the startup stub.
    let patched = crate::pgrbase::patch(&current, &game, &unity)?;
    package.manifest.files.push(crate::package::File {
        path: "PGRBase.dll".into(),
        source: "PGRBase.dll".into(),
        sha256: format!("{:x}", Sha256::digest(&patched)),
        size: patched.len() as u64,
    });
    Ok((package, Some(PreparedPgrBase { original: current, patched, observed })))
}

pub fn inspect(client: &Path, package: &PatchPackage) -> Result<PatchState> {
    let client = checked_client(client)?;
    let (package, _) = match prepare_pgrbase(&client, package) {
        Ok(prepared) => prepared,
        Err(error) => return Ok(PatchState::Unsupported(error.to_string())),
    };
    inspect_prepared(&client, &package)
}

fn inspect_prepared(client: &Path, package: &PatchPackage) -> Result<PatchState> {
    if client.join(STATE_DIR).join(JOURNAL_FILE).exists() {
        return Ok(PatchState::RepairRequired(
            "an interrupted transaction must be recovered by install or restore".into(),
        ));
    }
    for key in ORIGINAL_KEYS {
        let actual = file_hash(&client.join(key))?;
        if !package.manifest.accepts_original(key, actual.as_deref()) {
            return Ok(PatchState::Unsupported(crate::package::unsupported_client(&package.manifest.application_version, key, actual.as_deref())));
        }
    }

    if let Some(state) = read_state(&client)? {
        if let Some(base) = state.files.get("PGRBase.dll") {
            let actual = file_hash(&client.join("PGRBase.dll"))?;
            if actual.as_deref() != Some(&base.installed)
                && actual.as_deref() != base.original.as_deref()
                && !game_restored(&package.manifest, "PGRBase.dll", actual.as_deref(), base)
            {
                return Ok(PatchState::RepairRequired(modified_message("PGRBase.dll")));
            }
        }
        if state.schema_version != 1 {
            return Ok(PatchState::RepairRequired(
                "unknown launcher state version".into(),
            ));
        }
        // PGR.exe/GameAssembly.dll are never modified or backed up, only validated on disk
        // above; client updates may replace them, so only KRSDK's saved original must match.
        // A CN client has no KRSDK slot at all: state from the other region is not reusable.
        let global = is_global(client);
        if global != state.originals.contains_key(KRSDK) || (!global && state.files.contains_key(KRSDK)) {
            return Ok(PatchState::RepairRequired("saved launcher state belongs to a different client region".into()));
        }
        // A KRSDK.dll the game's updater restored makes the saved original irrelevant: install saves the new one.
        let krsdk_restored = state
            .files
            .get(KRSDK)
            .is_some_and(|record| game_restored(&package.manifest, KRSDK, file_hash(&client.join(KRSDK)).ok().flatten().as_deref(), record));
        if global
            && !krsdk_restored
            && !package
                .manifest
                .accepts_original(KRSDK, state.originals.get(KRSDK).map(String::as_str))
        {
            return Ok(PatchState::Unsupported(
                "saved retail originals do not match this release".into(),
            ));
        }
        if let Err(error) = verify_saved_backups(&client, &state) {
            return Ok(PatchState::RepairRequired(error.to_string()));
        }
        match compare_versions(&package.manifest.version, &state.release_version) {
            Ok(std::cmp::Ordering::Less) => {
                return Ok(PatchState::Unsupported("release downgrade refused".into()))
            }
            Err(error) => return Ok(PatchState::RepairRequired(error.to_string())),
            _ => {}
        }
        let mut all_target = true;
        let mut all_original = true;
        let mut unknown = None;
        for file in &package.manifest.files {
            let actual = file_hash(&client.join(&file.path))?;
            let old = state.files.get(&file.path);
            let new_base = file.path == "PGRBase.dll" && old.is_none();
            let matches_target = !new_base && actual.as_deref() == Some(&file.sha256);
            let matches_installed = old.is_some_and(|old| actual.as_deref() == Some(&old.installed));
            // A saved original of null means the patch added the file. Matching it means the file is absent.
            // A stock file written by the game's updater is as good as the saved original.
            let matches_original = new_base
                || old.is_some_and(|old| {
                    actual.as_deref() == old.original.as_deref()
                        || game_restored(&package.manifest, &file.path, actual.as_deref(), old)
                });
            all_target &= matches_target;
            all_original &= matches_original;
            // Retail copies and the previous patch are both known bytes. An unknown
            // replacement is not, even when the other files are still intact.
            if !(matches_target || matches_installed || matches_original) && unknown.is_none() {
                unknown = Some(file.path.as_str());
            }
        }
        if all_target {
            return Ok(if has_stray(client) { PatchState::UpdateAvailable } else { PatchState::Current });
        }
        if all_original {
            return Ok(PatchState::Unpatched);
        }
        return Ok(match unknown {
            None => PatchState::UpdateAvailable,
            Some(path) => PatchState::RepairRequired(modified_message(path)),
        });
    }

    if let Some((_path, legacy)) = find_legacy(client, package)? {
        let mut all_target = true;
        let mut all_installed = true;
        let mut all_original = true;
        for file in &package.manifest.files {
            let actual = file_hash(&client.join(&file.path))?;
            let old = legacy.files.get(&file.path);
            let new_base = file.path == "PGRBase.dll" && old.is_none();
            all_target &= !new_base && actual.as_deref() == Some(&file.sha256);
            all_installed &= new_base || old.is_some_and(|old| actual.as_deref() == Some(&old.installed));
            all_original &= new_base || old.is_some_and(|old| actual.as_deref() == old.original.as_deref());
        }
        if all_target {
            return Ok(PatchState::AdoptionRequired);
        }
        if all_original {
            return Ok(PatchState::Unpatched);
        }
        if all_installed {
            return Ok(PatchState::UpdateAvailable);
        }
        return Ok(PatchState::RepairRequired(
            "legacy-managed patch files are inconsistent".into(),
        ));
    }

    let krsdk = KRSDK;
    if is_global(client)
        && !package
            .manifest
            .accepts_original(krsdk, file_hash(&client.join(krsdk))?.as_deref())
    {
        return Ok(PatchState::Unsupported(
            "KRSDK.dll is neither a supported retail original nor a verified managed patch".into(),
        ));
    }
    for file in &package.manifest.files {
        if file.path != krsdk && file.path != "PGRBase.dll" && file_hash(&client.join(&file.path))?.is_some() {
            return Ok(PatchState::Unsupported(format!(
                "unmanaged file would be replaced: {}",
                file.path
            )));
        }
    }
    Ok(PatchState::Unpatched)
}

/// One launcher.log line: the action, the game folder, the detected region (or why detection failed) and the
/// patch state (`managed=` whether launcher state exists when no inspection was made).
pub fn log_action(action: &str, game: &Path, package: Option<&PatchPackage>, patch: Option<&PatchState>) {
    let region = match package {
        Some(p) => match client_region(&p.manifest.application_version, &p.manifest.originals, game) {
            Ok(region) => format!("{region:?}"),
            Err(error) => format!("error: {error:#}"),
        },
        None if is_cn_layout(game) => "CN".into(),
        None if game.join(KRSDK).exists() => "Global".into(),
        None => "unknown".into(),
    };
    let patch = match patch {
        Some(patch) => format!("{patch:?}"),
        None => format!("managed={}", read_state(game).map(|state| state.is_some()).unwrap_or(false)),
    };
    let _ = crate::local::launcher_log(&format!("{action}: game={} region={region} patch={patch}", game.display()));
}

/// Logs the outcome (or the full error chain) of `action`; passes the result through.
pub fn log_result<T>(action: &str, result: Result<T>) -> Result<T> {
    let _ = crate::local::launcher_log(&match &result {
        Ok(_) => format!("{action}: ok"),
        Err(error) => format!("{action}: failed: {error:#}"),
    });
    result
}

/// A declined UAC request is cancellation, not a failed transaction.
#[derive(Debug)]
pub struct ElevationCancelled;
impl std::fmt::Display for ElevationCancelled {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Administrator approval cancelled; game files were not changed")
    }
}
impl std::error::Error for ElevationCancelled {}

pub fn install_with_consent(
    client: &Path,
    package: &PatchPackage,
    progress: &mut dyn FnMut(String),
) -> Result<PathBuf> {
    #[cfg(windows)]
    if inspect(client, package)? == PatchState::Current && crate::access::document_writable(client)? {
        return Ok(client.join(STATE_DIR).join(STATE_FILE));
    }
    #[cfg(windows)]
    if needs_elevation(client)? || !crate::access::document_writable(client)? {
        let expected = package_fingerprint(package)?;
        elevate(client, Some((&package.directory, &expected)), progress)?;
        if inspect(client, package)? != PatchState::Current {
            bail!("elevated installation did not produce the verified prepared patch");
        }
        if !crate::access::document_writable(client)? {
            bail!("resource document directory remains inaccessible to this user after approval");
        }
        return Ok(client.join(STATE_DIR).join(STATE_FILE));
    }
    install(client, package, progress)
}

pub fn restore_with_consent(client: &Path, progress: &mut dyn FnMut(String)) -> Result<()> {
    #[cfg(windows)]
    if needs_elevation(client)? {
        let client = checked_client(client)?;
        let state = read_state(&client)?.context("no launcher-managed installation to restore")?;
        verify_saved_backups(&client, &state)?;
        let restored = restored_paths(&crate::package::Manifest::bundled()?, &client, &state)?;
        elevate(&client, None, progress)?;
        if let Some(stray) = &state.stray {
            if file_hash(&client.join(KRSDK))?.as_deref() != Some(&stray.sha256) {
                bail!("elevated restore did not restore the leftover KRSDK.dll");
            }
        }
        for (relative, record) in state.files {
            if !restored.contains(&relative) && file_hash(&client.join(&relative))? != record.original {
                bail!("elevated restore did not restore the verified original: {relative}");
            }
        }
        if read_state(&client)?.is_some() || client.join(STATE_DIR).join(JOURNAL_FILE).exists() {
            bail!("elevated restore left an incomplete transaction");
        }
        return Ok(());
    }
    restore(client, progress)
}

#[cfg(windows)]
fn package_fingerprint(package: &PatchPackage) -> Result<String> {
    use sha2::{Digest, Sha256};
    Ok(format!("{:x}", Sha256::digest(serde_json::to_vec(&package.manifest)?)))
}

// Opening existing objects with the rights used by atomic replacement does not
// create a probe file or alter retail/state files before the consent prompt.
#[cfg(windows)]
fn needs_elevation(client: &Path) -> Result<bool> {
    use std::os::windows::ffi::OsStrExt;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{CloseHandle, ERROR_ACCESS_DENIED};
    use windows::Win32::Storage::FileSystem::*;
    let client = checked_client(client)?;
    if game_running()? {
        bail!("close PGR.exe before changing game files");
    }
    let mut paths = vec![client.clone()];
    for relative in ["PGR_Data", "PGR_Data/Plugins", STATE_DIR,
        "version.dll", "lucia.dll", "libraries.txt", "PGR_Data/Plugins/KRSDK.dll", "PGRBase.dll"] {
        let path = client.join(relative);
        if path.exists() { paths.push(path); }
    }
    let mut denied = false;
    while let Some(path) = paths.pop() {
        refuse_reparse(&path)?;
        let directory = path.is_dir();
        if directory && path.starts_with(client.join(STATE_DIR)) {
            match fs::read_dir(&path) {
                Ok(entries) => { for entry in entries { paths.push(entry?.path()); } }
                Err(error) if error.kind() == std::io::ErrorKind::PermissionDenied => denied = true,
                Err(error) => return Err(error.into()),
            }
        }
        let name: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        // FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY; existing files need DELETE.
        let access = if directory { 0x6 } else { 0x10000 };
        match unsafe { CreateFileW(PCWSTR(name.as_ptr()), access,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, None,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, None) } {
            Ok(handle) => { unsafe { CloseHandle(handle)?; } }
            Err(error) if error.code() == windows::core::HRESULT::from_win32(ERROR_ACCESS_DENIED.0) => denied = true,
            Err(error) => return Err(error).with_context(|| format!("checking write access to {}", path.display())),
        }
    }
    Ok(denied)
}

#[cfg(windows)]
fn elevate(client: &Path, package: Option<(&Path, &str)>, progress: &mut dyn FnMut(String)) -> Result<()> {
    use std::io::Read;
    use std::net::TcpListener;
    use std::os::windows::ffi::OsStrExt;
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{CloseHandle, ERROR_CANCELLED, WAIT_OBJECT_0};
    use windows::Win32::System::Threading::{GetExitCodeProcess, TerminateProcess, WaitForSingleObject};
    use windows::Win32::UI::Shell::{ShellExecuteExW, SHELLEXECUTEINFOW, SEE_MASK_NOCLOSEPROCESS, SEE_MASK_NOASYNC};
    use windows::Win32::UI::WindowsAndMessaging::SW_HIDE;
    let client = checked_client(client)?;
    let listener = TcpListener::bind(("127.0.0.1", 0))?;
    listener.set_nonblocking(true)?;
    let mut args = vec!["--patch-worker".to_owned(), client.to_string_lossy().into_owned(),
        listener.local_addr()?.port().to_string()];
    if let Some((path, fingerprint)) = package {
        args.push(fs::canonicalize(path)?.to_string_lossy().into_owned());
        args.push(fingerprint.to_owned());
        let (pid, creation) = crate::access::caller_identity()?;
        args.push(pid.to_string());
        args.push(creation.to_string());
    }
    let parameters = args.iter().map(|arg| quote_windows_arg(arg)).collect::<Vec<_>>().join(" ");
    let parameters: Vec<u16> = parameters.encode_utf16().chain(Some(0)).collect();
    let executable: Vec<u16> = std::env::current_exe()?.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC,
        lpVerb: w!("runas"), lpFile: PCWSTR(executable.as_ptr()),
        lpParameters: PCWSTR(parameters.as_ptr()), nShow: SW_HIDE.0,
        ..Default::default()
    };
    progress("Windows administrator approval is required only to change protected game files…".into());
    if let Err(error) = unsafe { ShellExecuteExW(&mut info) } {
        if error.code() == windows::core::HRESULT::from_win32(ERROR_CANCELLED.0) {
            return Err(ElevationCancelled.into());
        }
        return Err(error).context("requesting administrator approval for game files");
    }
    let result = (|| -> Result<()> {
        let mut report = None;
        let started = std::time::Instant::now();
        loop {
            if let Ok((mut stream, _)) = listener.accept() {
                let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(2)));
                let mut bytes = vec![0; 65536];
                if let Ok(count) = stream.read(&mut bytes) {
                    if let Ok(value) = serde_json::from_slice::<std::result::Result<Vec<String>, String>>(&bytes[..count]) {
                        report = Some(value);
                    }
                }
            }
            let wait = unsafe { WaitForSingleObject(info.hProcess, 50) };
            if wait == WAIT_OBJECT_0 { break; }
            if wait != windows::Win32::Foundation::WAIT_TIMEOUT { bail!("waiting for protected-file worker failed"); }
            if started.elapsed() >= std::time::Duration::from_secs(600) {
                unsafe {
                    TerminateProcess(info.hProcess, 1).context("stopping timed-out protected-file worker")?;
                    let _ = WaitForSingleObject(info.hProcess, 30_000);
                }
                bail!("protected game-file operation timed out; any transaction journal is retained for recovery on the next install or restore");
            }
        }
        // The process exit is authoritative; loopback diagnostics alone never
        // establish success. The caller also reinspects every changed game file.
        let mut exit = 1;
        unsafe { GetExitCodeProcess(info.hProcess, &mut exit)?; }
        if report.is_none() {
            if let Ok((mut stream, _)) = listener.accept() {
                let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(2)));
                let mut bytes = vec![0; 65536];
                if let Ok(count) = stream.read(&mut bytes) {
                    report = serde_json::from_slice(&bytes[..count]).ok();
                }
            }
        }
        if exit == 124 {
            bail!("protected game-file operation timed out; any transaction journal is retained for recovery on the next install or restore");
        }
        if exit != 0 {
            if let Some(Err(error)) = report {
                bail!("protected game-file worker exited with code {exit}: {error}");
            }
            bail!("protected game-file worker exited with code {exit}");
        }
        // Diagnostics are unauthenticated and cannot veto a genuine successful
        // exit. Only the caller's independent file inspection establishes success.
        progress("Protected game-file worker finished; verifying game files…".into());
        Ok(())
    })();
    unsafe { let _ = CloseHandle(info.hProcess); }
    result
}

fn quote_windows_arg(value: &str) -> String {
    let mut output = String::from("\"");
    let mut slashes = 0;
    for c in value.chars() {
        if c == '\\' { slashes += 1; continue; }
        output.extend(std::iter::repeat('\\').take(if c == '"' { slashes * 2 + 1 } else { slashes }));
        output.push(c);
        slashes = 0;
    }
    output.extend(std::iter::repeat('\\').take(slashes * 2));
    output.push('"');
    output
}

/// Internal worker: no setup, profile lookup, process launch, or arbitrary command.
#[cfg(windows)]
pub fn patch_worker(args: &[String]) -> i32 {
    use std::net::TcpStream;
    // An unelevated parent may not have termination rights over this process.
    // Self-expiry leaves the durable journal available to the next operation.
    std::thread::spawn(|| {
        std::thread::sleep(std::time::Duration::from_secs(580));
        std::process::exit(124);
    });
    WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = Some(BTreeMap::new()));
    let mut lines = Vec::new();
    let result = (|| -> Result<()> {
        if args.len() != 3 && args.len() != 7 { bail!("invalid patch worker arguments"); }
        let client = Path::new(&args[1]);
        if !client.is_absolute() { bail!("worker game path must be absolute"); }
        let client = checked_client(client)?;
        for name in ["PGR.exe", "GameAssembly.dll"] {
            check_target(&client, &client.join(name))?;
            if !client.join(name).is_file() { bail!("worker requires a PGR game directory"); }
        }
        // Restrict even persisted recovery/state input to the patch's fixed write set.
        validate_worker_state(&client)?;
        if args.len() == 7 {
            let directory = Path::new(&args[3]);
            if !directory.is_absolute() { bail!("worker package path must be absolute"); }
            refuse_ancestor_reparse(directory)?;
            let package = crate::package::load_package(directory)?;
            if package_fingerprint(&package)? != args[4] { bail!("prepared package changed before approval"); }
            install(&client, &package, &mut |line| lines.push(line))?;
            crate::access::prepare_document_for_caller(&client, args[5].parse()?, args[6].parse()?)?;
        } else {
            restore(&client, &mut |line| lines.push(line))?;
        }
        Ok(())
    })();
    WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = None);
    let exit = if result.is_ok() { 0 } else { 1 };
    let report = result.map(|()| lines).map_err(|error| format!("{error:#}"));
    if let Some(port) = args.get(2).and_then(|port| port.parse::<u16>().ok()) {
        if let Ok(mut stream) = TcpStream::connect(("127.0.0.1", port)) {
            let _ = stream.set_write_timeout(Some(std::time::Duration::from_secs(2)));
            if let Ok(bytes) = serde_json::to_vec(&report) {
                let _ = stream.write_all(&bytes);
            }
        }
    }
    exit
}

#[cfg(windows)]
fn validate_worker_state(client: &Path) -> Result<()> {
    let allowed = ["version.dll", "lucia.dll", "libraries.txt", "PGR_Data/Plugins/KRSDK.dll", "PGRBase.dll"];
    let root = client.join(STATE_DIR);
    if root.exists() {
        let mut paths = vec![root.clone()];
        while let Some(path) = paths.pop() {
            refuse_reparse(&path)?;
            if path.is_dir() {
                pin_worker_directory(&path)?;
                for entry in fs::read_dir(path)? { paths.push(entry?.path()); }
            }
        }
    }
    if let Some(state) = read_state(client)? {
        if state.files.keys().any(|key| !allowed.contains(&key.as_str())) {
            bail!("saved state contains a destination outside the patch write set");
        }
    }
    let journal = root.join(JOURNAL_FILE);
    if journal.exists() {
        let journal: Journal = serde_json::from_reader(File::open(journal)?)?;
        if journal.files.keys().any(|key| !allowed.contains(&key.as_str()) && key != ".ascnet-launcher/state.json") {
            bail!("recovery journal contains a destination outside the patch write set");
        }
    }
    Ok(())
}

pub fn install(
    client: &Path,
    package: &PatchPackage,
    progress: &mut dyn FnMut(String),
) -> Result<PathBuf> {
    let _lock = OperationLock::acquire()?;
    if game_running()? {
        bail!("refusing to install while PGR.exe is running");
    }
    let client = checked_client(client)?;
    recover_if_needed(&client)?;
    validate_package_paths(package)?;
    let (prepared_package, pgr_base) = prepare_pgrbase(&client, package)?;
    let package = &prepared_package;
    let observed = inspect_prepared(&client, package)?;
    if let PatchState::Unsupported(reason) | PatchState::RepairRequired(reason) = &observed {
        bail!("refusing installation: {reason}");
    }
    if observed == PatchState::Current {
        return Ok(client.join(STATE_DIR).join(STATE_FILE));
    }

    let state_root = client.join(STATE_DIR);
    create_private_dir(&state_root)?;
    refuse_reparse(&state_root)?;

    let prior = match read_state(&client)? {
        Some(state) => {
            verify_saved_backups(&client, &state)?;
            Some(state)
        }
        None => adopt_legacy(&client, package)?,
    };
    if let Some(old) = &prior {
        if old.release_version != "legacy"
            && compare_versions(&package.manifest.version, &old.release_version)?
                == std::cmp::Ordering::Less
        {
            bail!("release downgrade refused");
        }
    }
    if observed == PatchState::AdoptionRequired {
        let mut adopted = prior.context("verified legacy backup disappeared during adoption")?;
        adopted.release_version = package.manifest.version.clone();
        for key in ORIGINAL_KEYS {
            adopted.originals.insert(key.into(), sha256_file(&client.join(key))?);
        }
        for file in &package.manifest.files {
            adopted
                .files
                .get_mut(&file.path)
                .context("legacy backup does not track every release file")?
                .installed = file.sha256.clone();
        }
        verify_saved_backups(&client, &adopted)?;
        let state_path = state_root.join(STATE_FILE);
        write_json_atomic(&state_path, &adopted)?;
        progress("Adopted verified existing patch".into());
        return Ok(state_path);
    }
    let mut originals = prior.as_ref().map(|state| state.originals.clone()).unwrap_or_default();
    for key in ORIGINAL_KEYS {
        originals.insert(key.into(), sha256_file(&client.join(key))?);
    }
    if is_global(&client) && !originals.contains_key(KRSDK) {
        // Saved KRSDK original is preserved; unmanaged client binaries track the verified disk copy.
        originals.insert(KRSDK.into(), sha256_file(&client.join(KRSDK))?);
    }
    let id = Uuid::new_v4().to_string();
    let backup_parent = state_root.join("backups");
    create_private_dir(&backup_parent)?;
    let backup_root = backup_parent.join(&id);
    create_private_dir(&backup_root)?;
    let mut files = BTreeMap::new();
    let stray_target = client.join(KRSDK);
    let remove_stray = has_stray(&client);
    let mut stray = prior.as_ref().and_then(|state| state.stray.clone());
    if remove_stray {
        check_target(&client, &stray_target)?;
        let hash = sha256_file(&stray_target)?;
        if stray.as_ref().map(|s| &s.sha256) != Some(&hash) {
            let dest = backup_root.join(KRSDK);
            atomic_copy(&stray_target, &dest)?;
            if sha256_file(&dest)? != hash {
                bail!("backup verification failed: {KRSDK}");
            }
            stray = Some(Stray { sha256: hash, backup: format!("backups/{id}/{KRSDK}") });
        }
    }
    for file in &package.manifest.files {
        let target = client.join(&file.path);
        check_target(&client, &target)?;
        let actual = file_hash(&target)?;
        // A stock file the game's updater wrote over the patch is backed up as the new original below.
        let old = prior
            .as_ref()
            .and_then(|s| s.files.get(&file.path))
            .filter(|old| !game_restored(&package.manifest, &file.path, actual.as_deref(), old));
        let (original, backup) = if let Some(old) = old {
            if let Some(rel) = &old.backup {
                let source = client.join(STATE_DIR).join(rel);
                let dest = backup_root.join(&file.path);
                atomic_copy(&source, &dest)?;
                if file_hash(&dest)?.as_deref() != old.original.as_deref() {
                    bail!("saved original changed: {}", file.path);
                }
                (
                    old.original.clone(),
                    Some(format!("backups/{id}/{}", file.path.replace('\\', "/"))),
                )
            } else {
                (None, None)
            }
        } else if file.path == "PGRBase.dll" && pgr_base.is_some() {
            let prepared = pgr_base.as_ref().unwrap();
            if file_hash(&target)?.as_deref() != Some(&prepared.observed) {
                bail!("PGRBase.dll changed during preparation");
            }
            let dest = backup_root.join(&file.path);
            check_target(&client, &dest)?;
            fs::write(&dest, &prepared.original)?;
            (
                Some(sha256_file(&dest)?),
                Some(format!("backups/{id}/{}", file.path)),
            )
        } else if target.is_file() {
            let hash = sha256_file(&target)?;
            let dest = backup_root.join(&file.path);
            atomic_copy(&target, &dest)?;
            if sha256_file(&dest)? != hash {
                bail!("backup verification failed: {}", file.path);
            }
            (
                Some(hash),
                Some(format!("backups/{id}/{}", file.path.replace('\\', "/"))),
            )
        } else {
            (None, None)
        };
        files.insert(
            file.path.clone(),
            FileState {
                original,
                installed: file.sha256.clone(),
                backup,
            },
        );
    }
    if let Some(FileState { original: Some(hash), .. }) = files.get(KRSDK) {
        originals.insert(KRSDK.into(), hash.clone());
    }
    let state = State {
        schema_version: 1,
        release_version: package.manifest.version.clone(),
        originals,
        files,
        stray,
    };
    verify_saved_backups_at(&client, &state, &state_root)?;

    let rollback_root = state_root.join("rollback").join(&id);
    create_private_dir(&state_root.join("rollback"))?;
    create_private_dir(&rollback_root)?;
    let mut rollback = BTreeMap::new();
    let generated_base = rollback_root.join("PGRBase.payload");
    if let Some(prepared) = &pgr_base {
        check_target(&client, &generated_base)?;
        fs::write(&generated_base, &prepared.patched)?;
    }
    snapshot_for_rollback(
        &client,
        &state_root,
        &rollback_root,
        &format!("{STATE_DIR}/{STATE_FILE}"),
        &mut rollback,
    )?;
    for path in package.manifest.files.iter().map(|file| file.path.as_str()).chain(remove_stray.then_some(KRSDK)) {
        let target = client.join(path);
        if target.is_file() {
            let hash = sha256_file(&target)?;
            let saved = rollback_root.join(path);
            atomic_copy(&target, &saved)?;
            rollback.insert(
                path.to_owned(),
                RollbackFile {
                    hash: Some(hash),
                    backup: Some(format!("rollback/{id}/{}", path.replace('\\', "/"))),
                },
            );
        } else {
            rollback.insert(
                path.to_owned(),
                RollbackFile {
                    hash: None,
                    backup: None,
                },
            );
        }
    }
    write_json_atomic(
        &state_root.join(JOURNAL_FILE),
        &Journal {
            schema_version: 1,
            files: rollback,
        },
    )?;

    let result = (|| -> Result<()> {
        for file in &package.manifest.files {
            let source = if file.path == "PGRBase.dll" && pgr_base.is_some() {
                generated_base.clone()
            } else {
                package.directory.join(&file.source)
            };
            refuse_reparse(&source)?;
            if sha256_file(&source)? != file.sha256 {
                bail!("release payload changed after verification: {}", file.path);
            }
            let target = client.join(&file.path);
            check_target(&client, &target)?;
            if file.path == "PGRBase.dll" {
                if let Some(base) = &pgr_base {
                    if file_hash(&target)?.as_deref() != Some(&base.observed) {
                        bail!("PGRBase.dll changed before installation");
                    }
                }
            }
            progress(format!("Installing {}", file.path));
            atomic_copy(&source, &target)?;
            if sha256_file(&target)? != file.sha256 {
                bail!("installed-file verification failed: {}", file.path);
            }
        }
        if remove_stray {
            fs::remove_file(&stray_target)?;
            sync_dir(stray_target.parent().unwrap())?;
        }
        write_json_atomic(&state_root.join(STATE_FILE), &state)?;
        Ok(())
    })();
    if let Err(error) = result {
        recover_if_needed(&client).context("installation failed and rollback failed")?;
        return Err(error);
    }
    fs::remove_file(state_root.join(JOURNAL_FILE))?;
    remove_rollback_directory(&rollback_root);
    sync_dir(&state_root)?;
    if let (true, Some(stray)) = (remove_stray, &state.stray) {
        progress(format!(
            "Removed leftover KRSDK.dll from a CN client (backed up to {})",
            state_root.join(&stray.backup).display()
        ));
    }
    Ok(state_root.join(STATE_FILE))
}

pub fn restore(client: &Path, progress: &mut dyn FnMut(String)) -> Result<()> {
    restore_with(client, progress, crate::package::Manifest::bundled()?)
}

/// `stock` names the files the game's updater may legitimately have written over managed ones.
fn restore_with(client: &Path, progress: &mut dyn FnMut(String), stock: crate::package::Manifest) -> Result<()> {
    let _lock = OperationLock::acquire()?;
    if game_running()? {
        bail!("refusing to restore while PGR.exe is running");
    }
    let client = checked_client(client)?;
    recover_if_needed(&client)?;
    let state = read_state(&client)?.context("no launcher-managed installation to restore")?;
    verify_saved_backups(&client, &state)?;
    let restored = restored_paths(&stock, &client, &state)?;
    for (relative, record) in &state.files {
        let target = client.join(relative);
        check_target(&client, &target)?;
        let actual = file_hash(&target)?;
        if actual.as_deref() != Some(&record.installed)
            && actual.as_deref() != record.original.as_deref()
            && !restored.contains(relative)
        {
            bail!(
                "refusing to overwrite modified managed file: {relative} (not a known game or AscNet file). \
                 Run Verify & Repair on the game client, then Setup; or delete the {STATE_DIR} folder in the game directory to start over"
            );
        }
    }
    if let Some(stray) = &state.stray {
        let target = client.join(KRSDK);
        check_target(&client, &target)?;
        if file_hash(&target)?.is_some_and(|hash| hash != stray.sha256) {
            bail!("refusing to overwrite a different {KRSDK}");
        }
    }
    let state_root = client.join(STATE_DIR);
    let id = Uuid::new_v4().to_string();
    let rollback_root = state_root.join("rollback").join(&id);
    create_private_dir(&state_root.join("rollback"))?;
    create_private_dir(&rollback_root)?;
    let mut rollback = BTreeMap::new();
    snapshot_for_rollback(
        &client,
        &state_root,
        &rollback_root,
        &format!("{STATE_DIR}/{STATE_FILE}"),
        &mut rollback,
    )?;
    for relative in state.files.keys().map(String::as_str).chain(state.stray.as_ref().map(|_| KRSDK)) {
        let target = client.join(relative);
        if target.is_file() {
            let saved = rollback_root.join(relative);
            atomic_copy(&target, &saved)?;
            rollback.insert(
                relative.to_owned(),
                RollbackFile {
                    hash: Some(sha256_file(&target)?),
                    backup: Some(format!("rollback/{id}/{}", relative.replace('\\', "/"))),
                },
            );
        } else {
            rollback.insert(
                relative.to_owned(),
                RollbackFile {
                    hash: None,
                    backup: None,
                },
            );
        }
    }
    write_json_atomic(
        &state_root.join(JOURNAL_FILE),
        &Journal {
            schema_version: 1,
            files: rollback,
        },
    )?;
    let result = (|| -> Result<()> {
        for (relative, record) in &state.files {
            if restored.contains(relative) {
                continue;
            }
            progress(format!("Restoring {relative}"));
            let target = client.join(relative);
            if let Some(backup) = &record.backup {
                atomic_copy(&state_root.join(backup), &target)?;
            } else if target.exists() {
                fs::remove_file(&target)?;
                sync_dir(target.parent().unwrap())?;
            }
        }
        if let Some(stray) = &state.stray {
            progress(format!("Restoring leftover {KRSDK}"));
            atomic_copy(&state_root.join(&stray.backup), &client.join(KRSDK))?;
        }
        fs::remove_file(state_root.join(STATE_FILE))?;
        sync_dir(&state_root)?;
        Ok(())
    })();
    if let Err(error) = result {
        recover_if_needed(&client).context("restore failed and rollback failed")?;
        return Err(error);
    }
    fs::remove_file(state_root.join(JOURNAL_FILE))?;
    remove_rollback_directory(&rollback_root);
    Ok(())
}

fn remove_rollback_directory(path: &Path) {
    #[cfg(windows)]
    WORKER_DIRECTORIES.with(|pins| {
        if let Some(pins) = pins.borrow_mut().as_mut() {
            pins.retain(|directory, _| !directory.starts_with(path));
        }
    });
    // std's remove_dir_all does not follow directory links. Release only this
    // finished rollback subtree; its ancestors and live transaction stay pinned.
    let _ = fs::remove_dir_all(path);
}

/// Whether a `PGR.exe` process exists. A ToolHelp process snapshot: cheap enough for the UI thread to poll
/// (the launcher's music follows the game), unlike spawning `tasklist.exe`.
pub fn game_running() -> Result<bool> {
    #[cfg(windows)]
    {
        use windows::Win32::Foundation::{CloseHandle, ERROR_NO_MORE_FILES, HANDLE};
        use windows::Win32::System::Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
        };
        struct Snapshot(HANDLE);
        impl Drop for Snapshot {
            fn drop(&mut self) {
                unsafe {
                    let _ = CloseHandle(self.0);
                }
            }
        }
        let snapshot = Snapshot(
            unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }.context("querying running processes")?,
        );
        let mut entry = PROCESSENTRY32W { dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32, ..Default::default() };
        let mut next = unsafe { Process32FirstW(snapshot.0, &mut entry) };
        loop {
            match next {
                Ok(()) => {
                    let length = entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(entry.szExeFile.len());
                    if String::from_utf16_lossy(&entry.szExeFile[..length]).eq_ignore_ascii_case("PGR.exe") {
                        return Ok(true);
                    }
                }
                Err(e) if e.code() == ERROR_NO_MORE_FILES.to_hresult() => return Ok(false),
                Err(e) => return Err(e).context("listing running processes; refusing to assume the game is stopped"),
            }
            next = unsafe { Process32NextW(snapshot.0, &mut entry) };
        }
    }
    #[cfg(not(windows))]
    {
        Ok(false)
    }
}

pub fn launch(client: &Path, origin: &str) -> Result<()> {
    let _lock = OperationLock::acquire()?;
    if game_running()? {
        bail!("PGR.exe is already running");
    }
    let client = checked_client(client)?;
    let origin = validate_server_origin(origin)?;
    let executable = client.join("PGR.exe");
    if !executable.is_file() {
        bail!("PGR.exe is missing");
    }
    let mut command = launch_command(&client, executable, origin);
    if running_under_wine() {
        let old = std::env::var("WINEDLLOVERRIDES").unwrap_or_default();
        command.env("WINEDLLOVERRIDES", merge_version_dll_override(&old));
    }
    command.spawn().context("launching PGR.exe")?;
    Ok(())
}

/// Region-neutral: every supported client (EN/TW/KR/JP/CN) gets the same environment (CN's gate path comes from AscNet's config.tab).
/// FPS / no-camera-fade live in `ascnet-graphics.ini`, not in the environment.
fn launch_command(client: &Path, executable: PathBuf, origin: String) -> Command {
    let mut command = Command::new(executable);
    command.current_dir(client).env("ASCNET_PATCH_ORIGIN", origin);
    for key in [
        "ASCNET_PATCH_TRACE",
        "ASCNET_PATCH_PROBE",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "no_proxy",
    ] {
        command.env_remove(key);
    }
    command
}

/// Keep an existing Wine DLL override list and make sure `version.dll` loads beside the game.
pub(crate) fn merge_version_dll_override(current: &str) -> String {
    let mut parts: Vec<String> = current
        .split(';')
        .map(str::trim)
        .filter(|part| !part.is_empty())
        .map(str::to_owned)
        .collect();
    let has_version = parts.iter().any(|part| {
        part.split('=')
            .next()
            .unwrap_or("")
            .split(',')
            .any(|name| name.trim().eq_ignore_ascii_case("version"))
    });
    if !has_version {
        parts.push("version=n,b".to_owned());
    }
    parts.join(";")
}

#[cfg(windows)]
pub(crate) fn running_under_wine() -> bool {
    use windows::core::{s, w};
    use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};

    unsafe {
        GetModuleHandleW(w!("ntdll.dll"))
            .ok()
            .and_then(|module| GetProcAddress(module, s!("wine_get_version")))
            .is_some()
    }
}

#[cfg(not(windows))]
pub(crate) fn running_under_wine() -> bool {
    false
}

/// `\\?\Z:\home` and `Z:\home` name one directory. A junction's target does not.
#[cfg_attr(not(any(windows, test)), allow(dead_code))]
fn windows_paths_match(left: &str, right: &str) -> bool {
    let left = normalize_windows_path(left);
    let right = normalize_windows_path(right);
    !left.is_empty() && left == right
}

#[cfg_attr(not(any(windows, test)), allow(dead_code))]
fn normalize_windows_path(value: &str) -> String {
    let value = value.replace('/', "\\").to_ascii_lowercase();
    let value = if let Some(rest) = value.strip_prefix("\\\\?\\unc\\") {
        format!("\\\\{rest}")
    } else if let Some(rest) = value.strip_prefix("\\\\?\\") {
        rest.to_string()
    } else {
        value
    };
    // `z:\` is the drive root. Every other trailing slash is not part of the name.
    if value.len() > 3 && value.ends_with('\\') {
        value.trim_end_matches('\\').to_string()
    } else {
        value
    }
}

/// `FILE_ATTRIBUTE_DIRECTORY` is 0x10 and `FILE_ATTRIBUTE_REPARSE_POINT` is 0x400.
/// `wine_mount` is the result of checking the path before the pin is taken.
/// The opened handle has to pass the same test: a directory swapped for a
/// junction still has the directory attribute.
#[cfg_attr(not(any(windows, test)), allow(dead_code))]
pub(crate) fn directory_pin_allows(attributes: u32, wine_mount: bool) -> bool {
    const DIRECTORY: u32 = 0x10;
    const REPARSE: u32 = 0x400;
    attributes & DIRECTORY != 0 && (attributes & REPARSE == 0 || wine_mount)
}

/// Proton bind-mounts `/home` at `Z:\home`. Wine reports that directory as a
/// junction. Rust then reports it as a symlink, so `is_dir()` is false even
/// though the directory attribute is set. Opening it with and without
/// `FILE_FLAG_OPEN_REPARSE_POINT` returns the same path. A junction that points
/// somewhere else returns its target only when the reparse point is followed.
#[cfg(windows)]
pub(crate) fn wine_mount_keeps_its_path(path: &Path) -> bool {
    if !running_under_wine() {
        return false;
    }
    let Ok(metadata) = fs::symlink_metadata(path) else { return false };
    use std::os::windows::fs::MetadataExt;
    // `FILE_ATTRIBUTE_DIRECTORY`. Do not use `is_dir()`: it is false for junctions.
    if metadata.file_attributes() & 0x10 == 0 {
        return false;
    }
    match (final_dos_path(path, false), final_dos_path(path, true)) {
        (Ok(followed), Ok(opened)) => windows_paths_match(&followed, &opened),
        _ => false,
    }
}

#[cfg(windows)]
fn final_dos_path(path: &Path, open_reparse: bool) -> std::io::Result<String> {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::Storage::FileSystem::{
        GetFinalPathNameByHandleW, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
        FILE_READ_ATTRIBUTES, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, VOLUME_NAME_DOS,
    };
    let mut flags = FILE_FLAG_BACKUP_SEMANTICS.0;
    if open_reparse {
        flags |= FILE_FLAG_OPEN_REPARSE_POINT.0;
    }
    let file = OpenOptions::new()
        .access_mode(FILE_READ_ATTRIBUTES.0)
        .share_mode(FILE_SHARE_READ.0 | FILE_SHARE_WRITE.0 | FILE_SHARE_DELETE.0)
        .custom_flags(flags)
        .open(path)?;
    let handle = windows::Win32::Foundation::HANDLE(file.as_raw_handle() as isize);
    let mut buffer = vec![0u16; 260];
    loop {
        let length = unsafe { GetFinalPathNameByHandleW(handle, &mut buffer[..], VOLUME_NAME_DOS) };
        if length == 0 {
            return Err(std::io::Error::last_os_error());
        }
        if (length as usize) < buffer.len() {
            return Ok(String::from_utf16_lossy(&buffer[..length as usize]));
        }
        buffer.resize(length as usize, 0);
    }
}

fn checked_client(client: &Path) -> Result<PathBuf> {
    #[cfg(windows)]
    {
        let absolute = std::path::absolute(client)?;
        pin_worker_directory(&absolute)?;
        refuse_ancestor_reparse(&absolute)?;
    }
    let client = fs::canonicalize(client)
        .with_context(|| format!("invalid game directory: {}", client.display()))?;
    if !client.is_dir() {
        bail!("game path is not a directory");
    }
    refuse_reparse(&client)?;
    Ok(client)
}

#[cfg(windows)]
thread_local! {
    // Holding directory handles without WRITE/DELETE sharing prevents both
    // junction conversion and ancestor replacement throughout elevated writes.
    static WORKER_DIRECTORIES: std::cell::RefCell<Option<BTreeMap<PathBuf, File>>> =
        const { std::cell::RefCell::new(None) };
}

#[cfg(windows)]
fn pin_worker_directory(path: &Path) -> Result<()> {
    use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
    use windows::Win32::Storage::FileSystem::{
        FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ,
    };
    WORKER_DIRECTORIES.with(|pins| -> Result<()> {
        let mut pins = pins.borrow_mut();
        let Some(pins) = pins.as_mut() else { return Ok(()); };
        let mut ancestors: Vec<_> = path.ancestors().collect();
        ancestors.reverse();
        for directory in ancestors {
            if pins.contains_key(directory) { continue; }
            // Decide before taking the share-read pin. That pin denies a later open.
            let metadata = fs::symlink_metadata(directory)
                .with_context(|| format!("locking protected write directory {}", directory.display()))?;
            let attributes = metadata.file_attributes();
            // Decide before taking the share-read pin. That pin denies a later open.
            // `is_dir()` is false for a junction. The directory attribute is what matters.
            let wine_mount = attributes & 0x400 != 0 && wine_mount_keeps_its_path(directory);
            if !directory_pin_allows(attributes, wine_mount) {
                bail!("refusing link/reparse write directory: {}", directory.display());
            }
            // Metadata-only opens do not participate in Windows share checks.
            let handle = OpenOptions::new().read(true)
                .share_mode(FILE_SHARE_READ.0)
                .custom_flags(FILE_FLAG_BACKUP_SEMANTICS.0 | FILE_FLAG_OPEN_REPARSE_POINT.0)
                .open(directory).with_context(|| format!("locking protected write directory {}", directory.display()))?;
            let pinned = handle.metadata()
                .with_context(|| format!("locking protected write directory {}", directory.display()))?
                .file_attributes();
            if !directory_pin_allows(pinned, wine_mount) {
                bail!("refusing link/reparse write directory: {}", directory.display());
            }
            pins.insert(directory.to_path_buf(), handle);
        }
        Ok(())
    })
}

#[cfg(windows)]
fn refuse_ancestor_reparse(path: &Path) -> Result<()> {
    for ancestor in path.ancestors() {
        if ancestor.as_os_str().is_empty() { continue; }
        if ancestor_redirects(ancestor)? {
            bail!("refusing link/reparse path: {}", ancestor.display());
        }
    }
    Ok(())
}

/// A symlink or a junction whose target is a different path. Wine's own mount
/// junctions (`Z:\`, `Z:\home`) are not redirects.
#[cfg(windows)]
fn ancestor_redirects(path: &Path) -> Result<bool> {
    let metadata = fs::symlink_metadata(path)?;
    let reparse = metadata.file_type().is_symlink() || {
        use std::os::windows::fs::MetadataExt;
        metadata.file_attributes() & 0x400 != 0
    };
    if !reparse {
        return Ok(false);
    }
    Ok(!wine_mount_keeps_its_path(path))
}

fn validate_package_paths(package: &PatchPackage) -> Result<()> {
    for file in &package.manifest.files {
        validate_relative(&file.path)?;
        validate_relative(&file.source)?;
        let source = package.directory.join(&file.source);
        check_contained(&package.directory, &source)?;
    }
    Ok(())
}
fn validate_relative(value: &str) -> Result<()> {
    let path = Path::new(value);
    if value.contains('\\')
        || path
            .components()
            .any(|c| !matches!(c, Component::Normal(_)))
    {
        bail!("unsafe relative path: {value}");
    }
    Ok(())
}
fn check_target(client: &Path, target: &Path) -> Result<()> {
    check_contained(client, target)?;
    let mut cursor = client.to_path_buf();
    let relative = target.strip_prefix(client)?;
    #[cfg(windows)]
    pin_worker_directory(target.parent().context("target has no parent")?)?;
    for part in relative.components() {
        cursor.push(part);
        if cursor.exists() {
            refuse_reparse(&cursor)?;
        }
    }
    if target.exists() && !target.is_file() {
        bail!("refusing non-regular destination: {}", target.display());
    }
    Ok(())
}
fn check_contained(root: &Path, path: &Path) -> Result<()> {
    if !path.starts_with(root) {
        bail!("path escapes trusted root: {}", path.display());
    }
    Ok(())
}
fn refuse_reparse(path: &Path) -> Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() {
        bail!("refusing link/reparse path: {}", path.display());
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            bail!("refusing link/reparse path: {}", path.display());
        }
    }
    Ok(())
}
fn file_hash(path: &Path) -> Result<Option<String>> {
    if !path.exists() {
        return Ok(None);
    }
    refuse_reparse(path)?;
    if !path.is_file() {
        bail!("not a regular file: {}", path.display());
    }
    Ok(Some(sha256_file(path)?))
}
fn create_private_dir(path: &Path) -> Result<()> {
    #[cfg(windows)]
    if WORKER_DIRECTORIES.with(|pins| pins.borrow().is_some()) {
        let path = std::path::absolute(path)?;
        let mut ancestors: Vec<_> = path.ancestors().collect();
        ancestors.reverse();
        for directory in ancestors {
            if !directory.exists() { fs::create_dir(directory)?; }
            pin_worker_directory(directory)?;
        }
        return Ok(());
    }
    fs::create_dir_all(path).with_context(|| format!("creating {}", path.display()))
}

fn atomic_copy(source: &Path, destination: &Path) -> Result<()> {
    let parent = destination.parent().context("destination has no parent")?;
    create_private_dir(parent)?;
    let temporary = parent.join(format!(
        ".{}.{}.tmp",
        destination.file_name().unwrap().to_string_lossy(),
        Uuid::new_v4()
    ));
    let result = (|| -> Result<()> {
        let mut input = open_copy_source(source)?;
        let mut output = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        std::io::copy(&mut input, &mut output)?;
        output.sync_all()?;
        drop(output);
        atomic_replace(&temporary, destination)
            .with_context(|| format!("committing {}", destination.display()))?;
        sync_dir(parent)?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}
fn open_copy_source(path: &Path) -> Result<File> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
        use windows::Win32::Storage::FileSystem::{FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ};
        pin_worker_directory(path.parent().context("copy source has no parent")?)?;
        let file = OpenOptions::new().read(true).share_mode(FILE_SHARE_READ.0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT.0).open(path)?;
        let metadata = file.metadata()?;
        if !metadata.is_file() || metadata.file_attributes() & 0x400 != 0 {
            bail!("refusing non-regular copy source: {}", path.display());
        }
        Ok(file)
    }
    #[cfg(not(windows))]
    { Ok(File::open(path)?) }
}

fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    #[cfg(windows)]
    {
        use std::os::windows::{ffi::OsStrExt, fs::OpenOptionsExt, io::AsRawHandle};
        use windows::Wdk::Storage::FileSystem::{
            NtSetInformationFile, FileRenameInformation, FILE_RENAME_INFORMATION,
        };
        use windows::core::PCWSTR;
        use windows::Win32::Foundation::{HANDLE, STATUS_OBJECT_PATH_SYNTAX_BAD};
        use windows::Win32::Storage::FileSystem::{
            DELETE, SYNCHRONIZE, FILE_FLAG_OPEN_REPARSE_POINT,
            MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
        };
        use windows::Win32::System::IO::IO_STATUS_BLOCK;

        // All callers stage a sibling. A full destination path makes Windows
        // reopen its parent for FILE_WRITE_DATA, conflicting with our directory
        // pin. A native bare-name rename stays in the source's pinned directory.
        anyhow::ensure!(source.parent() == destination.parent(), "atomic replacement requires sibling paths");
        let name: Vec<u16> = destination.file_name().context("destination has no filename")?
            .encode_wide().collect();
        let bytes = std::mem::size_of::<FILE_RENAME_INFORMATION>() + name.len() * 2;
        // usize storage provides the alignment required by the trailing-array ABI.
        let mut storage = vec![0usize; bytes.div_ceil(std::mem::size_of::<usize>())];
        let info = storage.as_mut_ptr().cast::<FILE_RENAME_INFORMATION>();
        let file = OpenOptions::new().access_mode((DELETE | SYNCHRONIZE).0)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT.0).open(source)?;
        let mut status = IO_STATUS_BLOCK::default();
        let renamed = unsafe {
            (*info).Anonymous.ReplaceIfExists = true.into();
            (*info).FileNameLength = (name.len() * 2).try_into()?;
            std::ptr::copy_nonoverlapping(name.as_ptr(), (*info).FileName.as_mut_ptr(), name.len());
            NtSetInformationFile(
                HANDLE(file.as_raw_handle() as isize), &mut status, info.cast(), bytes.try_into()?,
                FileRenameInformation,
            )
        };
        if renamed != STATUS_OBJECT_PATH_SYNTAX_BAD {
            return renamed.ok().with_context(|| format!("atomically replacing {}", destination.display()));
        }
        // Wine does not implement NULL-root bare-name renames. Its path-based
        // rename works with the same pins; never fall back on a sharing failure.
        drop(file);
        let src: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
        let dst: Vec<u16> = destination.as_os_str().encode_wide().chain(Some(0)).collect();
        unsafe {
            MoveFileExW(
                PCWSTR(src.as_ptr()), PCWSTR(dst.as_ptr()),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            ).with_context(|| format!("atomically replacing {}", destination.display()))
        }
    }
    #[cfg(not(windows))]
    {
        fs::rename(source, destination).map_err(Into::into)
    }
}
#[cfg(not(windows))]
fn sync_dir(path: &Path) -> Result<()> {
    File::open(path)
        .with_context(|| format!("opening directory for durability sync: {}", path.display()))?
        .sync_all()
        .with_context(|| format!("syncing directory: {}", path.display()))
}
#[cfg(windows)]
fn sync_dir(_path: &Path) -> Result<()> {
    // File contents are flushed before the atomic, same-directory Windows rename.
    // Directory fsync is not available through std::fs::File on Windows.
    Ok(())
}
fn write_json_atomic<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    let parent = path.parent().unwrap();
    create_private_dir(parent)?;
    let temp = parent.join(format!(
        ".{}.{}.tmp",
        path.file_name().unwrap().to_string_lossy(),
        Uuid::new_v4()
    ));
    let result = (|| -> Result<()> {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)?;
        serde_json::to_writer_pretty(&mut file, value)?;
        file.write_all(b"\n")?;
        file.sync_all()?;
        drop(file);
        atomic_replace(&temp, path)?;
        sync_dir(parent)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temp);
    }
    result
}
fn read_state(client: &Path) -> Result<Option<State>> {
    let path = client.join(STATE_DIR).join(STATE_FILE);
    if !path.exists() {
        return Ok(None);
    }
    refuse_reparse(&path)?;
    Ok(Some(
        serde_json::from_reader(File::open(path)?).context("invalid launcher state")?,
    ))
}
/// Files this launcher patched in `client` (relative path -> installed SHA-256, plus whether a transaction
/// journal is pending). Lets the official-client downloader keep patched files instead of "repairing" them.
pub fn managed_files(client: &Path) -> Result<(BTreeMap<String, String>, bool)> {
    let pending = client.join(STATE_DIR).join(JOURNAL_FILE).exists();
    let files = read_state(client)?
        .map(|state| state.files.into_iter().map(|(path, file)| (path, file.installed)).collect())
        .unwrap_or_default();
    Ok((files, pending))
}
fn verify_saved_backups(client: &Path, state: &State) -> Result<()> {
    verify_saved_backups_at(client, state, &client.join(STATE_DIR))
}
fn verify_saved_backups_at(client: &Path, state: &State, root: &Path) -> Result<()> {
    for (relative, record) in &state.files {
        validate_relative(relative)?;
        match (&record.original, &record.backup) {
            (Some(expected), Some(backup)) => {
                validate_relative(backup)?;
                let path = root.join(backup);
                check_contained(root, &path)?;
                if file_hash(&path)?.as_deref() != Some(expected) {
                    bail!("backup is missing or modified: {relative}");
                }
            }
            (None, None) => {}
            _ => bail!("invalid backup record: {relative}"),
        }
    }
    if let Some(stray) = &state.stray {
        validate_relative(&stray.backup)?;
        let path = root.join(&stray.backup);
        check_contained(root, &path)?;
        if file_hash(&path)?.as_deref() != Some(&stray.sha256) {
            bail!("backup is missing or modified: {KRSDK}");
        }
    }
    let _ = client;
    Ok(())
}
fn snapshot_for_rollback(
    client: &Path,
    state_root: &Path,
    rollback_root: &Path,
    relative: &str,
    rollback: &mut BTreeMap<String, RollbackFile>,
) -> Result<()> {
    let source = client.join(relative);
    if source.is_file() {
        let hash = sha256_file(&source)?;
        let saved = rollback_root.join(relative);
        atomic_copy(&source, &saved)?;
        rollback.insert(
            relative.into(),
            RollbackFile {
                hash: Some(hash),
                backup: Some(
                    saved
                        .strip_prefix(state_root)?
                        .to_string_lossy()
                        .replace('\\', "/"),
                ),
            },
        );
    } else {
        rollback.insert(
            relative.into(),
            RollbackFile {
                hash: None,
                backup: None,
            },
        );
    }
    Ok(())
}
fn recover_if_needed(client: &Path) -> Result<()> {
    let root = client.join(STATE_DIR);
    let path = root.join(JOURNAL_FILE);
    if !path.exists() {
        return Ok(());
    }
    let journal: Journal = serde_json::from_reader(File::open(&path)?)
        .context("invalid transaction journal; refusing mutation")?;
    if journal.schema_version != 1 {
        bail!("unknown transaction journal version");
    }
    for (relative, record) in &journal.files {
        validate_relative(relative)?;
        let target = client.join(relative);
        check_target(client, &target)?;
        match (&record.hash, &record.backup) {
            (Some(expected), Some(backup)) => {
                validate_relative(backup)?;
                let saved = root.join(backup);
                check_contained(&root, &saved)?;
                if file_hash(&saved)?.as_deref() != Some(expected) {
                    bail!("rollback backup is missing or modified: {relative}");
                }
                atomic_copy(&saved, &target)?;
            }
            (None, None) => {
                if target.exists() {
                    fs::remove_file(&target)?;
                    sync_dir(target.parent().unwrap())?;
                }
            }
            _ => bail!("invalid rollback record: {relative}"),
        }
    }
    fs::remove_file(path)?;
    sync_dir(&root)?;
    Ok(())
}

fn find_legacy(client: &Path, package: &PatchPackage) -> Result<Option<(PathBuf, LegacyManifest)>> {
    // Legacy backups only ever described the global KRSDK.dll replacement.
    if !is_global(client) {
        return Ok(None);
    }
    let root = client
        .parent()
        .context("client has no parent")?
        .join("patch_backups");
    if !root.is_dir() {
        return Ok(None);
    }
    let mut manifests = fs::read_dir(&root)?
        .filter_map(|e| e.ok())
        .map(|e| e.path().join("manifest.json"))
        .filter(|p| p.is_file())
        .collect::<Vec<_>>();
    manifests.sort();
    manifests.reverse();
    for path in manifests {
        let manifest: LegacyManifest = match serde_json::from_reader(File::open(&path)?) {
            Ok(v) => v,
            Err(_) => continue,
        };
        if !legacy_client_matches(client, &manifest.client) {
            continue;
        }
        if !package
            .manifest
            .accepts_original(KRSDK, manifest.pinned_client.get(KRSDK).map(String::as_str))
        {
            continue;
        }
        let mut valid = true;
        for (relative, record) in &manifest.files {
            if validate_relative(relative).is_err() {
                valid = false;
                break;
            }
            let actual = file_hash(&client.join(relative))?;
            if actual.as_deref() != Some(&record.installed)
                && actual.as_deref() != record.original.as_deref()
            {
                valid = false;
                break;
            }
            if let Some(original) = &record.original {
                if file_hash(&path.parent().unwrap().join(relative))?.as_deref() != Some(original) {
                    valid = false;
                    break;
                }
            }
        }
        if valid {
            return Ok(Some((path, manifest)));
        }
    }
    Ok(None)
}
fn legacy_client_matches(client: &Path, claimed: &str) -> bool {
    if Path::new(claimed).canonicalize().ok().as_deref() == Some(client) {
        return true;
    }
    #[cfg(windows)]
    {
        let normalized = claimed.replace('/', "\\");
        let mapped = if normalized.starts_with("\\Volumes\\") {
            format!("Z:{normalized}")
        } else {
            normalized
        };
        if Path::new(&mapped).canonicalize().ok().as_deref() == Some(client) {
            return true;
        }
    }
    false
}
fn adopt_legacy(client: &Path, package: &PatchPackage) -> Result<Option<State>> {
    let Some((manifest_path, legacy)) = find_legacy(client, package)? else {
        return Ok(None);
    };
    let id = Uuid::new_v4().to_string();
    let root = client.join(STATE_DIR);
    let parent = root.join("backups");
    create_private_dir(&parent)?;
    let backup_root = parent.join(&id);
    create_private_dir(&backup_root)?;
    let mut files = BTreeMap::new();
    for file in &package.manifest.files {
        if file.path == "PGRBase.dll" && !legacy.files.contains_key(&file.path) {
            continue;
        }
        let old = legacy
            .files
            .get(&file.path)
            .context("legacy manifest does not track every release file")?;
        let backup = if let Some(original) = &old.original {
            let dest = backup_root.join(&file.path);
            atomic_copy(&manifest_path.parent().unwrap().join(&file.path), &dest)?;
            if file_hash(&dest)?.as_deref() != Some(original) {
                bail!("legacy backup changed while adopting");
            }
            Some(format!("backups/{id}/{}", file.path))
        } else {
            None
        };
        files.insert(
            file.path.clone(),
            FileState {
                original: old.original.clone(),
                installed: old.installed.clone(),
                backup,
            },
        );
    }
    Ok(Some(State {
        schema_version: 1,
        release_version: "legacy".into(),
        originals: legacy.pinned_client,
        files,
        stray: None,
    }))
}

struct OperationLock {
    #[cfg(windows)]
    handle: windows::Win32::Foundation::HANDLE,
    #[cfg(not(windows))]
    _guard: std::sync::MutexGuard<'static, ()>,
}
impl OperationLock {
    fn acquire() -> Result<Self> {
        #[cfg(windows)]
        {
            use windows::core::w;
            use windows::Win32::Foundation::{WAIT_ABANDONED, WAIT_OBJECT_0};
            use windows::Win32::System::Threading::{CreateMutexW, WaitForSingleObject, INFINITE};
            let handle =
                unsafe { CreateMutexW(None, false, w!("Local\\AscNetLauncherOperations"))? };
            let timeout = WORKER_DIRECTORIES.with(|pins| if pins.borrow().is_some() { 60_000 } else { INFINITE });
            let result = unsafe { WaitForSingleObject(handle, timeout) };
            if result != WAIT_OBJECT_0 && result != WAIT_ABANDONED {
                unsafe {
                    let _ = windows::Win32::Foundation::CloseHandle(handle);
                }
                bail!("could not acquire launcher operation lock");
            }
            Ok(Self { handle })
        }
        #[cfg(not(windows))]
        {
            static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
            Ok(Self {
                _guard: LOCK
                    .lock()
                    .map_err(|_| anyhow::anyhow!("launcher operation lock poisoned"))?,
            })
        }
    }
}
#[cfg(windows)]
impl Drop for OperationLock {
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::System::Threading::ReleaseMutex(self.handle);
            let _ = windows::Win32::Foundation::CloseHandle(self.handle);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn temp() -> PathBuf {
        let p = std::env::temp_dir().join(format!("ascnet-install-test-{}", Uuid::new_v4()));
        fs::create_dir_all(&p).unwrap();
        p
    }
    #[test]
    fn launch_command_sets_origin_clears_overrides_and_is_region_neutral() {
        let client = temp();
        for (key, value) in [("ASCNET_PATCH_TRACE", "1"), ("HTTPS_PROXY", "http://proxy"), ("KEEP_ME", "x")] {
            std::env::set_var(key, value);
        }
        for region in ["kr", "jp", "cn"] {
            let dir = client.join(region);
            let command = launch_command(&dir, dir.join("PGR.exe"), "http://127.0.0.1:5000".into());
            let envs: BTreeMap<_, _> = command.get_envs().collect();
            assert_eq!(command.get_program(), dir.join("PGR.exe").as_os_str());
            assert_eq!(command.get_current_dir(), Some(dir.as_path()));
            assert_eq!(command.get_args().count(), 0);
            assert_eq!(envs["ASCNET_PATCH_ORIGIN".as_ref() as &std::ffi::OsStr], Some("http://127.0.0.1:5000".as_ref()));
            assert_eq!(envs["ASCNET_PATCH_TRACE".as_ref() as &std::ffi::OsStr], None);
            assert_eq!(envs["HTTPS_PROXY".as_ref() as &std::ffi::OsStr], None);
            assert!(!envs.contains_key("KEEP_ME".as_ref() as &std::ffi::OsStr));
        }
        fs::remove_dir_all(client).unwrap();
    }
    #[test]
    fn version_override_is_merged_once() {
        assert_eq!(merge_version_dll_override(""), "version=n,b");
        assert_eq!(merge_version_dll_override("d3d11,dxgi=n,b"), "d3d11,dxgi=n,b;version=n,b");
        assert_eq!(merge_version_dll_override("d3d11,dxgi=n,b;"), "d3d11,dxgi=n,b;version=n,b");
        assert_eq!(merge_version_dll_override("version=n,b"), "version=n,b");
        assert_eq!(merge_version_dll_override("Version=b"), "Version=b");
        assert_eq!(merge_version_dll_override("d3d11,version=n"), "d3d11,version=n");
    }
    #[test]
    fn rollback_preserves_previous_managed_state() {
        let client = temp();
        let root = client.join(STATE_DIR);
        let rollback = root.join("rollback/check");
        fs::create_dir_all(&rollback).unwrap();
        fs::write(root.join(STATE_FILE), b"previous-state").unwrap();
        let mut files = BTreeMap::new();
        snapshot_for_rollback(&client, &root, &rollback,
            &format!("{STATE_DIR}/{STATE_FILE}"), &mut files).unwrap();
        write_json_atomic(&root.join(JOURNAL_FILE), &Journal { schema_version: 1, files }).unwrap();
        fs::write(root.join(STATE_FILE), b"changed-state").unwrap();
        recover_if_needed(&client).unwrap();
        assert_eq!(fs::read(root.join(STATE_FILE)).unwrap(), b"previous-state");
        fs::remove_dir_all(client).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn writable_directory_needs_no_elevation() {
        let client = temp();
        fs::write(client.join("libraries.txt"), b"retail").unwrap();
        let needs_consent = needs_elevation(&client);
        let unchanged = fs::read(client.join("libraries.txt")).unwrap();
        let state_created = client.join(STATE_DIR).exists();
        fs::remove_dir_all(client).unwrap();
        assert!(!needs_consent.unwrap());
        assert_eq!(unchanged, b"retail");
        assert!(!state_created);
    }

    #[cfg(windows)]
    #[test]
    #[ignore = "native Windows only: changes ACLs solely on a disposable directory; close PGR first"]
    fn protected_directory_requires_consent_without_mutation() {
        let client = temp();
        let target = client.join("libraries.txt");
        fs::write(&target, b"retail").unwrap();
        let denied = Command::new("icacls.exe").arg(&client)
            .args(["/deny", "*S-1-1-0:(W,D)"]).status().unwrap();
        assert!(denied.success());
        let result = needs_elevation(&client);
        let granted = Command::new("icacls.exe").arg(&client)
            .args(["/remove:d", "*S-1-1-0"]).status().unwrap();
        assert!(granted.success());
        assert!(result.unwrap());
        assert_eq!(fs::read(target).unwrap(), b"retail");
        assert!(!client.join(STATE_DIR).exists());
        fs::remove_dir_all(client).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn worker_refuses_unrelated_recovery_destination_without_mutation() {
        let client = temp();
        fs::write(client.join("unrelated.txt"), b"leave alone").unwrap();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(&root).unwrap();
        write_json_atomic(&root.join(JOURNAL_FILE), &Journal {
            schema_version: 1,
            files: BTreeMap::from([("unrelated.txt".into(), RollbackFile { hash: None, backup: None })]),
        }).unwrap();
        assert!(validate_worker_state(&client).is_err());
        assert_eq!(fs::read(client.join("unrelated.txt")).unwrap(), b"leave alone");
        fs::remove_dir_all(client).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn worker_pins_prevent_directory_replacement() {
        let root = temp();
        let nested = root.join("created/inside");
        WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = Some(BTreeMap::new()));
        let created = create_private_dir(&nested);
        let replaced = fs::rename(&nested, root.join("moved"));
        WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = None);
        created.unwrap();
        assert!(replaced.is_err(), "a pinned write directory must not be replaceable");
        fs::rename(&nested, root.join("moved")).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn worker_pins_allow_new_nested_backup_and_atomic_replacement() {
        let root = temp();
        let source = root.join("KRSDK.dll");
        let backup = root.join(STATE_DIR).join(format!("backups/{}/PGR_Data/Plugins/KRSDK.dll", Uuid::new_v4()));
        fs::write(&source, b"retail").unwrap();
        WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = Some(BTreeMap::new()));
        let result = (|| -> Result<()> {
            atomic_copy(&source, &backup)?;
            assert_eq!(fs::read(&backup)?, b"retail");
            fs::write(&source, b"managed")?;
            atomic_copy(&source, &backup)?;
            assert_eq!(fs::read(&backup)?, b"managed");
            assert!(fs::rename(backup.parent().unwrap(), root.join("moved")).is_err());
            Ok(())
        })();
        WORKER_DIRECTORIES.with(|pins| *pins.borrow_mut() = None);
        fs::remove_dir_all(root).unwrap();
        result.unwrap();
    }

    #[test]
    fn rollback_restores_and_removes() {
        let client = temp();
        fs::write(client.join("a"), b"old").unwrap();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(root.join("rollback/x")).unwrap();
        fs::write(root.join("rollback/x/a"), b"old").unwrap();
        let hash = sha256_file(&client.join("a")).unwrap();
        fs::write(client.join("a"), b"new").unwrap();
        fs::write(client.join("b"), b"new").unwrap();
        let journal = Journal {
            schema_version: 1,
            files: BTreeMap::from([
                (
                    "a".into(),
                    RollbackFile {
                        hash: Some(hash),
                        backup: Some("rollback/x/a".into()),
                    },
                ),
                (
                    "b".into(),
                    RollbackFile {
                        hash: None,
                        backup: None,
                    },
                ),
            ]),
        };
        write_json_atomic(&root.join(JOURNAL_FILE), &journal).unwrap();
        recover_if_needed(&client).unwrap();
        assert_eq!(fs::read(client.join("a")).unwrap(), b"old");
        assert!(!client.join("b").exists());
        let _ = fs::remove_dir_all(client);
    }
    #[test]
    fn modified_backup_is_rejected() {
        let client = temp();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(root.join("backups/x")).unwrap();
        fs::write(root.join("backups/x/a"), b"changed").unwrap();
        let state = State {
            schema_version: 1,
            release_version: "1".into(),
            originals: BTreeMap::new(),
            files: BTreeMap::from([(
                "a".into(),
                FileState {
                    original: Some("00".repeat(32)),
                    installed: "11".repeat(32),
                    backup: Some("backups/x/a".into()),
                },
            )]),
            stray: None,
        };
        assert!(verify_saved_backups(&client, &state).is_err());
        let _ = fs::remove_dir_all(client);
    }
    #[test]
    fn traversal_and_links_are_refused() {
        assert!(validate_relative("../PGR.exe").is_err());
        assert!(validate_relative("a\\b").is_err());
    }
    #[test]
    fn directory_pin_allows_a_directory_and_a_wine_mount_only() {
        assert!(directory_pin_allows(0x10, false));
        assert!(!directory_pin_allows(0x10 | 0x400, false));
        assert!(directory_pin_allows(0x10 | 0x400, true));
        assert!(!directory_pin_allows(0x400, true));
        assert!(!directory_pin_allows(0, false));
    }
    #[test]
    fn wine_bind_mount_path_matches_and_a_junction_target_does_not() {
        assert!(windows_paths_match(r"\\?\Z:\home", r"\\?\Z:\home"));
        assert!(windows_paths_match(r"Z:\home", r"\\?\Z:\home"));
        assert!(windows_paths_match(r"\\?\Z:\", r"Z:\"));
        assert!(windows_paths_match(r"\\?\Z:\Home\", r"z:\home"));
        assert!(windows_paths_match(r"\\?\UNC\server\share\", r"\\server\share"));
        assert!(!windows_paths_match(r"\\?\C:\ascnet-reparse-junc", r"\\?\C:\windows"));
        assert!(!windows_paths_match(r"\\?\Z:\home", r"\\?\Z:\home\saku"));
        assert!(!windows_paths_match(r"Z:\home", r"Z:\home2"));
        assert!(!windows_paths_match("", ""));
        assert!(!windows_paths_match(r"\\?\Z:\home", r""));
    }
    #[test]
    fn unknown_state_is_not_overwritten() {
        let client = temp();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(&root).unwrap();
        fs::write(
            root.join(STATE_FILE),
            br#"{"schemaVersion":1,"releaseVersion":"1","originals":{},"files":{},"foreign":true}"#,
        )
        .unwrap();
        assert!(read_state(&client).is_err());
        assert!(fs::read_to_string(root.join(STATE_FILE))
            .unwrap()
            .contains("foreign"));
        let _ = fs::remove_dir_all(client);
    }
    #[test]
    fn restore_refuses_modified_managed_file() {
        let client = temp();
        fs::write(client.join("a"), b"foreign").unwrap();
        let root = client.join(STATE_DIR);
        fs::create_dir_all(&root).unwrap();
        let state = State {
            schema_version: 1,
            release_version: "1".into(),
            originals: BTreeMap::new(),
            files: BTreeMap::from([(
                "a".into(),
                FileState {
                    original: None,
                    installed: "00".repeat(32),
                    backup: None,
                },
            )]),
            stray: None,
        };
        write_json_atomic(&root.join(STATE_FILE), &state).unwrap();
        let error = restore(&client, &mut |_| {}).unwrap_err().to_string();
        assert!(error.contains("modified managed file"));
        let _ = fs::remove_dir_all(client);
    }
    #[test]
    fn retained_legacy_restore_and_local_update_preserve_retail_originals() {
        for assembly in [b"assembly-stock".as_slice(), b"assembly-wine".as_slice()] {
            check_retail_originals(assembly);
        }
    }

    fn check_retail_originals(assembly: &[u8]) {
        use crate::package::{File, Manifest};
        use sha2::Digest;
        let root = temp();
        let client = root.join("game");
        let payload = root.join("release");
        fs::create_dir_all(client.join("PGR_Data/Plugins")).unwrap();
        fs::create_dir_all(&payload).unwrap();
        fs::write(client.join("PGR.exe"), b"exe").unwrap();
        fs::write(client.join("GameAssembly.dll"), assembly).unwrap();
        fs::write(client.join("PGR_Data/Plugins/KRSDK.dll"), b"retail-sdk").unwrap();
        let originals: BTreeMap<String, String> = BTreeMap::from([
            (
                "PGR.exe".into(),
                sha256_file(&client.join("PGR.exe")).unwrap(),
            ),
            (
                "GameAssembly.dll".into(),
                sha256_file(&client.join("GameAssembly.dll")).unwrap(),
            ),
            (
                "PGR_Data/Plugins/KRSDK.dll".into(),
                sha256_file(&client.join("PGR_Data/Plugins/KRSDK.dll")).unwrap(),
            ),
        ]);
        let specs = [
            ("version.dll", "version.dll", b"version".as_slice()),
            ("lucia.dll", "lucia.dll", b"lucia".as_slice()),
            (
                "PGR_Data/Plugins/KRSDK.dll",
                "KRSDK.dll",
                b"patched-sdk".as_slice(),
            ),
            (
                "libraries.txt",
                "libraries.txt",
                b"*PGR.exe\nlucia.dll\n".as_slice(),
            ),
        ];
        let files = specs
            .iter()
            .map(|(path, source, bytes)| {
                fs::write(payload.join(source), bytes).unwrap();
                File {
                    path: (*path).into(),
                    source: (*source).into(),
                    sha256: sha256_file(&payload.join(source)).unwrap(),
                    size: bytes.len() as u64,
                }
            })
            .collect::<Vec<_>>();
        let legacy = root.join("patch_backups/legacy");
        fs::create_dir_all(legacy.join("PGR_Data/Plugins")).unwrap();
        fs::write(legacy.join("PGR_Data/Plugins/KRSDK.dll"), b"retail-sdk").unwrap();
        let records = files
            .iter()
            .map(|file| {
                let original = originals.get(&file.path).cloned();
                (
                    file.path.clone(),
                    serde_json::json!({"original": original, "installed": file.sha256.clone()}),
                )
            })
            .collect::<serde_json::Map<_, _>>();
        fs::write(
            legacy.join("manifest.json"),
            serde_json::to_vec(&serde_json::json!({
                "client": fs::canonicalize(&client).unwrap(),
                "pinned_client": originals.clone(),
                "files": records
            }))
            .unwrap(),
        )
        .unwrap();
        let package = PatchPackage {
            manifest: Manifest {
                schema_version: 1,
                version: "1.0.0".into(),
                application_version: "test".into(),
                pgr_base: None,
                originals: originals
                    .clone()
                    .into_iter()
                    .map(|(key, hash)| {
                        let hashes = if key == "GameAssembly.dll" {
                            [b"assembly-stock", b"assembly-wine".as_slice()]
                                .iter()
                                .map(|bytes| format!("{:x}", sha2::Sha256::digest(bytes)))
                                .collect()
                        } else {
                            vec![hash]
                        };
                        (key, hashes)
                    })
                    .collect(),
                files,
            },
            directory: payload,
        };
        fs::write(client.join("GameAssembly.dll"), b"unknown").unwrap();
        assert!(matches!(inspect(&client, &package).unwrap(), PatchState::Unsupported(_)));
        fs::write(client.join("GameAssembly.dll"), assembly).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Unpatched);
        for file in &package.manifest.files {
            let target = client.join(&file.path);
            fs::create_dir_all(target.parent().unwrap()).unwrap();
            fs::copy(package.directory.join(&file.source), target).unwrap();
        }
        assert_eq!(
            inspect(&client, &package).unwrap(),
            PatchState::AdoptionRequired
        );
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        assert_eq!(read_state(&client).unwrap().unwrap().originals, originals);
        restore(&client, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Unpatched);
        fs::remove_dir_all(client.join(STATE_DIR)).unwrap();
        fs::remove_dir_all(&legacy).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Unpatched);
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        assert_eq!(read_state(&client).unwrap().unwrap().originals, originals);
        // One retail file restored beside an otherwise intact patch can be updated.
        // A third, unknown copy of a managed file still cannot.
        fs::write(client.join("version.dll"), b"tampered").unwrap();
        assert!(matches!(inspect(&client, &package).unwrap(), PatchState::RepairRequired(_)));
        assert!(install(&client, &package, &mut |_| {}).is_err());
        assert_eq!(fs::read(client.join("version.dll")).unwrap(), b"tampered");
        fs::copy(package.directory.join("version.dll"), client.join("version.dll")).unwrap();
        fs::write(client.join("PGR_Data/Plugins/KRSDK.dll"), b"retail-sdk").unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::UpdateAvailable);
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(fs::read(client.join("PGR_Data/Plugins/KRSDK.dll")).unwrap(), b"patched-sdk");
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        let payload_v2 = root.join("package-v2");
        fs::create_dir_all(&payload_v2).unwrap();
        let mut manifest_v2 = package.manifest.clone();
        manifest_v2.version = "2.0.0".into();
        for file in &mut manifest_v2.files {
            let bytes = format!("v2 payload for {}", file.path);
            fs::write(payload_v2.join(&file.source), bytes.as_bytes()).unwrap();
            file.sha256 = sha256_file(&payload_v2.join(&file.source)).unwrap();
            file.size = bytes.len() as u64;
        }
        let package_v2 = PatchPackage {
            manifest: manifest_v2,
            directory: payload_v2,
        };
        assert_eq!(
            inspect(&client, &package_v2).unwrap(),
            PatchState::UpdateAvailable
        );
        install(&client, &package_v2, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package_v2).unwrap(), PatchState::Current);
        // A newer supported client replaces PGR.exe/GameAssembly.dll under an existing patch.
        let hash = |bytes: &[u8]| format!("{:x}", sha2::Sha256::digest(bytes));
        let mut package_v3 = package.clone();
        package_v3.manifest.version = "3.0.0".into();
        package_v3.manifest.originals.insert("PGR.exe".into(), vec![hash(b"exe-new")]);
        package_v3.manifest.originals.insert("GameAssembly.dll".into(), vec![hash(b"assembly-new")]);
        fs::write(client.join("PGR.exe"), b"exe-new").unwrap();
        fs::write(client.join("GameAssembly.dll"), b"assembly-new").unwrap();
        assert_eq!(inspect(&client, &package_v3).unwrap(), PatchState::UpdateAvailable);
        install(&client, &package_v3, &mut |_| {}).unwrap();
        let state = read_state(&client).unwrap().unwrap();
        assert_eq!(state.originals["PGR.exe"], hash(b"exe-new"));
        assert_eq!(state.originals["GameAssembly.dll"], hash(b"assembly-new"));
        assert_eq!(state.originals["PGR_Data/Plugins/KRSDK.dll"], originals["PGR_Data/Plugins/KRSDK.dll"]);
        // A saved KRSDK original outside the release allowlist is still refused.
        let mut bad = state.clone();
        bad.originals.insert("PGR_Data/Plugins/KRSDK.dll".into(), "00".repeat(32));
        let state_path = client.join(STATE_DIR).join(STATE_FILE);
        write_json_atomic(&state_path, &bad).unwrap();
        assert!(matches!(inspect(&client, &package_v3).unwrap(), PatchState::Unsupported(_)));
        write_json_atomic(&state_path, &state).unwrap();
        fs::write(client.join("PGR.exe"), b"exe").unwrap();
        fs::write(client.join("GameAssembly.dll"), assembly).unwrap();
        restore(&client, &mut |_| {}).unwrap();
        assert_eq!(fs::read(client.join("PGR.exe")).unwrap(), b"exe");
        assert_eq!(
            fs::read(client.join("GameAssembly.dll")).unwrap(),
            assembly
        );
        assert_eq!(
            fs::read(client.join("PGR_Data/Plugins/KRSDK.dll")).unwrap(),
            b"retail-sdk"
        );
        assert!(!client.join("version.dll").exists());
        assert!(!client.join("lucia.dll").exists());
        assert!(!client.join("libraries.txt").exists());
        let _ = fs::remove_dir_all(root);
    }
    #[test]
    fn pgrbase_transaction_restores_stock_and_preexisting_wine_bytes() {
        use crate::package::{File as Payload, Manifest, PgrBaseBuild};
        use sha2::{Digest, Sha256};
        let hash = |bytes: &[u8]| format!("{:x}", Sha256::digest(bytes));
        for wine in [false, true] {
            let root = temp();
            let client = root.join("game");
            let directory = root.join("package");
            fs::create_dir_all(client.join("PGR_Data/Plugins")).unwrap();
            fs::create_dir_all(&directory).unwrap();
            let (mut stock, game, unity) = crate::pgrbase::fixture();
            stock[0x800..0x814].copy_from_slice(&[
                0x21, 0xca, 0x21, 0xca, 0x81, 0xf2, 0xb3, 0xa5, 0xd6, 0x7a,
                0x0f, 0x1f, 0xc2, 0x41, 0x8b, 0x0a, 0x50, 0x48, 0x8d, 0x05,
            ]);
            let mut before = stock.clone();
            if wine {
                before = crate::pgrbase::patch(&before, &game, &unity).unwrap();
                before[0x80a..0x80d].fill(0x90);
            }
            fs::write(client.join("PGRBase.dll"), &before).unwrap();
            fs::write(client.join("UnityPlayer.dll"), &unity).unwrap();
            let originals = [
                ("PGR.exe", game.as_slice()),
                ("GameAssembly.dll", b"stock-assembly".as_slice()),
                ("PGR_Data/Plugins/KRSDK.dll", b"stock-sdk".as_slice()),
            ].into_iter().map(|(path, bytes)| {
                fs::write(client.join(path), bytes).unwrap();
                (path.into(), vec![hash(bytes)])
            }).collect();
            fs::write(directory.join("version.dll"), b"loader").unwrap();
            let package = PatchPackage {
                manifest: Manifest {
                    schema_version: 1,
                    version: "2.0.0".into(),
                    application_version: "fixture".into(),
                    originals,
                    pgr_base: Some(PgrBaseBuild {
                        originals: vec![hash(&stock)],
                        unity_players: vec![hash(&unity)],
                        original_export_jumps: vec![stock[0x500..0x505].try_into().unwrap()],
                    }),
                    files: vec![Payload {
                        path: "version.dll".into(), source: "version.dll".into(),
                        sha256: hash(b"loader"), size: 6,
                    }],
                },
                directory,
            };
            let mut unknown = before.clone();
            unknown[0x900] ^= 1;
            fs::write(client.join("PGRBase.dll"), &unknown).unwrap();
            assert!(matches!(inspect(&client, &package).unwrap(), PatchState::Unsupported(_)));
            assert!(install(&client, &package, &mut |_| {}).is_err());
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), unknown);
            assert!(!client.join(STATE_DIR).exists());
            fs::write(client.join("PGRBase.dll"), &before).unwrap();
            // Upgrade a managed installation whose state predates PGRBase tracking.
            let mut old = package.clone();
            old.manifest.version = "1.0.0".into();
            old.manifest.pgr_base = None;
            install(&client, &old, &mut |_| {}).unwrap();
            assert_eq!(inspect(&client, &package).unwrap(), PatchState::UpdateAvailable);
            install(&client, &package, &mut |_| {}).unwrap();
            assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
            let expected = crate::pgrbase::patch(&before, &game, &unity).unwrap();
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), expected);
            let state = read_state(&client).unwrap().unwrap();
            assert_eq!(state.files["PGRBase.dll"].original.as_deref(), Some(hash(&before).as_str()));
            assert_eq!(fs::read(client.join(STATE_DIR).join(state.files["PGRBase.dll"].backup.as_ref().unwrap())).unwrap(), before);
            restore(&client, &mut |_| {}).unwrap();
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), before);
            assert!(!client.join("version.dll").exists());
            // Failure after a payload is installed rolls back all files and state.
            let mut failed = package.clone();
            failed.manifest.files.push(Payload {
                path: "lucia.dll".into(), source: "missing.dll".into(),
                sha256: hash(b"missing"), size: 7,
            });
            assert!(install(&client, &failed, &mut |_| {}).is_err());
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), before);
            assert!(!client.join("version.dll").exists());
            assert!(read_state(&client).unwrap().is_none());
            // Corrupt the generated payload after preparation: post-copy verification
            // must roll back even after PGRBase itself has been replaced.
            let result = install(&client, &package, &mut |line| {
                if line == "Installing PGRBase.dll" {
                    for entry in fs::read_dir(client.join(STATE_DIR).join("rollback")).unwrap() {
                        let payload = entry.unwrap().path().join("PGRBase.payload");
                        if payload.is_file() { fs::write(payload, b"changed after verification").unwrap(); }
                    }
                }
            });
            assert!(result.is_err());
            assert_eq!(fs::read(client.join("PGRBase.dll")).unwrap(), before);
            assert!(!client.join("version.dll").exists());
            assert!(read_state(&client).unwrap().is_none());
            fs::remove_dir_all(root).unwrap();
        }
    }

    #[test]
    fn pgrbase_recovery_tries_every_known_stock_export_jump() {
        use crate::package::PgrBaseBuild;
        use sha2::{Digest, Sha256};
        let hash = |bytes: &[u8]| format!("{:x}", Sha256::digest(bytes));
        let (global, game, unity) = crate::pgrbase::fixture();
        // A second stock build whose export entry jumps elsewhere (the CN case).
        let mut cn = global.clone();
        cn[0x501..0x505].copy_from_slice(&0x60u32.to_le_bytes());
        cn[0x900] ^= 1; // distinct stock bytes: the wrong jump must not reproduce the other build's hash
        let jumps: Vec<[u8; 5]> = vec![global[0x500..0x505].try_into().unwrap(), cn[0x500..0x505].try_into().unwrap()];
        let build = |originals: Vec<String>, jumps: Vec<[u8; 5]>| PgrBaseBuild {
            originals,
            unity_players: vec![hash(&unity)],
            original_export_jumps: jumps,
        };
        let both = build(vec![hash(&global), hash(&cn)], jumps.clone());
        for stock in [&global, &cn] {
            let patched = crate::pgrbase::patch(stock, &game, &unity).unwrap();
            assert_ne!(&patched, stock);
            assert_eq!(&recover_stock(&both, &patched, &game, &unity).unwrap(), stock);
        }
        // Recovery is bounded by the allowlist: a known jump with an unlisted stock hash, or an unknown jump, is refused.
        let cn_patched = crate::pgrbase::patch(&cn, &game, &unity).unwrap();
        assert!(recover_stock(&build(vec![hash(&global)], jumps.clone()), &cn_patched, &game, &unity).is_err());
        assert!(recover_stock(&build(vec![hash(&cn)], jumps[..1].to_vec()), &cn_patched, &game, &unity).is_err());
    }

    #[test]
    fn cn_client_installs_loader_without_touching_the_official_sdk() {
        use crate::package::{File as Payload, Manifest};
        use sha2::{Digest, Sha256};
        let hash = |bytes: &[u8]| format!("{:x}", Sha256::digest(bytes));
        let root = temp();
        let client = root.join("game");
        let directory = root.join("package");
        fs::create_dir_all(client.join("PGR_Data/Plugins/KRSDKRes")).unwrap();
        fs::write(client.join("PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json"), b"{}").unwrap();
        fs::create_dir_all(&directory).unwrap();
        let retail = [
            ("PGR.exe", b"cn-exe".as_slice()),
            ("GameAssembly.dll", b"cn-assembly".as_slice()),
            ("PGR_Data/Plugins/KRSDKEx.dll", b"cn-sdk-ex".as_slice()),
            ("PGR_Data/Plugins/libkrsdkcurl.dll", b"cn-sdk-curl".as_slice()),
        ];
        let mut originals: BTreeMap<String, Vec<String>> = BTreeMap::new();
        for (path, bytes) in retail {
            fs::write(client.join(path), bytes).unwrap();
            originals.insert(path.into(), vec![hash(bytes)]);
        }
        originals.insert(KRSDK.into(), vec![hash(b"global-sdk")]);
        let files = [
            ("version.dll", "version.dll"),
            ("lucia.dll", "lucia.dll"),
            (KRSDK, "KRSDK.dll"),
            ("libraries.txt", "libraries.txt"),
        ]
        .map(|(path, source)| {
            let bytes = format!("payload {source}");
            fs::write(directory.join(source), &bytes).unwrap();
            Payload { path: path.into(), source: source.into(), sha256: hash(bytes.as_bytes()), size: bytes.len() as u64 }
        });
        let package = PatchPackage {
            manifest: Manifest {
                schema_version: 1,
                version: "2.0.0".into(),
                application_version: "fixture".into(),
                originals,
                pgr_base: None,
                files: files.into(),
            },
            directory,
        };
        let sdk_bytes = || retail[2..].iter().map(|(path, _)| fs::read(client.join(path)).unwrap()).collect::<Vec<_>>();
        let sdk_before = sdk_bytes();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Unpatched);
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        let state = read_state(&client).unwrap().unwrap();
        assert_eq!(state.files.keys().map(String::as_str).collect::<Vec<_>>(), ["libraries.txt", "lucia.dll", "version.dll"]);
        assert_eq!(state.originals.keys().map(String::as_str).collect::<Vec<_>>(), ["GameAssembly.dll", "PGR.exe"]);
        for installed in ["version.dll", "lucia.dll", "libraries.txt"] {
            assert!(client.join(installed).is_file(), "{installed}");
        }
        assert!(!client.join(KRSDK).exists());
        assert_eq!(sdk_bytes(), sdk_before);

        // State written by a global install cannot be reused for a CN folder.
        let mut foreign = state.clone();
        foreign.originals.insert(KRSDK.into(), hash(b"global-sdk"));
        write_json_atomic(&client.join(STATE_DIR).join(STATE_FILE), &foreign).unwrap();
        assert!(matches!(inspect(&client, &package).unwrap(), PatchState::RepairRequired(_)));
        assert!(install(&client, &package, &mut |_| {}).is_err());
        write_json_atomic(&client.join(STATE_DIR).join(STATE_FILE), &state).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);

        restore(&client, &mut |_| {}).unwrap();
        for installed in ["version.dll", "lucia.dll", "libraries.txt"] {
            assert!(!client.join(installed).exists(), "{installed}");
        }
        assert!(read_state(&client).unwrap().is_none());
        assert_eq!(sdk_bytes(), sdk_before);

        // A leftover AscNet KRSDK.dll is not a global client: Setup backs it up and removes it, Restore returns it.
        let stray = client.join(KRSDK);
        fs::write(&stray, b"leftover-asc-sdk").unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Unpatched);
        let mut lines = Vec::new();
        install(&client, &package, &mut |line| lines.push(line)).unwrap();
        assert!(!stray.exists());
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        assert!(lines.iter().any(|l| l.starts_with("Removed leftover KRSDK.dll from a CN client (backed up to ")), "{lines:?}");
        assert_eq!(read_state(&client).unwrap().unwrap().originals.keys().count(), 2);
        // Copying it back afterwards is detected again, and a second Setup keeps the first backup recoverable.
        fs::write(&stray, b"leftover-asc-sdk").unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::UpdateAvailable);
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        restore(&client, &mut |_| {}).unwrap();
        assert_eq!(fs::read(&stray).unwrap(), b"leftover-asc-sdk");
        for installed in ["version.dll", "lucia.dll", "libraries.txt"] {
            assert!(!client.join(installed).exists(), "{installed}");
        }
        assert!(read_state(&client).unwrap().is_none());
        assert_eq!(sdk_bytes(), sdk_before);
        fs::remove_file(&stray).unwrap();

        // An SDK build outside the allowlist (or a folder with no SDK at all) is refused, nothing written.
        fs::write(client.join("PGR_Data/Plugins/KRSDKEx.dll"), b"cn-sdk-ex-other").unwrap();
        let observed = hash(b"cn-sdk-ex-other");
        match inspect(&client, &package).unwrap() {
            PatchState::Unsupported(reason) => assert!(reason.contains("KRSDKEx.dll") && reason.contains(&observed), "{reason}"),
            other => panic!("{other:?}"),
        }
        assert!(install(&client, &package, &mut |_| {}).is_err());
        assert!(!client.join("version.dll").exists());
        fs::remove_file(client.join("PGR_Data/Plugins/KRSDKEx.dll")).unwrap();
        match inspect(&client, &package).unwrap() {
            PatchState::Unsupported(reason) => assert!(reason.contains("neither"), "{reason}"),
            other => panic!("{other:?}"),
        }
        fs::remove_dir_all(root).unwrap();
    }
}

#[cfg(test)]
mod game_update_tests {
    use super::*;
    use crate::package::{File as Payload, Manifest};
    use sha2::{Digest, Sha256};

    fn hash(bytes: &[u8]) -> String {
        format!("{:x}", Sha256::digest(bytes))
    }

    /// A Steam update replaces the patched KRSDK.dll with an accepted stock one; Setup must adopt it, Restore must
    /// leave it alone, and an unknown file is still refused with an actionable message.
    #[test]
    fn game_update_over_managed_krsdk_is_not_tampering() {
        let root = std::env::temp_dir().join(format!("ascnet-install-test-{}", Uuid::new_v4()));
        let client = root.join("game");
        let directory = root.join("package");
        fs::create_dir_all(client.join("PGR_Data/Plugins")).unwrap();
        fs::create_dir_all(&directory).unwrap();
        let sdk = client.join(KRSDK);
        fs::write(client.join("PGR.exe"), b"exe").unwrap();
        fs::write(client.join("GameAssembly.dll"), b"assembly").unwrap();
        fs::write(&sdk, b"sdk-4.8").unwrap();
        let mut originals: BTreeMap<String, Vec<String>> = BTreeMap::new();
        originals.insert("PGR.exe".into(), vec![hash(b"exe")]);
        originals.insert("GameAssembly.dll".into(), vec![hash(b"assembly")]);
        originals.insert(KRSDK.into(), vec![hash(b"sdk-4.8"), hash(b"sdk-4.9"), hash(b"sdk-4.9-cdn")]);
        let files = [("version.dll", "version.dll"), ("lucia.dll", "lucia.dll"), (KRSDK, "KRSDK.dll"), ("libraries.txt", "libraries.txt")]
            .map(|(path, source)| {
                let bytes = format!("payload {source}");
                fs::write(directory.join(source), &bytes).unwrap();
                Payload { path: path.into(), source: source.into(), sha256: hash(bytes.as_bytes()), size: bytes.len() as u64 }
            });
        let package = PatchPackage {
            manifest: Manifest {
                schema_version: 1,
                version: "1.0.0".into(),
                application_version: "fixture".into(),
                originals,
                pgr_base: None,
                files: files.into(),
            },
            directory,
        };
        let sdk_backup = |client: &Path| {
            let state = read_state(client).unwrap().unwrap();
            let record = state.files.get(KRSDK).unwrap().clone();
            (state.originals[KRSDK].clone(), record.original.unwrap(), fs::read(client.join(STATE_DIR).join(record.backup.unwrap())).unwrap())
        };

        // Installed on the old game version: backup holds the 4.8 stock SDK.
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        assert_eq!(sdk_backup(&client).2, b"sdk-4.8");

        // Steam updates the game: KRSDK.dll is stock 4.9 again, the other managed files survive.
        fs::write(&sdk, b"sdk-4.9").unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::UpdateAvailable);
        install(&client, &package, &mut |_| {}).unwrap();
        assert_eq!(inspect(&client, &package).unwrap(), PatchState::Current);
        let (saved, original, backup) = sdk_backup(&client);
        assert_eq!((saved, original, backup), (hash(b"sdk-4.9"), hash(b"sdk-4.9"), b"sdk-4.9".to_vec()));
        assert_eq!(fs::read(&sdk).unwrap(), b"payload KRSDK.dll");

        // Restore after a second update puts nothing over the game's own file.
        fs::write(&sdk, b"sdk-4.9-cdn").unwrap();
        restore_with(&client, &mut |_| {}, package.manifest.clone()).unwrap();
        assert_eq!(fs::read(&sdk).unwrap(), b"sdk-4.9-cdn");
        assert!(read_state(&client).unwrap().is_none());
        assert!(!client.join("lucia.dll").exists());

        // Restore of an untouched patched install still restores the recorded backup.
        install(&client, &package, &mut |_| {}).unwrap();
        restore_with(&client, &mut |_| {}, package.manifest.clone()).unwrap();
        assert_eq!(fs::read(&sdk).unwrap(), b"sdk-4.9-cdn");

        // Unknown bytes (foreign injector, hand edit) are still refused, naming the file and the way out.
        install(&client, &package, &mut |_| {}).unwrap();
        fs::write(&sdk, b"foreign").unwrap();
        let PatchState::RepairRequired(reason) = inspect(&client, &package).unwrap() else { panic!("expected RepairRequired") };
        assert!(reason.contains("KRSDK.dll") && reason.contains("Verify & Repair") && reason.contains(STATE_DIR), "{reason}");
        let error = install(&client, &package, &mut |_| {}).unwrap_err().to_string();
        assert!(error.starts_with("refusing installation:") && error.contains("KRSDK.dll"), "{error}");
        let error = restore_with(&client, &mut |_| {}, package.manifest.clone()).unwrap_err().to_string();
        assert!(error.contains("modified managed file: PGR_Data/Plugins/KRSDK.dll") && error.contains("Verify & Repair"), "{error}");
        assert_eq!(fs::read(&sdk).unwrap(), b"foreign");
        let _ = fs::remove_dir_all(root);
    }
}
