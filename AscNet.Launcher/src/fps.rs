use crate::{bundle::{resolve, read_bounded, decode, rewrite, Active}, install};
use anyhow::{bail, Context, Result};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::Path;
use uuid::Uuid;
const TEXT_ASSET: &[u8] = b"XUiMain.lua";
const MARKER: &str = "PgrNativeFpsTimer";
const START: &str = "function XUiMain:OnStart()";
fn text_asset(payload: &[u8]) -> Result<(usize, &[u8])> {
    crate::bundle::text_asset(payload, TEXT_ASSET)
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct BackupMetadata<'a> {
    schema_version: u32,
    bundle: &'a str,
    original_sha256: &'a str,
    replacement_sha256: &'a str,
}

fn inspect(game: &Path) -> Result<Option<i32>> {
    let active = resolve(game)?;
    let bytes = read_bounded(&active.path)?;
    let decoded = decode(&bytes)?;
    let (_, content) = text_asset(&decoded.payload)?;
    owned_fps(std::str::from_utf8(content).context("XUiMain.lua is not UTF-8")?)
}

/// One-time migration from the retired bundle FPS patch (FPS is now applied natively by lucia):
/// removes the old Lua hook from the document-scope matrix bundle, keeping a backup.
/// Returns the FPS value the hook had, or `None` when the bundle is clean.
pub fn remove_legacy_hook(game: &Path) -> Result<Option<i32>> {
    let Some(fps) = inspect(game)? else {
        return Ok(None);
    };
    if install::game_running()? {
        bail!("the old bundle FPS patch ({fps} FPS) is still installed; close PGR.exe and press Check to remove it");
    }
    let game = fs::canonicalize(game)
        .with_context(|| format!("invalid game directory: {}", game.display()))?;
    let found = resolve(&game)?;
    let original = read_bounded(&found.path)?;
    let decoded = decode(&original)?;
    let (content_offset, content) = text_asset(&decoded.payload)?;
    let text = std::str::from_utf8(content).context("XUiMain.lua is not UTF-8")?;
    let replacement = fit_same_size(content, &remove_hook(text)?)?;
    let output = rewrite(&original, decoded, content_offset, replacement)?;

    // Reject an updater race before creating a provenance record or committing.
    if sha256(&read_bounded(&found.path)?) != sha256(&original) {
        bail!("matrix bundle changed while removing the old FPS patch; retry after the update finishes");
    }
    let active = stage_in_document(&game, found)?;
    save_backup(&game, &active.path, &original, &output)?;
    write_atomic(&active.path, &output)?;
    Ok(Some(fps))
}

/// The launcher only writes `document`. When the matrix bundle ships in `resource` (4.9), put a copy in
/// `document/matrix` under the same physical name first; the document file overrides the resource one by name.
fn stage_in_document(game: &Path, active: Active) -> Result<Active> {
    if active.scope == "document" {
        return Ok(active);
    }
    let name = active.path.file_name().context("matrix bundle has no file name")?;
    let directory = game.join("PGR_Data/StreamingAssets/document/matrix");
    fs::create_dir_all(&directory).with_context(|| format!("creating {}", directory.display()))?;
    let target = directory.join(name);
    write_new(&target, &read_bounded(&active.path)?)
        .with_context(|| format!("copying the packaged matrix bundle to {}", target.display()))?;
    sync_dir(&directory)?;
    Ok(Active { path: fs::canonicalize(target)?, scope: "document" })
}

fn owned_fps(text: &str) -> Result<Option<i32>> {
    let lines: Vec<_> = text.lines().filter(|line| line.contains(MARKER)).collect();
    if lines.is_empty() {
        return Ok(None);
    }
    if lines.len() != 1 {
        bail!("ambiguous FPS hook: multiple marker lines");
    }
    parse_hook(lines[0]).map(Some)
}

fn parse_hook(line: &str) -> Result<i32> {
    let prefix = "    if not XUiMain.PgrNativeFpsTimer then local f=function() CS.UnityEngine.Application.targetFrameRate=";
    let suffix = " end f() XUiMain.PgrNativeFpsTimer=XScheduleManager.ScheduleForever(f,2000) end";
    let value = line
        .strip_prefix(prefix)
        .and_then(|v| v.strip_suffix(suffix))
        .context("unrecognized FPS hook; refusing to alter it")?;
    let fps: i32 = value.parse().context("invalid FPS value in hook")?;
    if fps <= 0 {
        bail!("invalid non-positive FPS value in hook");
    }
    Ok(fps)
}

fn remove_hook(text: &str) -> Result<String> {
    owned_fps(text)?;
    Ok(text
        .split_inclusive('\n')
        .filter(|line| !line.contains(MARKER))
        .collect())
}

fn fit_same_size(original: &[u8], changed: &str) -> Result<Vec<u8>> {
    let mut bytes = changed.as_bytes().to_vec();
    if bytes.len() > original.len() {
        let need = bytes.len() - original.len();
        let start = bytes
            .windows(START.len())
            .position(|w| w == START.as_bytes())
            .context("XUiMain:OnStart marker missing")?;
        let whitespace_start = bytes[..start]
            .iter()
            .rposition(|b| !matches!(b, b' ' | b'\t'))
            .map_or(0, |n| n + 1);
        let available = start - whitespace_start;
        if available < need {
            bail!("XUiMain.lua lacks {need} bytes of safe whitespace for the FPS hook");
        }
        bytes.drain(whitespace_start..whitespace_start + need);
    } else if bytes.len() < original.len() {
        let add = original.len() - bytes.len();
        let start = bytes
            .windows(START.len())
            .position(|w| w == START.as_bytes())
            .context("XUiMain:OnStart marker missing")?;
        bytes.splice(start..start, std::iter::repeat(b' ').take(add));
    }
    if bytes.len() != original.len() {
        bail!("FPS patch changed TextAsset size");
    }
    Ok(bytes)
}

fn save_backup(game: &Path, bundle: &Path, original: &[u8], replacement: &[u8]) -> Result<()> {
    let state = game.join(".ascnet-launcher");
    create_real_dir(&state)?;
    let backups = state.join("fps-backups");
    create_real_dir(&backups)?;
    let root = backups.join(Uuid::new_v4().to_string());
    fs::create_dir(&root)?;
    let backup = root.join("matrix.ab");
    write_new(&backup, original)?;
    let original_hash = sha256(original);
    if sha256(&fs::read(&backup)?) != original_hash {
        bail!("FPS backup verification failed");
    }
    let relative = bundle
        .strip_prefix(game)
        .context("matrix bundle lies outside game directory")?
        .to_string_lossy()
        .replace('\\', "/");
    let replacement_hash = sha256(replacement);
    let metadata = serde_json::to_vec_pretty(&BackupMetadata {
        schema_version: 1,
        bundle: &relative,
        original_sha256: &original_hash,
        replacement_sha256: &replacement_hash,
    })?;
    write_new(&root.join("metadata.json"), &metadata)?;
    sync_dir(&root)?;
    sync_dir(root.parent().context("backup directory has no parent")?)
}

fn create_real_dir(path: &Path) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(metadata)
            if metadata.file_type().is_symlink() || !metadata.is_dir() || is_reparse(&metadata) =>
        {
            bail!(
                "refusing non-directory/reparse backup path: {}",
                path.display()
            )
        }
        Ok(_) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            fs::create_dir(path).with_context(|| format!("creating {}", path.display()))
        }
        Err(error) => Err(error.into()),
    }
}
#[cfg(windows)]
fn is_reparse(metadata: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    metadata.file_attributes() & 0x400 != 0
}
#[cfg(not(windows))]
fn is_reparse(_: &fs::Metadata) -> bool {
    false
}

fn write_new(path: &Path, data: &[u8]) -> Result<()> {
    let mut file = OpenOptions::new().write(true).create_new(true).open(path)?;
    file.write_all(data)?;
    file.sync_all()?;
    Ok(())
}
fn write_atomic(path: &Path, data: &[u8]) -> Result<()> {
    let parent = path.parent().context("asset has no parent directory")?;
    let temporary = parent.join(format!(
        ".{}.{}.tmp",
        path.file_name().context("asset has no file name")?.to_string_lossy(),
        Uuid::new_v4()
    ));
    let result = (|| {
        write_new(&temporary, data)?;
        atomic_replace(&temporary, path)?;
        sync_dir(parent)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary);
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
            )?;
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
    fs::File::open(path)?.sync_all().map_err(Into::into)
}
#[cfg(windows)]
fn sync_dir(_: &Path) -> Result<()> {
    Ok(())
}

fn sha256(data: &[u8]) -> String {
    format!("{:x}", Sha256::digest(data))
}
#[cfg(test)]
mod tests {
    use super::*;
    const ANCHOR: &str = "    CS.XInputManager.SetCurInputMap(CS.XInputMapId.System)";
    fn legacy_hook(fps: i32) -> String {
        format!("    if not XUiMain.PgrNativeFpsTimer then local f=function() CS.UnityEngine.Application.targetFrameRate={fps} end f() XUiMain.PgrNativeFpsTimer=XScheduleManager.ScheduleForever(f,2000) end")
    }
    #[test]
    fn malformed_hook_is_never_removed() {
        let text = "PgrNativeFpsTimer = somebody_else\n";
        assert!(owned_fps(text).is_err());
        assert!(remove_hook(text).is_err());
    }
    #[test]
    fn legacy_hook_is_detected_and_removed_without_touching_other_bytes() {
        let stock = format!("{ANCHOR}\n{}{START}\nkeep\n", " ".repeat(256));
        assert_eq!(owned_fps(&stock).unwrap(), None);
        let hooked = stock.replacen(ANCHOR, &format!("{ANCHOR}\n{}", legacy_hook(240)), 1);
        let hooked = hooked.replacen(&" ".repeat(legacy_hook(240).len() + 1), "", 1);
        assert_eq!(hooked.len(), stock.len());
        assert_eq!(owned_fps(&hooked).unwrap(), Some(240));
        let restored = fit_same_size(hooked.as_bytes(), &remove_hook(&hooked).unwrap()).unwrap();
        assert_eq!(restored.len(), stock.len());
        let restored = std::str::from_utf8(&restored).unwrap();
        assert_eq!(owned_fps(restored).unwrap(), None);
        assert!(restored.starts_with(ANCHOR));
        assert!(restored.ends_with("function XUiMain:OnStart()\nkeep\n"));
    }
    #[test]
    fn corrupt_lengths_are_rejected() {
        assert!(decode(b"UnityFS\0").is_err());
    }
}
