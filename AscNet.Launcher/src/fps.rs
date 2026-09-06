use crate::install;
use aes::Aes128;
use anyhow::{bail, Context, Result};
use ctr::cipher::{KeyIvInit, StreamCipher};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use uuid::Uuid;

type Aes128Ctr = ctr::Ctr128BE<Aes128>;

const LOGICAL_ASSET: &str = "assets/temp/lua/matrix.ab";
const TEXT_ASSET: &[u8] = b"XUiMain.lua";
const MARKER: &str = "PgrNativeFpsTimer";
const KEY: &[u8; 16] = b"XxecodrPeGaka2e6";
const ANCHOR: &str = "    CS.XInputManager.SetCurInputMap(CS.XInputMapId.System)";
const START: &str = "function XUiMain:OnStart()";
const MAX_BUNDLE_SIZE: u64 = 512 * 1024 * 1024;
const MAX_BLOCKS: usize = 65_536;
const MAX_NODES: usize = 65_536;
const MAX_MSGPACK_DEPTH: usize = 64;

#[derive(Clone)]
struct Header {
    fields: usize,
    file_size: usize,
    info_compressed: usize,
    info_plain: usize,
    flags: u32,
    header_size: usize,
}
#[derive(Clone)]
struct Block {
    plain: usize,
    compressed: usize,
    flags: u16,
}
struct Directory {
    plain: Vec<u8>,
    blocks: Vec<Block>,
    info_offset: usize,
}
struct Decoded {
    header: Header,
    directory: Directory,
    payload: Vec<u8>,
    context: [u8; 32],
}
struct Active {
    path: PathBuf,
    scope: &'static str,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct BackupMetadata<'a> {
    schema_version: u32,
    bundle: &'a str,
    original_sha256: &'a str,
    replacement_sha256: &'a str,
}

pub fn inspect(game: &Path) -> Result<Option<i32>> {
    let active = resolve(game)?;
    let bytes = read_bounded(&active.path)?;
    let decoded = decode(&bytes)?;
    let (_, content) = text_asset(&decoded.payload)?;
    owned_fps(std::str::from_utf8(content).context("XUiMain.lua is not UTF-8")?)
}

pub fn apply(game: &Path, fps: i32) -> Result<()> {
    if fps <= 0 {
        bail!("FPS must be positive");
    }
    mutate(game, Some(fps))
}

pub fn disable(game: &Path) -> Result<()> {
    mutate(game, None)
}

fn mutate(game: &Path, wanted: Option<i32>) -> Result<()> {
    if install::game_running()? {
        bail!("refusing FPS changes while PGR.exe is running");
    }
    let game = fs::canonicalize(game)
        .with_context(|| format!("invalid game directory: {}", game.display()))?;
    let active = resolve(&game)?;
    if active.scope != "document" {
        bail!("refusing to patch packaged resource bundle; start the game once to create its document override");
    }
    let original = read_bounded(&active.path)?;
    let decoded = decode(&original)?;
    let (content_offset, content) = text_asset(&decoded.payload)?;
    let text = std::str::from_utf8(content).context("XUiMain.lua is not UTF-8")?;
    let current = owned_fps(text)?;
    if current == wanted {
        return Ok(());
    }
    let changed = change_lua(text, wanted)?;
    let replacement = fit_same_size(content, &changed)?;
    let output = rewrite(&original, decoded, content_offset, replacement)?;

    // Reject an updater race before creating a provenance record or committing.
    if sha256(&read_bounded(&active.path)?) != sha256(&original) {
        bail!(
            "matrix bundle changed while preparing the FPS patch; retry after the update finishes"
        );
    }
    save_backup(&game, &active.path, &original, &output)?;
    write_atomic(&active.path, &output)?;
    Ok(())
}

fn resolve(game: &Path) -> Result<Active> {
    let game = fs::canonicalize(game)
        .with_context(|| format!("invalid game directory: {}", game.display()))?;
    if !game.join("PGR.exe").is_file() {
        bail!("game directory does not contain PGR.exe");
    }
    for scope in ["document", "resource"] {
        let root = game
            .join("PGR_Data/StreamingAssets")
            .join(scope)
            .join("matrix");
        let index = root.join("index");
        if !index.is_file() {
            continue;
        }
        let payload = decode(&read_bounded(&index)?)?.payload;
        if let Some(name) = matrix_name(&payload)? {
            let path = root.join(name);
            let canonical = fs::canonicalize(&path)
                .with_context(|| format!("matrix bundle is missing: {}", path.display()))?;
            if !canonical.starts_with(&root) || !canonical.is_file() {
                bail!("matrix index resolves outside its asset directory");
            }
            return Ok(Active {
                path: canonical,
                scope,
            });
        }
    }
    bail!("active matrix bundle not found for {LOGICAL_ASSET}")
}

fn read_bounded(path: &Path) -> Result<Vec<u8>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        bail!("refusing non-regular asset: {}", path.display());
    }
    if metadata.len() > MAX_BUNDLE_SIZE {
        bail!("asset is too large: {}", path.display());
    }
    fs::read(path).with_context(|| format!("reading {}", path.display()))
}

fn decode(data: &[u8]) -> Result<Decoded> {
    let header = header(data)?;
    let (directory, context) = directory(data, &header)?;
    let start = align16(checked_add(directory.info_offset, header.info_compressed)?)?;
    let mut cursor = start;
    let total = directory
        .blocks
        .iter()
        .try_fold(0usize, |sum, b| checked_add(sum, b.plain))?;
    if total > MAX_BUNDLE_SIZE as usize {
        bail!("UnityFS payload is too large");
    }
    let mut payload = Vec::with_capacity(total);
    for (index, block) in directory.blocks.iter().enumerate() {
        let end = checked_add(cursor, block.compressed)?;
        let mut compressed = slice(data, cursor, end)?.to_vec();
        if matches!(block.flags & 0x3f, 2 | 3) {
            transform_controls(&mut compressed, &context, index, false)?;
        }
        payload.extend(decompress(&compressed, block.flags & 0x3f, block.plain)?);
        cursor = end;
    }
    if cursor != data.len() {
        bail!("unexpected UnityFS trailing bytes");
    }
    Ok(Decoded {
        header,
        directory,
        payload,
        context,
    })
}

fn header(data: &[u8]) -> Result<Header> {
    let (signature, mut offset) = cstring(data, 0)?;
    if signature != b"UnityFS" {
        bail!("not a UnityFS bundle");
    }
    offset = checked_add(offset, 4)?;
    (_, offset) = cstring(data, offset)?;
    (_, offset) = cstring(data, offset)?;
    let fields = offset;
    let file_size = usize::try_from(be_u64(data, offset)?)
        .context("UnityFS file size exceeds this platform")?;
    let info_compressed = be_u32(data, offset + 8)? as usize;
    let info_plain = be_u32(data, offset + 12)? as usize;
    let flags = be_u32(data, offset + 16)?;
    let header_size = checked_add(offset, 20)?;
    if file_size != data.len() || info_compressed == 0 || info_plain > MAX_BUNDLE_SIZE as usize {
        bail!("invalid UnityFS size fields");
    }
    if flags & 0x80 != 0 {
        bail!("UnityFS directory-at-end layout is unsupported");
    }
    Ok(Header {
        fields,
        file_size,
        info_compressed,
        info_plain,
        flags,
        header_size,
    })
}

fn directory(data: &[u8], header: &Header) -> Result<(Directory, [u8; 32])> {
    if header.flags & 0x1000 == 0 {
        bail!("matrix bundle is not PGR-protected UnityFS");
    }
    let protected_start = if header.flags & 0x80 != 0 {
        header
            .file_size
            .checked_sub(header.info_compressed)
            .context("invalid directory offset")?
    } else if header.flags & 0x400 != 0 {
        align16(header.header_size)?
    } else {
        header.header_size
    };
    let (metadata_end, context) = protected_metadata(data, protected_start)?;
    for skip in 0..16usize {
        let start = checked_add(metadata_end, skip)?;
        let end = checked_add(start, header.info_compressed)?;
        let Some(compressed) = data.get(start..end) else {
            continue;
        };
        let Ok(plain) = decompress(compressed, (header.flags & 0x3f) as u16, header.info_plain)
        else {
            continue;
        };
        if let Ok(blocks) = parse_directory(&plain) {
            return Ok((
                Directory {
                    plain,
                    blocks,
                    info_offset: start,
                },
                context,
            ));
        }
    }
    bail!("unable to locate protected UnityFS directory")
}

fn protected_metadata(data: &[u8], start: usize) -> Result<(usize, [u8; 32])> {
    let mut offset = checked_add(start, 4)?;
    let (first, next) = cstring(data, offset)?;
    offset = next;
    (_, offset) = cstring(data, offset)?;
    if first.len() != 32 {
        bail!("invalid protected UnityFS seed");
    }
    let mut decrypted = first[..16].to_vec();
    let mut cipher = Aes128Ctr::new_from_slices(KEY, &first[16..])
        .map_err(|_| anyhow::anyhow!("invalid protected UnityFS AES parameters"))?;
    cipher.apply_keystream(&mut decrypted);
    let mut context = [0u8; 32];
    for index in 0..16 {
        let nibble = if index & 1 == 0 {
            decrypted[index / 2] >> 4
        } else {
            decrypted[index / 2] & 15
        };
        context[nibble as usize] = index as u8;
    }
    let positions = [
        16, 20, 24, 28, 17, 21, 25, 29, 18, 22, 26, 30, 19, 23, 27, 31,
    ];
    for (i, position) in positions.into_iter().enumerate() {
        let byte = decrypted[8 + i / 2];
        context[position] = if i & 1 == 0 { byte >> 4 } else { byte & 15 };
    }
    Ok((offset, context))
}

fn parse_directory(data: &[u8]) -> Result<Vec<Block>> {
    let mut offset = 16usize;
    let count = be_u32(data, offset)? as usize;
    offset += 4;
    if count == 0 || count > MAX_BLOCKS {
        bail!("invalid UnityFS block count");
    }
    let mut blocks = Vec::with_capacity(count);
    for _ in 0..count {
        blocks.push(Block {
            plain: be_u32(data, offset)? as usize,
            compressed: be_u32(data, offset + 4)? as usize,
            flags: be_u16(data, offset + 8)?,
        });
        offset = checked_add(offset, 10)?;
    }
    let nodes = be_u32(data, offset)? as usize;
    offset += 4;
    if nodes > MAX_NODES {
        bail!("invalid UnityFS node count");
    }
    for _ in 0..nodes {
        offset = checked_add(offset, 20)?;
        (_, offset) = cstring(data, offset)?;
    }
    if offset != data.len() {
        bail!("invalid UnityFS directory length");
    }
    Ok(blocks)
}

fn decompress(data: &[u8], compression: u16, expected: usize) -> Result<Vec<u8>> {
    match compression {
        0 if data.len() == expected => Ok(data.to_vec()),
        0 => bail!("uncompressed UnityFS size mismatch"),
        2 | 3 => lz4_flex::block::decompress(data, expected).context("invalid UnityFS LZ4 block"),
        value => bail!("unsupported UnityFS compression {value}"),
    }
}

fn transform_controls(
    data: &mut [u8],
    context: &[u8; 32],
    initial: usize,
    encode: bool,
) -> Result<()> {
    let mut offset = 0usize;
    let mut sequence = initial;
    while offset < data.len() {
        let token = if encode {
            data[offset]
        } else {
            transform_byte(data[offset], context, sequence, false)?
        };
        data[offset] = if encode {
            transform_byte(token, context, sequence, true)?
        } else {
            token
        };
        offset += 1;
        sequence = checked_add(sequence, 1)?;
        let mut control = sequence;
        let mut literals = (token >> 4) as usize;
        if literals == 15 {
            loop {
                if offset >= data.len() {
                    bail!("truncated protected LZ4 literal length");
                }
                let value = if encode {
                    data[offset]
                } else {
                    transform_byte(data[offset], context, control, false)?
                };
                data[offset] = if encode {
                    transform_byte(value, context, control, true)?
                } else {
                    value
                };
                offset += 1;
                control += 1;
                literals = checked_add(literals, value as usize)?;
                if value != 255 {
                    break;
                }
            }
        }
        offset = checked_add(offset, literals)?;
        if offset > data.len() {
            bail!("protected LZ4 literal overruns block");
        }
        if offset == data.len() {
            break;
        }
        if offset + 2 > data.len() {
            bail!("truncated protected LZ4 match offset");
        }
        for byte in &mut data[offset..offset + 2] {
            *byte = transform_byte(*byte, context, control, encode)?;
            control += 1;
        }
        offset += 2;
        if token & 15 == 15 {
            loop {
                if offset >= data.len() {
                    bail!("truncated protected LZ4 match length");
                }
                let plain = if encode {
                    data[offset]
                } else {
                    transform_byte(data[offset], context, control, false)?
                };
                data[offset] = if encode {
                    transform_byte(plain, context, control, true)?
                } else {
                    plain
                };
                offset += 1;
                control += 1;
                if plain != 255 {
                    break;
                }
            }
        }
    }
    Ok(())
}

fn transform_byte(value: u8, context: &[u8; 32], counter: usize, encode: bool) -> Result<u8> {
    let shift = context[28 + ((counter >> 6) & 3)]
        .wrapping_add(context[24 + ((counter >> 4) & 3)])
        .wrapping_add(context[20 + ((counter >> 2) & 3)])
        .wrapping_add(context[16 + (counter & 3)]);
    if !encode {
        return Ok(
            ((context[(value >> 4) as usize].wrapping_sub(shift) & 15) << 4)
                | (context[(value & 15) as usize].wrapping_sub(shift) & 15),
        );
    }
    let find = |wanted: u8| {
        context[..16]
            .iter()
            .position(|n| n.wrapping_sub(shift) & 15 == wanted)
            .map(|n| n as u8)
    };
    Ok(
        (find(value >> 4).context("invalid protected LZ4 context")? << 4)
            | find(value & 15).context("invalid protected LZ4 context")?,
    )
}

fn text_asset(payload: &[u8]) -> Result<(usize, &[u8])> {
    let mut search = 0usize;
    while let Some(relative) = payload
        .get(search..)
        .and_then(|p| p.windows(TEXT_ASSET.len()).position(|w| w == TEXT_ASSET))
    {
        let name = checked_add(search, relative)?;
        if name >= 4 && le_u32(payload, name - 4)? as usize == TEXT_ASSET.len() {
            let size_at = align4(checked_add(name, TEXT_ASSET.len())?)?;
            let size = le_u32(payload, size_at)? as usize;
            let content = checked_add(size_at, 4)?;
            let end = checked_add(content, size)?;
            if let Some(value) = payload.get(content..end) {
                return Ok((content, value));
            }
        }
        search = checked_add(name, 1)?;
    }
    bail!("XUiMain.lua TextAsset not found")
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

fn hook(fps: i32) -> String {
    format!("    if not XUiMain.PgrNativeFpsTimer then local f=function() CS.UnityEngine.Application.targetFrameRate={fps} end f() XUiMain.PgrNativeFpsTimer=XScheduleManager.ScheduleForever(f,2000) end")
}

fn change_lua(text: &str, wanted: Option<i32>) -> Result<String> {
    let current = owned_fps(text)?;
    match (current, wanted) {
        (Some(_), Some(fps)) => {
            let mut lines = text
                .split_inclusive('\n')
                .map(str::to_owned)
                .collect::<Vec<_>>();
            let index = lines.iter().position(|l| l.contains(MARKER)).unwrap();
            let ending = if lines[index].ends_with("\r\n") {
                "\r\n"
            } else if lines[index].ends_with('\n') {
                "\n"
            } else {
                ""
            };
            lines[index] = hook(fps) + ending;
            Ok(lines.concat())
        }
        (None, Some(fps)) => {
            let newline = if text.contains("\r\n") { "\r\n" } else { "\n" };
            let count = text.match_indices(ANCHOR).count();
            if count != 1 {
                bail!("expected exactly one XUiMain input-map anchor, found {count}");
            }
            Ok(text.replacen(ANCHOR, &(ANCHOR.to_owned() + newline + &hook(fps)), 1))
        }
        (Some(_), None) => Ok(text
            .split_inclusive('\n')
            .filter(|line| !line.contains(MARKER))
            .collect()),
        (None, None) => Ok(text.to_owned()),
    }
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

fn rewrite(
    original: &[u8],
    mut decoded: Decoded,
    content_offset: usize,
    replacement: Vec<u8>,
) -> Result<Vec<u8>> {
    let end = checked_add(content_offset, replacement.len())?;
    decoded
        .payload
        .get_mut(content_offset..end)
        .context("replacement exceeds payload")?
        .copy_from_slice(&replacement);
    let data_start = align16(decoded.directory.info_offset + decoded.header.info_compressed)?;
    let mut cursor = data_start;
    let mut logical = 0usize;
    let mut compressed = Vec::with_capacity(decoded.directory.blocks.len());
    for (index, block) in decoded.directory.blocks.iter().enumerate() {
        let old_end = checked_add(cursor, block.compressed)?;
        let old = slice(original, cursor, old_end)?;
        let plain_end = checked_add(logical, block.plain)?;
        let changed = slice(&decoded.payload, logical, plain_end)?;
        let unpacked = if matches!(block.flags & 0x3f, 2 | 3) {
            transform_copy(old, &decoded.context, index)?
        } else {
            old.to_vec()
        };
        let old_plain = decompress(&unpacked, block.flags & 0x3f, block.plain)?;
        if old_plain == changed {
            compressed.push(old.to_vec());
        } else {
            if !matches!(block.flags & 0x3f, 2 | 3) {
                bail!("changed UnityFS block is not LZ4");
            }
            let mut packed = lz4_flex::block::compress(changed);
            transform_controls(&mut packed, &decoded.context, index, true)?;
            compressed.push(packed);
        }
        cursor = old_end;
        logical = plain_end;
    }
    for (index, block) in compressed.iter().enumerate() {
        let at = 20 + index * 10 + 4;
        let size = u32::try_from(block.len()).context("compressed block exceeds UnityFS limit")?;
        decoded
            .directory
            .plain
            .get_mut(at..at + 4)
            .context("directory block record missing")?
            .copy_from_slice(&size.to_be_bytes());
    }
    let info = lz4_flex::block::compress(&decoded.directory.plain);
    let new_start = align16(decoded.directory.info_offset + info.len())?;
    let mut output = original[..decoded.directory.info_offset].to_vec();
    output.extend(&info);
    output.resize(new_start, 0);
    for block in compressed {
        output.extend(block);
    }
    let size = u64::try_from(output.len())?;
    let info_size = u32::try_from(info.len())?;
    output[decoded.header.fields..decoded.header.fields + 8].copy_from_slice(&size.to_be_bytes());
    output[decoded.header.fields + 8..decoded.header.fields + 12]
        .copy_from_slice(&info_size.to_be_bytes());
    let verified = decode(&output)?;
    let (_, content) = text_asset(&verified.payload)?;
    if content != replacement {
        bail!("generated bundle failed content verification");
    }
    Ok(output)
}

fn transform_copy(data: &[u8], context: &[u8; 32], index: usize) -> Result<Vec<u8>> {
    let mut value = data.to_vec();
    transform_controls(&mut value, context, index, false)?;
    Ok(value)
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
    sync_dir(root.parent().unwrap())
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
        path.file_name().unwrap().to_string_lossy(),
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

fn matrix_name(payload: &[u8]) -> Result<Option<String>> {
    for offset in payload
        .iter()
        .enumerate()
        .filter_map(|(i, b)| (*b == 0x93).then_some(i))
    {
        if let Ok((Value::Array(values), _)) = msgpack(payload, offset, 0) {
            if values.len() == 3 {
                if let Value::Map(entries) = &values[0] {
                    for (key, value) in entries {
                        if key == LOGICAL_ASSET {
                            if let Value::Array(parts) = value {
                                if let Some(Value::String(name)) = parts.first() {
                                    if Path::new(name).components().count() == 1 {
                                        return Ok(Some(name.clone()));
                                    }
                                    bail!("unsafe matrix bundle name in index");
                                }
                            }
                            bail!("invalid matrix index entry");
                        }
                    }
                }
            }
        }
    }
    Ok(None)
}

enum Value {
    String(String),
    Array(Vec<Value>),
    Map(Vec<(String, Value)>),
    Other,
}
fn msgpack(data: &[u8], mut offset: usize, depth: usize) -> Result<(Value, usize)> {
    if depth > MAX_MSGPACK_DEPTH {
        bail!("MessagePack nesting is too deep");
    }
    let marker = *data.get(offset).context("truncated MessagePack value")?;
    offset += 1;
    let (kind, count) = match marker {
        0x80..=0x8f => (0, (marker & 15) as usize),
        0x90..=0x9f => (1, (marker & 15) as usize),
        0xa0..=0xbf => {
            let n = (marker & 31) as usize;
            return string_value(data, offset, n);
        }
        0xd9 => {
            let n = *data.get(offset).context("truncated MessagePack string")? as usize;
            return string_value(data, offset + 1, n);
        }
        0xda => {
            let n = be_u16(data, offset)? as usize;
            return string_value(data, offset + 2, n);
        }
        0xdc => (1, be_u16(data, offset)? as usize),
        0xde => (0, be_u16(data, offset)? as usize),
        0..=0x7f | 0xe0..=0xff | 0xc0 | 0xc2 | 0xc3 => return Ok((Value::Other, offset)),
        0xcc | 0xd0 => return Ok((Value::Other, checked_add(offset, 1)?)),
        0xcd | 0xd1 => return Ok((Value::Other, checked_add(offset, 2)?)),
        0xce | 0xd2 => return Ok((Value::Other, checked_add(offset, 4)?)),
        0xcf | 0xd3 => return Ok((Value::Other, checked_add(offset, 8)?)),
        _ => bail!("unsupported MessagePack marker 0x{marker:02x}"),
    };
    if marker == 0xdc || marker == 0xde {
        offset += 2;
    }
    if count > MAX_NODES {
        bail!("MessagePack collection is too large");
    }
    if kind == 1 {
        let mut values = Vec::with_capacity(count);
        for _ in 0..count {
            let (value, next) = msgpack(data, offset, depth + 1)?;
            values.push(value);
            offset = next;
        }
        Ok((Value::Array(values), offset))
    } else {
        let mut values = Vec::with_capacity(count);
        for _ in 0..count {
            let (key, next) = msgpack(data, offset, depth + 1)?;
            offset = next;
            let (value, next) = msgpack(data, offset, depth + 1)?;
            offset = next;
            let Value::String(key) = key else { continue };
            values.push((key, value));
        }
        Ok((Value::Map(values), offset))
    }
}
fn string_value(data: &[u8], offset: usize, length: usize) -> Result<(Value, usize)> {
    let end = checked_add(offset, length)?;
    let value = std::str::from_utf8(slice(data, offset, end)?)?.to_owned();
    Ok((Value::String(value), end))
}

fn sha256(data: &[u8]) -> String {
    format!("{:x}", Sha256::digest(data))
}
fn checked_add(a: usize, b: usize) -> Result<usize> {
    a.checked_add(b).context("asset offset overflow")
}
fn align4(n: usize) -> Result<usize> {
    checked_add(n, 3).map(|v| v & !3)
}
fn align16(n: usize) -> Result<usize> {
    checked_add(n, 15).map(|v| v & !15)
}
fn slice(data: &[u8], start: usize, end: usize) -> Result<&[u8]> {
    data.get(start..end).context("truncated asset")
}
fn cstring(data: &[u8], offset: usize) -> Result<(&[u8], usize)> {
    let rest = data.get(offset..).context("string offset exceeds asset")?;
    let end = rest
        .iter()
        .position(|b| *b == 0)
        .context("unterminated asset string")?;
    Ok((&rest[..end], checked_add(offset, end + 1)?))
}
fn be_u16(data: &[u8], at: usize) -> Result<u16> {
    Ok(u16::from_be_bytes(
        slice(data, at, checked_add(at, 2)?)?.try_into().unwrap(),
    ))
}
fn be_u32(data: &[u8], at: usize) -> Result<u32> {
    Ok(u32::from_be_bytes(
        slice(data, at, checked_add(at, 4)?)?.try_into().unwrap(),
    ))
}
fn be_u64(data: &[u8], at: usize) -> Result<u64> {
    Ok(u64::from_be_bytes(
        slice(data, at, checked_add(at, 8)?)?.try_into().unwrap(),
    ))
}
fn le_u32(data: &[u8], at: usize) -> Result<u32> {
    Ok(u32::from_le_bytes(
        slice(data, at, checked_add(at, 4)?)?.try_into().unwrap(),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn malformed_hook_is_never_removed() {
        let text = "PgrNativeFpsTimer = somebody_else\n";
        assert!(owned_fps(text).is_err());
        assert!(change_lua(text, None).is_err());
    }
    #[test]
    fn safe_padding_round_trip_preserves_other_bytes() {
        let stock = format!("{ANCHOR}\n{}{START}\nkeep\n", " ".repeat(256));
        let applied = change_lua(&stock, Some(240)).unwrap();
        let fitted = fit_same_size(stock.as_bytes(), &applied).unwrap();
        let patched = std::str::from_utf8(&fitted).unwrap();
        assert_eq!(owned_fps(patched).unwrap(), Some(240));
        let disabled = change_lua(patched, None).unwrap();
        let restored = fit_same_size(&fitted, &disabled).unwrap();
        assert_eq!(
            owned_fps(std::str::from_utf8(&restored).unwrap()).unwrap(),
            None
        );
        assert!(std::str::from_utf8(&restored)
            .unwrap()
            .ends_with("function XUiMain:OnStart()\nkeep\n"));
    }
    #[test]
    fn corrupt_lengths_are_rejected() {
        assert!(header(b"UnityFS\0").is_err());
        assert!(transform_controls(&mut [0xf0], &[0; 32], 0, true).is_err());
    }
}
