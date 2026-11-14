//! Rosetta 2 client patches (macOS/CrossOver): `PGRBase.dll` and `GameAssembly.dll`.
//!
//! Rosetta cannot translate register-form `0F 1F` NOPs, so each build has exactly one such site replaced with `90`s
//! (real CPUs execute both as no-ops). Port of `Scripts/apply_rosetta_patches.py` (GameAssembly) and the PGRBase
//! signature from `docs/macos-port.md`. The Unity startup stub stays in `pgrbase.rs`/`install.rs`.
//!
//! API: [`apply`] patches a game folder (idempotent; writes nothing unless exactly one site matches and the patched
//! hash is accepted by `supported-client.json`); [`restore`] puts the recorded originals back. Originals are backed up
//! under `<game>/.ascnet-rosetta/` with their hashes recorded in `state.json`.
use crate::package::Manifest;
use anyhow::{anyhow, bail, ensure, Context, Result};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use uuid::Uuid;

const STATE_DIR: &str = ".ascnet-rosetta";
const GAME_ASSEMBLY: &str = "GameAssembly.dll";
const PGR_BASE: &str = "PGRBase.dll";
const SECTION: &[u8] = b".tvm0";
/// `PGRBase.dll`: bytes before and after the `0F 1F C2` NOP (offset 10), stable across builds.
const PGR_BASE_HEAD: [u8; 10] = [0x21, 0xca, 0x21, 0xca, 0x81, 0xf2, 0xb3, 0xa5, 0xd6, 0x7a];
const PGR_BASE_TAIL: [u8; 7] = [0x41, 0x8b, 0x0a, 0x50, 0x48, 0x8d, 0x05];
const NOP3: [u8; 3] = [0x0f, 0x1f, 0xc2];
/// Patched 4.9.1 GameAssembly.dll (EN, TW, KR, JP, CN). Only EN is verified in game.
const KNOWN_PATCHED: [&str; 5] = [
    "3e9e3d54aa67e5014dcdaa8af36910deceebeb182539dd1f5d07992e2449a39e",
    "c45344a7f74c133eaa42720f21c308e40668e7620516ba953588b5aff448079d",
    "a18db6b966de19c039d9d1d812efe547b2ef98adc2537e92115b87716c88dc68",
    "e4fe4f09435b63d403ceacdd96ded5cf44b597330df361959f4df29ddf2bbfc1",
    "641486e2ffaecf6c4a131fd36c66060e88af399cde9ce9f2bcb7d9af6c3c1a41",
];

/// What happened to one file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Report {
    pub file: &'static str,
    /// Hash before patching; `None` when the file was already patched and no record of the original exists.
    pub original_sha256: Option<String>,
    pub patched_sha256: String,
    /// `false` when the file was already patched.
    pub changed: bool,
}

#[derive(Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct State {
    files: BTreeMap<String, Record>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Record {
    original: String,
    patched: String,
}

fn sha(data: &[u8]) -> String {
    format!("{:x}", Sha256::digest(data))
}

/// Raw file range of section `name`.
fn section_range(data: &[u8], name: &[u8]) -> Result<(usize, usize)> {
    let u16_at = |o: usize| -> Result<usize> {
        Ok(u16::from_le_bytes(data.get(o..o + 2).ok_or_else(|| anyhow!("truncated PE"))?.try_into()?) as usize)
    };
    let u32_at = |o: usize| -> Result<usize> {
        Ok(u32::from_le_bytes(data.get(o..o + 4).ok_or_else(|| anyhow!("truncated PE"))?.try_into()?) as usize)
    };
    ensure!(data.starts_with(b"MZ"), "not an MZ image");
    let pe = u32_at(0x3c)?;
    ensure!(data.get(pe..pe + 4) == Some(b"PE\0\0"), "not a PE image");
    let (count, optional) = (u16_at(pe + 6)?, u16_at(pe + 20)?);
    for i in 0..count {
        let row = pe + 24 + optional + 40 * i;
        let label = data.get(row..row + 8).ok_or_else(|| anyhow!("truncated PE section table"))?;
        if label.split(|&b| b == 0).next() == Some(name) {
            let (size, offset) = (u32_at(row + 16)?, u32_at(row + 20)?);
            ensure!(offset.checked_add(size).is_some_and(|end| end <= data.len()), "section exceeds file");
            return Ok((offset, offset + size));
        }
    }
    bail!("section {} not found (not a protected 4.9 GameAssembly.dll?)", String::from_utf8_lossy(name))
}

/// `[REX] 0F 1F C0-C7` directly followed by a load (`[REX] 8B`); the load is not part of the match.
/// Returns (offset, length) of every non-overlapping match in `data`.
fn register_nops(data: &[u8]) -> Vec<(usize, usize)> {
    let rex = |b: u8| (0x40..=0x4f).contains(&b);
    let mut hits = Vec::new();
    let mut i = 0;
    while i < data.len() {
        let start = i;
        let at = if rex(data[i]) { i + 1 } else { i };
        let len = at + 3 - start;
        let nop = data.get(at..at + 3).is_some_and(|b| b[0] == 0x0f && b[1] == 0x1f && (0xc0..=0xc7).contains(&b[2]));
        let load = |j: usize| match data.get(j) {
            Some(&b) if rex(b) => data.get(j + 1) == Some(&0x8b),
            Some(&b) => b == 0x8b,
            None => false,
        };
        if nop && load(at + 3) {
            hits.push((start, len));
            i += len;
        } else {
            i += 1;
        }
    }
    hits
}

#[derive(Debug)]
enum Patch {
    Patched(Vec<u8>),
    Already,
}

fn patch_game_assembly(data: &[u8], manifest: &Manifest) -> Result<Patch> {
    let (start, end) = section_range(data, SECTION)?;
    let hits = register_nops(&data[start..end]);
    match hits[..] {
        [] if KNOWN_PATCHED.contains(&sha(data).as_str()) => Ok(Patch::Already),
        [] => bail!("{GAME_ASSEMBLY}: Rosetta NOP signature not found and the file is not a known patched build; nothing written"),
        [(offset, len)] => {
            let mut out = data.to_vec();
            out[start + offset..start + offset + len].fill(0x90);
            let hash = sha(&out);
            ensure!(
                manifest.accepts_original(GAME_ASSEMBLY, Some(&hash)),
                "{GAME_ASSEMBLY}: patched result {hash} is not in supported-client.json; nothing written"
            );
            Ok(Patch::Patched(out))
        }
        _ => bail!("{GAME_ASSEMBLY}: Rosetta NOP signature matches {} times; nothing written", hits.len()),
    }
}

fn pgr_base_sites(data: &[u8], middle: [u8; 3]) -> Vec<usize> {
    let len = PGR_BASE_HEAD.len() + 3 + PGR_BASE_TAIL.len();
    data.windows(len)
        .enumerate()
        .filter(|(_, w)| w[..10] == PGR_BASE_HEAD && w[10..13] == middle && w[13..] == PGR_BASE_TAIL)
        .map(|(i, _)| i + PGR_BASE_HEAD.len())
        .collect()
}

fn patch_pgr_base(data: &[u8], manifest: &Manifest) -> Result<Patch> {
    let build = manifest.pgr_base.as_ref().context("supported-client.json has no PGRBase metadata")?;
    let sites = pgr_base_sites(data, NOP3);
    match sites[..] {
        [] => {
            // Already patched (possibly with the startup stub on top, which only `install` can verify).
            ensure!(pgr_base_sites(data, [0x90; 3]).len() == 1, "{PGR_BASE}: Rosetta NOP signature not found exactly once; nothing written");
            Ok(Patch::Already)
        }
        [offset] => {
            ensure!(
                build.originals.contains(&sha(data)),
                "{PGR_BASE}: not a supported stock build ({}); nothing written",
                sha(data)
            );
            let mut out = data.to_vec();
            out[offset..offset + 3].fill(0x90);
            Ok(Patch::Patched(out))
        }
        _ => bail!("{PGR_BASE}: Rosetta NOP signature matches {} times; nothing written", sites.len()),
    }
}

fn state_path(game: &Path) -> PathBuf {
    game.join(STATE_DIR).join("state.json")
}

fn read_state(game: &Path) -> Result<State> {
    match fs::read(state_path(game)) {
        Ok(bytes) => serde_json::from_slice(&bytes).context("invalid .ascnet-rosetta/state.json"),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(State::default()),
        Err(e) => Err(e.into()),
    }
}

/// Write via a sibling temp file and rename, so an interruption never leaves a half-written target.
fn write_atomic(path: &Path, data: &[u8]) -> Result<()> {
    let parent = path.parent().context("no parent directory")?;
    fs::create_dir_all(parent)?;
    let tmp = parent.join(format!(".{}.{}.tmp", path.file_name().unwrap().to_string_lossy(), Uuid::new_v4()));
    let result = (|| {
        let mut file = fs::File::create(&tmp)?;
        std::io::Write::write_all(&mut file, data)?;
        file.sync_all()?;
        fs::rename(&tmp, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    result.with_context(|| format!("writing {}", path.display()))
}

/// What [`inspect`] found in a game folder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RosettaState {
    /// Both files carry the Rosetta patch.
    Patched,
    /// At least one file is stock and Setup would patch it.
    Needed,
    /// A file is missing or matches no known build; the reason.
    Unsupported(String),
}

/// Read-only: what [`apply`] would do to `game`.
pub fn inspect(game: &Path) -> RosettaState {
    let manifest = match Manifest::bundled() {
        Ok(m) => m,
        Err(e) => return RosettaState::Unsupported(format!("{e:#}")),
    };
    let jobs: [(&str, fn(&[u8], &Manifest) -> Result<Patch>); 2] = [(PGR_BASE, patch_pgr_base), (GAME_ASSEMBLY, patch_game_assembly)];
    let mut needed = false;
    for (file, patch) in jobs {
        let data = match fs::read(game.join(file)) {
            Ok(d) => d,
            Err(e) => return RosettaState::Unsupported(format!("{file}: {e}")),
        };
        match patch(&data, &manifest) {
            Ok(Patch::Already) => {}
            Ok(Patch::Patched(_)) => needed = true,
            Err(e) => return RosettaState::Unsupported(format!("{e:#}")),
        }
    }
    if needed { RosettaState::Needed } else { RosettaState::Patched }
}

/// Applies the Rosetta patches to `game` (a PGR folder). Both files are checked before either is written.
pub fn apply(game: &Path) -> Result<Vec<Report>> {
    let manifest = Manifest::bundled()?;
    let jobs: [(&'static str, fn(&[u8], &Manifest) -> Result<Patch>); 2] =
        [(PGR_BASE, patch_pgr_base), (GAME_ASSEMBLY, patch_game_assembly)];
    let mut state = read_state(game)?;
    let mut plan = Vec::new();
    for (file, patch) in jobs {
        let path = game.join(file);
        let data = fs::read(&path).with_context(|| format!("reading {}", path.display()))?;
        plan.push((file, sha(&data), data.clone(), patch(&data, &manifest)?));
    }
    let mut reports = Vec::new();
    for (file, original, data, patch) in plan {
        reports.push(match patch {
            Patch::Already => {
                let record = state.files.get(file);
                Report {
                    file,
                    original_sha256: record.map(|r| r.original.clone()),
                    patched_sha256: original,
                    changed: false,
                }
            }
            Patch::Patched(out) => {
                let patched = sha(&out);
                // Backup and record first: an interrupted run is still restorable.
                write_atomic(&game.join(STATE_DIR).join("backups").join(file), &data)?;
                state.files.insert(file.into(), Record { original: original.clone(), patched: patched.clone() });
                write_atomic(&state_path(game), &serde_json::to_vec_pretty(&state)?)?;
                write_atomic(&game.join(file), &out)?;
                Report { file, original_sha256: Some(original), patched_sha256: patched, changed: true }
            }
        });
    }
    Ok(reports)
}

/// Puts back the recorded originals. A file that is neither our patched result nor the original (e.g. the game
/// updated it) is left alone; its record is dropped.
pub fn restore(game: &Path) -> Result<()> {
    let state = read_state(game)?;
    for (file, record) in &state.files {
        let path = game.join(file);
        let current = match fs::read(&path) {
            Ok(data) => Some(sha(&data)),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
            Err(e) => return Err(e.into()),
        };
        if current.as_deref() == Some(record.patched.as_str()) {
            let backup = fs::read(game.join(STATE_DIR).join("backups").join(file))
                .with_context(|| format!("reading Rosetta backup of {file}"))?;
            ensure!(sha(&backup) == record.original, "Rosetta backup of {file} does not match its recorded hash");
            write_atomic(&path, &backup)?;
        }
    }
    match fs::remove_dir_all(game.join(STATE_DIR)) {
        Err(e) if e.kind() != std::io::ErrorKind::NotFound => Err(e.into()),
        _ => Ok(()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Synthetic PE: one `.tvm0` section at raw offset 0x400 holding `body`.
    fn pe(body: &[u8]) -> Vec<u8> {
        let mut d = vec![0u8; 0x400];
        d[..2].copy_from_slice(b"MZ");
        d[0x3c] = 0x80;
        d[0x80..0x84].copy_from_slice(b"PE\0\0");
        d[0x86] = 1; // sections
        d[0x94] = 240; // optional header size
        let row = 0x80 + 24 + 240;
        d[row..row + 5].copy_from_slice(b".tvm0");
        d[row + 16..row + 20].copy_from_slice(&(body.len() as u32).to_le_bytes());
        d[row + 20..row + 24].copy_from_slice(&0x400u32.to_le_bytes());
        d.extend_from_slice(body);
        d
    }

    fn manifest() -> Manifest {
        Manifest::bundled().unwrap()
    }

    #[test]
    fn matches_en_and_cn_shapes_only_before_a_load() {
        assert_eq!(register_nops(&[1, 2, 0x41, 0x0f, 0x1f, 0xc1, 0x44, 0x8b, 0x0a]), vec![(2, 4)]);
        assert_eq!(register_nops(&[0x0f, 0x1f, 0xc2, 0x41, 0x8b, 0x11]), vec![(0, 3)]);
        assert!(register_nops(&[0x0f, 0x1f, 0xc2, 0x90, 0x8b]).is_empty()); // no load follows
        assert!(register_nops(&[0x0f, 0x1f, 0x00, 0x8b]).is_empty()); // memory form (mod != 11) is a real NOP
        assert_eq!(register_nops(&[0x0f, 0x1f, 0xc0, 0x8b, 0x0f, 0x1f, 0xc3, 0x8b]).len(), 2);
    }

    #[test]
    fn signature_must_be_unique_and_inside_tvm0() {
        let one = pe(&[0x90, 0x41, 0x0f, 0x1f, 0xc1, 0x44, 0x8b, 0x0a]);
        // The synthetic result is not a retail hash, so the supported-client check refuses to write.
        assert!(patch_game_assembly(&one, &manifest()).unwrap_err().to_string().contains("not in supported-client.json"));
        let two = pe(&[0x0f, 0x1f, 0xc2, 0x8b, 0x00, 0x0f, 0x1f, 0xc3, 0x8b]);
        assert!(patch_game_assembly(&two, &manifest()).unwrap_err().to_string().contains("2 times"));
        let zero = pe(&[0x90; 8]);
        assert!(patch_game_assembly(&zero, &manifest()).unwrap_err().to_string().contains("not found"));
        // A match outside .tvm0 is ignored.
        let mut outside = pe(&[0x90; 8]);
        outside.extend_from_slice(&[0x0f, 0x1f, 0xc2, 0x8b]);
        assert!(patch_game_assembly(&outside, &manifest()).is_err());
        assert!(section_range(&[0u8; 8], SECTION).is_err());
    }

    fn pgr_base(middle: [u8; 3]) -> Vec<u8> {
        let mut d = vec![0xcc; 5];
        d.extend_from_slice(&PGR_BASE_HEAD);
        d.extend_from_slice(&middle);
        d.extend_from_slice(&PGR_BASE_TAIL);
        d
    }

    #[test]
    fn pgr_base_signature_states() {
        let m = manifest();
        // Synthetic stock file is not a supported hash.
        assert!(patch_pgr_base(&pgr_base(NOP3), &m).unwrap_err().to_string().contains("not a supported stock"));
        assert!(matches!(patch_pgr_base(&pgr_base([0x90; 3]), &m).unwrap(), Patch::Already));
        assert!(patch_pgr_base(&[0xcc; 64], &m).is_err());
        let mut twice = pgr_base(NOP3);
        twice.extend(pgr_base(NOP3));
        assert!(patch_pgr_base(&twice, &m).unwrap_err().to_string().contains("2 times"));
    }

    /// Real EN 4.9.1 original, copied out of the game folder (`/tmp/rosetta-test/GameAssembly-en-4.9.1.dll`, or
    /// `ASCNET_TEST_GAMEASSEMBLY`). Skipped when absent.
    #[test]
    fn en_4_9_1_reproduces_installed_hash_and_restores() {
        let source = std::env::var("ASCNET_TEST_GAMEASSEMBLY").unwrap_or("/tmp/rosetta-test/GameAssembly-en-4.9.1.dll".into());
        let Ok(original) = fs::read(&source) else { return eprintln!("skipped: {source} missing") };
        assert_eq!(sha(&original), "03aeca92e47204fc242fb839d32c22721d4fdd1095fdd999ccd95255ca81b436");
        let Patch::Patched(out) = patch_game_assembly(&original, &manifest()).unwrap() else { panic!("not patched") };
        assert_eq!(sha(&out), KNOWN_PATCHED[0]);
        assert!(matches!(patch_game_assembly(&out, &manifest()).unwrap(), Patch::Already));

        // Folder round trip: only GameAssembly.dll is real; PGRBase is a synthetic already-patched file.
        let game = std::env::temp_dir().join(format!("ascnet-rosetta-test-{}", Uuid::new_v4()));
        fs::create_dir_all(&game).unwrap();
        fs::write(game.join(GAME_ASSEMBLY), &original).unwrap();
        fs::write(game.join(PGR_BASE), pgr_base([0x90; 3])).unwrap();
        let first = apply(&game).unwrap();
        assert_eq!(first[1].patched_sha256, KNOWN_PATCHED[0]);
        assert_eq!(first[1].original_sha256.as_deref(), Some(sha(&original).as_str()));
        assert!(first[1].changed && !first[0].changed);
        let again = apply(&game).unwrap();
        assert!(!again[1].changed);
        assert_eq!(again[1].original_sha256, first[1].original_sha256);
        restore(&game).unwrap();
        assert_eq!(fs::read(game.join(GAME_ASSEMBLY)).unwrap(), original);
        assert!(!game.join(STATE_DIR).exists());
        fs::remove_dir_all(&game).unwrap();
    }
}
