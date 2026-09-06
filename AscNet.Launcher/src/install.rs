use crate::package::{compare_versions, sha256_file, validate_server_origin, PatchPackage};
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
const ORIGINAL_KEYS: [&str; 3] = ["PGR.exe", "GameAssembly.dll", "PGR_Data/Plugins/KRSDK.dll"];

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

pub fn inspect(client: &Path, package: &PatchPackage) -> Result<PatchState> {
    let client = checked_client(client)?;
    if client.join(STATE_DIR).join(JOURNAL_FILE).exists() {
        return Ok(PatchState::RepairRequired(
            "an interrupted transaction must be recovered by install or restore".into(),
        ));
    }
    for key in [ORIGINAL_KEYS[0], ORIGINAL_KEYS[1]] {
        let actual = file_hash(&client.join(key))?;
        if !package.manifest.accepts_original(key, actual.as_deref()) {
            return Ok(PatchState::Unsupported(format!(
                "{key} does not match supported application version"
            )));
        }
    }

    if let Some(state) = read_state(&client)? {
        if state.schema_version != 1 {
            return Ok(PatchState::RepairRequired(
                "unknown launcher state version".into(),
            ));
        }
        if ORIGINAL_KEYS
            .iter()
            .any(|key| {
                !package
                    .manifest
                    .accepts_original(key, state.originals.get(*key).map(String::as_str))
            })
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
        let mut all_tracked = true;
        let mut all_original = true;
        for file in &package.manifest.files {
            let actual = file_hash(&client.join(&file.path))?;
            let old = state.files.get(&file.path);
            all_target &= actual.as_deref() == Some(&file.sha256);
            all_tracked &= old.is_some_and(|old| actual.as_deref() == Some(&old.installed));
            all_original &= old.is_some_and(|old| actual.as_deref() == old.original.as_deref());
        }
        if all_target {
            return Ok(PatchState::Current);
        }
        if all_original {
            return Ok(PatchState::Unpatched);
        }
        if all_tracked {
            return Ok(PatchState::UpdateAvailable);
        }
        return Ok(PatchState::RepairRequired(
            "a managed patch file was modified or removed".into(),
        ));
    }

    if let Some((_path, legacy)) = find_legacy(&client, package)? {
        let mut all_target = true;
        let mut all_installed = true;
        let mut all_original = true;
        for file in &package.manifest.files {
            let actual = file_hash(&client.join(&file.path))?;
            let old = legacy.files.get(&file.path);
            all_target &= actual.as_deref() == Some(&file.sha256);
            all_installed &= old.is_some_and(|old| actual.as_deref() == Some(&old.installed));
            all_original &= old.is_some_and(|old| actual.as_deref() == old.original.as_deref());
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

    let krsdk = ORIGINAL_KEYS[2];
    if !package
        .manifest
        .accepts_original(krsdk, file_hash(&client.join(krsdk))?.as_deref())
    {
        return Ok(PatchState::Unsupported(
            "KRSDK.dll is neither a supported retail original nor a verified managed patch".into(),
        ));
    }
    for file in &package.manifest.files {
        if file.path != krsdk && file_hash(&client.join(&file.path))?.is_some() {
            return Ok(PatchState::Unsupported(format!(
                "unmanaged file would be replaced: {}",
                file.path
            )));
        }
    }
    Ok(PatchState::Unpatched)
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
    let observed = inspect(&client, package)?;
    if matches!(
        observed,
        PatchState::Unsupported(_) | PatchState::RepairRequired(_)
    ) {
        bail!("refusing installation in state: {observed:?}");
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
    let originals = match &prior {
        Some(state) => state.originals.clone(),
        None => ORIGINAL_KEYS
            .iter()
            .map(|key| Ok(((*key).to_owned(), sha256_file(&client.join(key))?)))
            .collect::<Result<BTreeMap<_, _>>>()?,
    };
    let id = Uuid::new_v4().to_string();
    let backup_parent = state_root.join("backups");
    fs::create_dir_all(&backup_parent)?;
    let backup_root = backup_parent.join(&id);
    fs::create_dir(&backup_root)?;
    let mut files = BTreeMap::new();
    for file in &package.manifest.files {
        let target = client.join(&file.path);
        check_target(&client, &target)?;
        let old = prior.as_ref().and_then(|s| s.files.get(&file.path));
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
    let state = State {
        schema_version: 1,
        release_version: package.manifest.version.clone(),
        originals,
        files,
    };
    verify_saved_backups_at(&client, &state, &state_root)?;

    let rollback_root = state_root.join("rollback").join(&id);
    fs::create_dir_all(state_root.join("rollback"))?;
    fs::create_dir(&rollback_root)?;
    let mut rollback = BTreeMap::new();
    snapshot_for_rollback(
        &client,
        &state_root,
        &rollback_root,
        STATE_FILE,
        &mut rollback,
    )?;
    for file in &package.manifest.files {
        let target = client.join(&file.path);
        if target.is_file() {
            let hash = sha256_file(&target)?;
            let saved = rollback_root.join(&file.path);
            atomic_copy(&target, &saved)?;
            rollback.insert(
                file.path.clone(),
                RollbackFile {
                    hash: Some(hash),
                    backup: Some(format!("rollback/{id}/{}", file.path.replace('\\', "/"))),
                },
            );
        } else {
            rollback.insert(
                file.path.clone(),
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
            let source = package.directory.join(&file.source);
            refuse_reparse(&source)?;
            if sha256_file(&source)? != file.sha256 {
                bail!("release payload changed after verification: {}", file.path);
            }
            let target = client.join(&file.path);
            check_target(&client, &target)?;
            progress(format!("Installing {}", file.path));
            atomic_copy(&source, &target)?;
            if sha256_file(&target)? != file.sha256 {
                bail!("installed-file verification failed: {}", file.path);
            }
        }
        write_json_atomic(&state_root.join(STATE_FILE), &state)?;
        Ok(())
    })();
    if let Err(error) = result {
        recover_if_needed(&client).context("installation failed and rollback failed")?;
        return Err(error);
    }
    fs::remove_file(state_root.join(JOURNAL_FILE))?;
    let _ = fs::remove_dir_all(rollback_root);
    sync_dir(&state_root)?;
    Ok(state_root.join(STATE_FILE))
}

pub fn restore(client: &Path, progress: &mut dyn FnMut(String)) -> Result<()> {
    let _lock = OperationLock::acquire()?;
    if game_running()? {
        bail!("refusing to restore while PGR.exe is running");
    }
    let client = checked_client(client)?;
    recover_if_needed(&client)?;
    let state = read_state(&client)?.context("no launcher-managed installation to restore")?;
    verify_saved_backups(&client, &state)?;
    for (relative, record) in &state.files {
        let target = client.join(relative);
        check_target(&client, &target)?;
        let actual = file_hash(&target)?;
        if actual.as_deref() != Some(&record.installed)
            && actual.as_deref() != record.original.as_deref()
        {
            bail!("refusing to overwrite modified managed file: {relative}");
        }
    }
    let state_root = client.join(STATE_DIR);
    let id = Uuid::new_v4().to_string();
    let rollback_root = state_root.join("rollback").join(&id);
    fs::create_dir_all(state_root.join("rollback"))?;
    fs::create_dir(&rollback_root)?;
    let mut rollback = BTreeMap::new();
    snapshot_for_rollback(
        &client,
        &state_root,
        &rollback_root,
        STATE_FILE,
        &mut rollback,
    )?;
    for (relative, _) in &state.files {
        let target = client.join(relative);
        if target.is_file() {
            let saved = rollback_root.join(relative);
            atomic_copy(&target, &saved)?;
            rollback.insert(
                relative.clone(),
                RollbackFile {
                    hash: Some(sha256_file(&target)?),
                    backup: Some(format!("rollback/{id}/{}", relative.replace('\\', "/"))),
                },
            );
        } else {
            rollback.insert(
                relative.clone(),
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
            progress(format!("Restoring {relative}"));
            let target = client.join(relative);
            if let Some(backup) = &record.backup {
                atomic_copy(&state_root.join(backup), &target)?;
            } else if target.exists() {
                fs::remove_file(&target)?;
                sync_dir(target.parent().unwrap())?;
            }
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
    let _ = fs::remove_dir_all(rollback_root);
    Ok(())
}

pub fn game_running() -> Result<bool> {
    #[cfg(windows)]
    {
        let output = Command::new("tasklist.exe")
            .args(["/FI", "IMAGENAME eq PGR.exe", "/FO", "CSV", "/NH"])
            .output()
            .context("querying running processes")?;
        if !output.status.success() {
            bail!("tasklist failed; refusing to assume the game is stopped");
        }
        Ok(String::from_utf8_lossy(&output.stdout).lines().any(|line| {
            line.trim_start_matches('\u{feff}')
                .starts_with("\"PGR.exe\",")
        }))
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
    let mut command = Command::new(executable);
    command
        .current_dir(&client)
        .env("ASCNET_PATCH_ORIGIN", origin);
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
    if running_under_wine() {
        let old = std::env::var("WINEDLLOVERRIDES").unwrap_or_default();
        let merged = if old.is_empty() {
            "version=n,b".into()
        } else {
            format!("{old};version=n,b")
        };
        command.env("WINEDLLOVERRIDES", merged);
    }
    command.spawn().context("launching PGR.exe")?;
    Ok(())
}
#[cfg(windows)]
fn running_under_wine() -> bool {
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
fn running_under_wine() -> bool {
    false
}

fn checked_client(client: &Path) -> Result<PathBuf> {
    let client = fs::canonicalize(client)
        .with_context(|| format!("invalid game directory: {}", client.display()))?;
    if !client.is_dir() {
        bail!("game path is not a directory");
    }
    refuse_reparse(&client)?;
    Ok(client)
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
    fs::create_dir_all(path).with_context(|| format!("creating {}", path.display()))
}

fn atomic_copy(source: &Path, destination: &Path) -> Result<()> {
    let parent = destination.parent().context("destination has no parent")?;
    fs::create_dir_all(parent)?;
    let temporary = parent.join(format!(
        ".{}.{}.tmp",
        destination.file_name().unwrap().to_string_lossy(),
        Uuid::new_v4()
    ));
    let result = (|| -> Result<()> {
        let mut input = File::open(source)?;
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
fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        use windows::core::PCWSTR;
        use windows::Win32::Storage::FileSystem::{
            MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
        };
        let src: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
        let dst: Vec<u16> = destination
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect();
        unsafe {
            MoveFileExW(
                PCWSTR(src.as_ptr()),
                PCWSTR(dst.as_ptr()),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
            .with_context(|| format!("atomically replacing {}", destination.display()))?;
        }
        Ok(())
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
    // Windows cannot open a directory through std::fs::File. File contents are
    // flushed before commit and MoveFileExW(MOVEFILE_WRITE_THROUGH) makes the
    // rename durable, which is the Windows equivalent of the POSIX directory fsync.
    Ok(())
}
fn write_json_atomic<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    let parent = path.parent().unwrap();
    fs::create_dir_all(parent)?;
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
        if ORIGINAL_KEYS
            .iter()
            .any(|key| {
                !package
                    .manifest
                    .accepts_original(key, manifest.pinned_client.get(*key).map(String::as_str))
            })
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
    fs::create_dir_all(&parent)?;
    let backup_root = parent.join(&id);
    fs::create_dir(&backup_root)?;
    let mut files = BTreeMap::new();
    for file in &package.manifest.files {
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
            let result = unsafe { WaitForSingleObject(handle, INFINITE) };
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
}
