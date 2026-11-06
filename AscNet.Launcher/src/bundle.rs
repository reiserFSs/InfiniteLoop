use aes::Aes128;
use anyhow::{bail, Context, Result};
use ctr::cipher::{KeyIvInit, StreamCipher};
use std::fs;
use std::collections::HashSet;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use sha1::{Digest, Sha1};
type Aes128Ctr = ctr::Ctr128BE<Aes128>;
const LOGICAL_ASSET: &str = "assets/temp/lua/matrix.ab";
const KEY: &[u8; 16] = b"XxecodrPeGaka2e6";
const MAX_BUNDLE_SIZE: u64 = 512 * 1024 * 1024;
const MAX_BLOCKS: usize = 65_536;
const MAX_NODES: usize = 65_536;
const MAX_MSGPACK_DEPTH: usize = 64;
/// 4.9 prefixes every bundle with `HARUINKUROGAME` + zeros; the UnityFS header (and its `file_size`) starts after it.
const PREFIX_MAGIC: &[u8] = b"HARUINKUROGAME";
const PREFIX_LEN: usize = 32;
const MAX_ENTRIES: usize = 1 << 22;
const SCAN_HEAD: usize = 256 * 1024;
const MAX_TYPES: usize = 4096;
const ASSET_BUNDLE_CLASS: u32 = 142;

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
    data_start: usize,
    serialized: Option<usize>,
}
pub(crate) struct Decoded {
    header: Header,
    directory: Directory,
    pub(crate) payload: Vec<u8>,
    context: Option<[u8; 32]>,
    prefix: Vec<u8>,
}
pub(crate) struct Active {
    pub(crate) path: PathBuf,
    pub(crate) scope: &'static str,
}
/// One row of a 4.9 index: physical file, whole-file sha1 (hex) and size, prefix included.
struct Entry {
    file: String,
    sha1: String,
    size: u64,
}
const STREAMING: &str = "PGR_Data/StreamingAssets";
pub(crate) fn resolve(game: &Path) -> Result<Active> {
    let game = fs::canonicalize(game)
        .with_context(|| format!("invalid game directory: {}", game.display()))?;
    if !game.join("PGR.exe").is_file() {
        bail!("game directory does not contain PGR.exe");
    }
    let mut tables: Vec<(&'static str, PathBuf, Option<Vec<Entry>>)> = Vec::new();
    for scope in ["document", "resource"] {
        let root = game.join(STREAMING).join(scope).join("matrix");
        let mut table = None;
        // 4.8 and 4.9 document: `index`; 4.9 resource: `InPkgIndex`.
        for index in ["index", "InPkgIndex"] {
            let path = root.join(index);
            if !path.is_file() {
                continue;
            }
            let payload = decode_index(&read_bounded(&path)?)?.payload;
            // 4.8: the index names the bundle of each logical asset.
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
            // 4.9: entries are keyed by physical file; the logical name only lives inside the bundle.
            if table.is_none() {
                table = file_table(&payload)?;
            }
        }
        tables.push((scope, root, table));
    }
    let document_root = game.join(STREAMING).join("document").join("matrix");
    let snapshot: Option<HashSet<&str>> = tables
        .iter()
        .find(|(scope, ..)| *scope == "document")
        .and_then(|(.., table)| table.as_ref())
        .map(|table| table.iter().map(|entry| entry.file.as_str()).collect());
    for (scope, root, table) in &tables {
        let Some(table) = table else { continue };
        let mut hits = Vec::new();
        for entry in table {
            let file = entry.file.as_str();
            if !file.ends_with(".uab") || Path::new(file).components().count() != 1 {
                continue;
            }
            // The document index is a full snapshot: a resource file it omits is obsolete, and a document
            // file with the same physical name is the patched copy of it.
            if *scope == "resource"
                && (snapshot.as_ref().is_some_and(|names| !names.contains(file))
                    || document_root.join(file).exists())
            {
                continue;
            }
            let path = root.join(file);
            // ponytail: unreadable/unsupported bundles are skipped; a damaged scan ends in "not found" below.
            if bundle_name(&path).ok().flatten().as_deref() == Some(LOGICAL_ASSET) {
                hits.push((path, entry));
            }
        }
        let path = match hits.len() {
            0 => continue,
            1 => hits.swap_remove(0).0,
            count => {
                // Several claims: only the file still matching its index entry is unmodified.
                let mut intact = Vec::new();
                for (path, entry) in hits {
                    if matches_entry(&path, entry)? {
                        intact.push(path);
                    }
                }
                if intact.len() != 1 {
                    bail!("ambiguous matrix bundle: {count} files claim {LOGICAL_ASSET}");
                }
                intact.swap_remove(0)
            }
        };
        let canonical = fs::canonicalize(&path)
            .with_context(|| format!("matrix bundle is missing: {}", path.display()))?;
        if !canonical.starts_with(root) || !canonical.is_file() {
            bail!("matrix index resolves outside its asset directory");
        }
        return Ok(Active {
            path: canonical,
            scope,
        });
    }
    bail!("active matrix bundle not found for {LOGICAL_ASSET}")
}

fn matches_entry(path: &Path, entry: &Entry) -> Result<bool> {
    let data = read_bounded(path)?;
    Ok(data.len() as u64 == entry.size
        && format!("{:x}", Sha1::digest(&data)).eq_ignore_ascii_case(&entry.sha1))
}

pub(crate) fn read_bounded(path: &Path) -> Result<Vec<u8>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        bail!("refusing non-regular asset: {}", path.display());
    }
    if metadata.len() > MAX_BUNDLE_SIZE {
        bail!("asset is too large: {}", path.display());
    }
    fs::read(path).with_context(|| format!("reading {}", path.display()))
}

/// Matrix bundles only: must be PGR-protected.
pub(crate) fn decode(data: &[u8]) -> Result<Decoded> {
    decode_with(data, false)
}

/// Index bundles: protected in 4.8, plain LZ4HC in 4.9.
pub(crate) fn decode_index(data: &[u8]) -> Result<Decoded> {
    decode_with(data, true)
}

fn split_prefix(data: &[u8]) -> (&[u8], &[u8]) {
    let at = if data.len() >= PREFIX_LEN && data.starts_with(PREFIX_MAGIC) { PREFIX_LEN } else { 0 };
    data.split_at(at)
}

fn decode_with(data: &[u8], allow_plain: bool) -> Result<Decoded> {
    let (prefix, data) = split_prefix(data);
    let header = header(data, data.len())?;
    let (directory, context) = directory(data, &header, allow_plain)?;
    let mut cursor = directory.data_start;
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
        if let (Some(context), 2 | 3) = (&context, block.flags & 0x3f) {
            transform_controls(&mut compressed, context, index, false)?;
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
        prefix: prefix.to_vec(),
    })
}

/// `total`: length of the whole bundle after the prefix (`data` may be only its head).
fn header(data: &[u8], total: usize) -> Result<Header> {
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
    if file_size != total || info_compressed == 0 || info_plain > MAX_BUNDLE_SIZE as usize {
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

fn directory(data: &[u8], header: &Header, allow_plain: bool) -> Result<(Directory, Option<[u8; 32]>)> {
    let protected = header.flags & 0x1000 != 0;
    if !protected && !allow_plain {
        bail!("matrix bundle is not PGR-protected UnityFS");
    }
    let (metadata_end, context, skips) = if protected {
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
        let (end, context) = protected_metadata(data, protected_start)?;
        (end, Some(context), 16)
    } else if header.flags & 0x200 != 0 {
        (align16(header.header_size)?, None, 1)
    } else {
        (header.header_size, None, 1)
    };
    for skip in 0..skips {
        let start = checked_add(metadata_end, skip)?;
        let end = checked_add(start, header.info_compressed)?;
        let Some(compressed) = data.get(start..end) else {
            continue;
        };
        let Ok(plain) = decompress(compressed, (header.flags & 0x3f) as u16, header.info_plain)
        else {
            continue;
        };
        if let Ok((blocks, serialized)) = parse_directory(&plain) {
            let data_start = if protected || header.flags & 0x200 != 0 { align16(end)? } else { end };
            return Ok((
                Directory {
                    plain,
                    blocks,
                    info_offset: start,
                    data_start,
                    serialized,
                },
                context,
            ));
        }
    }
    bail!("unable to locate UnityFS directory")
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

/// Block table and the offset of the first serialized-file node.
fn parse_directory(data: &[u8]) -> Result<(Vec<Block>, Option<usize>)> {
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
    let mut serialized = None;
    for _ in 0..nodes {
        if serialized.is_none() && be_u32(data, checked_add(offset, 16)?)? & 4 != 0 {
            serialized = Some(usize::try_from(be_u64(data, offset)?).context("node offset exceeds this platform")?);
        }
        offset = checked_add(offset, 20)?;
        (_, offset) = cstring(data, offset)?;
    }
    if offset != data.len() {
        bail!("invalid UnityFS directory length");
    }
    Ok((blocks, serialized))
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

pub(crate) fn rewrite(
    original: &[u8],
    mut decoded: Decoded,
    content_offset: usize,
    replacement: Vec<u8>,
) -> Result<Vec<u8>> {
    let context = decoded.context.context("only PGR-protected bundles can be rewritten")?;
    let (_, original) = split_prefix(original);
    let end = checked_add(content_offset, replacement.len())?;
    decoded
        .payload
        .get_mut(content_offset..end)
        .context("replacement exceeds payload")?
        .copy_from_slice(&replacement);
    let mut cursor = decoded.directory.data_start;
    let mut logical = 0usize;
    let mut compressed = Vec::with_capacity(decoded.directory.blocks.len());
    for (index, block) in decoded.directory.blocks.iter().enumerate() {
        let old_end = checked_add(cursor, block.compressed)?;
        let old = slice(original, cursor, old_end)?;
        let plain_end = checked_add(logical, block.plain)?;
        let changed = slice(&decoded.payload, logical, plain_end)?;
        let unpacked = if matches!(block.flags & 0x3f, 2 | 3) {
            transform_copy(old, &context, index)?
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
            transform_controls(&mut packed, &context, index, true)?;
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
    // The header's file_size excludes the 4.9 prefix; the prefix goes back in front unchanged.
    let mut file = decoded.prefix;
    file.extend(output);
    let verified = decode(&file)?;
    let end = checked_add(content_offset, replacement.len())?;
    let content = slice(&verified.payload, content_offset, end)?;
    if content != replacement {
        bail!("generated bundle failed content verification");
    }
    Ok(file)
}

fn transform_copy(data: &[u8], context: &[u8; 32], index: usize) -> Result<Vec<u8>> {
    let mut value = data.to_vec();
    transform_controls(&mut value, context, index, false)?;
    Ok(value)
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

/// 4.9 index: the `fixarray(6)` whose first element maps physical file -> `[name, sha1, size, version, bases, 0]`.
/// Only that first element is decoded (the other five are download-group data).
fn file_table(payload: &[u8]) -> Result<Option<Vec<Entry>>> {
    'candidates: for offset in payload
        .iter()
        .enumerate()
        .filter_map(|(i, b)| (*b == 0x96).then_some(i))
    {
        if !matches!(payload.get(offset + 1), Some(0x80..=0x8f | 0xde | 0xdf)) {
            continue;
        }
        let Ok((Value::Map(rows), _)) = msgpack(payload, offset + 1, 1) else {
            continue;
        };
        let mut table = Vec::with_capacity(rows.len());
        for (file, row) in rows {
            let Value::Array(parts) = row else { continue 'candidates };
            let (Some(Value::String(sha1)), Some(Value::Int(size))) = (parts.get(1), parts.get(2)) else {
                continue 'candidates;
            };
            table.push(Entry { file, sha1: sha1.clone(), size: *size });
        }
        if !table.is_empty() {
            return Ok(Some(table));
        }
    }
    Ok(None)
}

/// `m_Name` of a PGR-protected bundle's AssetBundle object, reading only the LZ4 blocks that cover it.
/// `Ok(None)`: not protected, or not a layout this reader knows (serialized version < 22, big endian).
fn bundle_name(path: &Path) -> Result<Option<String>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() || metadata.len() > MAX_BUNDLE_SIZE {
        bail!("refusing asset: {}", path.display());
    }
    let len = usize::try_from(metadata.len())?;
    let mut file = fs::File::open(path)?;
    // Most of the 80k bundles are unprotected: decide from the first 512 bytes before reading more.
    let mut head = vec![0u8; len.min(512)];
    file.read_exact(&mut head)?;
    let (prefix, body) = split_prefix(&head);
    let total = len - prefix.len();
    let header = header(body, total)?;
    if header.flags & 0x1000 == 0 {
        return Ok(None);
    }
    head.resize(len.min(SCAN_HEAD), 0);
    file.seek(SeekFrom::Start(0))?;
    file.read_exact(&mut head)?;
    let (prefix, body) = split_prefix(&head);
    let (directory, context) = directory(body, &header, false)?;
    let base = directory.serialized.context("bundle has no serialized file")?;
    let mut lazy = Lazy {
        file,
        prefix: prefix.len(),
        total,
        blocks: directory.blocks,
        context,
        cursor: directory.data_start,
        next: 0,
        payload: Vec::new(),
    };
    let fixed = lazy.fill(checked_add(base, 48)?)?[base..].to_vec();
    if be_u32(&fixed, 8)? < 22 || fixed[16] != 0 {
        return Ok(None);
    }
    let data_offset = usize::try_from(be_u64(&fixed, 32)?).context("data offset exceeds this platform")?;
    let metadata = lazy.fill(checked_add(base, data_offset)?)?[base..].to_vec();
    let Some(byte_start) = asset_bundle_start(&metadata)? else {
        return Ok(None);
    };
    let at = checked_add(checked_add(base, data_offset)?, byte_start)?;
    let name_len = le_u32(lazy.fill(checked_add(at, 4)?)?, at)? as usize;
    if name_len > 4096 {
        return Ok(None);
    }
    let name = slice(lazy.fill(checked_add(at + 4, name_len)?)?, at + 4, at + 4 + name_len)?;
    Ok(Some(String::from_utf8(name.to_vec())?))
}

/// Serialized file v22 metadata (little endian after the 48-byte header): data-relative start of the
/// AssetBundle object (class 142), whose first field is `m_Name`.
fn asset_bundle_start(meta: &[u8]) -> Result<Option<usize>> {
    let (_, mut at) = cstring(meta, 48)?;
    at = checked_add(at, 4)?; // target platform
    let tree = *meta.get(at).context("truncated serialized metadata")? != 0;
    at += 1;
    let types = le_u32(meta, at)? as usize;
    at += 4;
    if types > MAX_TYPES {
        bail!("too many serialized types");
    }
    let mut classes = Vec::with_capacity(types);
    for _ in 0..types {
        let class = le_u32(meta, at)?;
        at = checked_add(at, 7)?; // class id, stripped flag, script type index
        if class == 114 {
            at = checked_add(at, 16)?; // script id
        }
        at = checked_add(at, 16)?; // old type hash
        if tree {
            let nodes = le_u32(meta, at)? as usize;
            let strings = le_u32(meta, at + 4)? as usize;
            at = checked_add(at, checked_add(8, checked_add(nodes.checked_mul(32).context("type tree overflow")?, strings)?)?)?;
            let dependencies = le_u32(meta, at)? as usize;
            at = checked_add(at, checked_add(4, dependencies.checked_mul(4).context("type dependency overflow")?)?)?;
        }
        classes.push(class);
    }
    let objects = le_u32(meta, at)? as usize;
    at += 4;
    if objects > MAX_ENTRIES {
        bail!("too many serialized objects");
    }
    for _ in 0..objects {
        at = align4(at)?;
        let byte_start = le_u64(meta, checked_add(at, 8)?)?;
        let type_id = le_u32(meta, checked_add(at, 20)?)? as usize;
        at = checked_add(at, 24)?;
        if classes.get(type_id) == Some(&ASSET_BUNDLE_CLASS) {
            return Ok(Some(usize::try_from(byte_start)?));
        }
    }
    Ok(None)
}

/// Block-at-a-time reader of a bundle's payload.
struct Lazy {
    file: fs::File,
    prefix: usize,
    total: usize,
    blocks: Vec<Block>,
    context: Option<[u8; 32]>,
    cursor: usize,
    next: usize,
    payload: Vec<u8>,
}
impl Lazy {
    fn fill(&mut self, n: usize) -> Result<&[u8]> {
        while self.payload.len() < n {
            let block = self.blocks.get(self.next).context("serialized data ends early")?.clone();
            let end = checked_add(self.cursor, block.compressed)?;
            if end > self.total || block.plain > MAX_BUNDLE_SIZE as usize {
                bail!("truncated asset");
            }
            self.file.seek(SeekFrom::Start((self.prefix + self.cursor) as u64))?;
            let mut data = vec![0u8; block.compressed];
            self.file.read_exact(&mut data)?;
            if let (Some(context), 2 | 3) = (&self.context, block.flags & 0x3f) {
                transform_controls(&mut data, context, self.next, false)?;
            }
            self.payload.extend(decompress(&data, block.flags & 0x3f, block.plain)?);
            self.cursor = end;
            self.next += 1;
        }
        Ok(&self.payload[..n])
    }
}

enum Value {
    String(String),
    Int(u64),
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
        0xdb => {
            let n = be_u32(data, offset)? as usize;
            return string_value(data, offset + 4, n);
        }
        0xdc => (1, be_u16(data, offset)? as usize),
        0xdd => (1, be_u32(data, offset)? as usize),
        0xde => (0, be_u16(data, offset)? as usize),
        0xdf => (0, be_u32(data, offset)? as usize),
        0x00..=0x7f => return Ok((Value::Int(u64::from(marker)), offset)),
        0xcc => {
            let n = *data.get(offset).context("truncated MessagePack integer")?;
            return Ok((Value::Int(u64::from(n)), offset + 1));
        }
        0xcd => return Ok((Value::Int(u64::from(be_u16(data, offset)?)), offset + 2)),
        0xce => return Ok((Value::Int(u64::from(be_u32(data, offset)?)), offset + 4)),
        0xcf => return Ok((Value::Int(be_u64(data, offset)?), offset + 8)),
        0xe0..=0xff | 0xc0 | 0xc2 | 0xc3 => return Ok((Value::Other, offset)),
        0xd0 => return Ok((Value::Other, checked_add(offset, 1)?)),
        0xd1 => return Ok((Value::Other, checked_add(offset, 2)?)),
        0xd2 => return Ok((Value::Other, checked_add(offset, 4)?)),
        0xd3 => return Ok((Value::Other, checked_add(offset, 8)?)),
        _ => bail!("unsupported MessagePack marker 0x{marker:02x}"),
    };
    offset += match marker {
        0xdc | 0xde => 2,
        0xdd | 0xdf => 4,
        _ => 0,
    };
    if count > MAX_ENTRIES {
        bail!("MessagePack collection is too large");
    }
    // The count is untrusted: grow with the data instead of reserving it.
    if kind == 1 {
        let mut values = Vec::with_capacity(count.min(1024));
        for _ in 0..count {
            let (value, next) = msgpack(data, offset, depth + 1)?;
            values.push(value);
            offset = next;
        }
        Ok((Value::Array(values), offset))
    } else {
        let mut values = Vec::with_capacity(count.min(1024));
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
        slice(data, at, checked_add(at, 2)?)?.try_into().context("invalid integer width")?,
    ))
}
fn be_u32(data: &[u8], at: usize) -> Result<u32> {
    Ok(u32::from_be_bytes(
        slice(data, at, checked_add(at, 4)?)?.try_into().context("invalid integer width")?,
    ))
}
fn be_u64(data: &[u8], at: usize) -> Result<u64> {
    Ok(u64::from_be_bytes(
        slice(data, at, checked_add(at, 8)?)?.try_into().context("invalid integer width")?,
    ))
}
fn le_u32(data: &[u8], at: usize) -> Result<u32> {
    Ok(u32::from_le_bytes(
        slice(data, at, checked_add(at, 4)?)?.try_into().context("invalid integer width")?,
    ))
}
fn le_u64(data: &[u8], at: usize) -> Result<u64> {
    Ok(u64::from_le_bytes(
        slice(data, at, checked_add(at, 8)?)?.try_into().context("invalid integer width")?,
    ))
}

pub(crate) fn text_asset<'a>(payload: &'a [u8], name: &[u8]) -> Result<(usize, &'a [u8])> {
    let mut search = 0usize;
    while let Some(relative) = payload
        .get(search..)
        .and_then(|p| p.windows(name.len()).position(|w| w == name))
    {
        let at = checked_add(search, relative)?;
        if at >= 4 && le_u32(payload, at - 4)? as usize == name.len() {
            let size_at = align4(checked_add(at, name.len())?)?;
            let size = le_u32(payload, size_at)? as usize;
            let content = checked_add(size_at, 4)?;
            let end = checked_add(content, size)?;
            if let Some(value) = payload.get(content..end) {
                return Ok((content, value));
            }
        }
        search = checked_add(at, 1)?;
    }
    bail!("TextAsset not found")
}

/// Decode the active document bundle, falling back to packaged resources.
pub(crate) fn load_matrix_payload(game: &Path) -> Result<Vec<u8>> {
    let active = resolve(game)?;
    Ok(decode(&read_bounded(&active.path)?)?.payload)
}

/// TextAssets store basenames only; never guess which duplicate a module meant.
pub(crate) fn extract_text_asset<'a>(payload: &'a [u8], name: &str) -> Result<&'a [u8]> {
    if name.is_empty() || name.contains(['/', '\\', '\0']) {
        bail!("invalid TextAsset name");
    }
    let name = name.as_bytes();
    let mut found = None;
    for at in payload.windows(name.len()).enumerate().filter_map(|(at, value)| (value == name).then_some(at)) {
        if at < 4 || le_u32(payload, at - 4)? as usize != name.len() {
            continue;
        }
        let size_at = align4(checked_add(at, name.len())?)?;
        let size = le_u32(payload, size_at)? as usize;
        let start = checked_add(size_at, 4)?;
        let end = checked_add(start, size)?;
        let content = slice(payload, start, end)?;
        if found.replace(content).is_some() {
            bail!("ambiguous TextAsset: {}", String::from_utf8_lossy(name));
        }
    }
    found.with_context(|| format!("TextAsset not found: {}", String::from_utf8_lossy(name)))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn append_asset(payload: &mut Vec<u8>, name: &[u8], content: &[u8]) {
        payload.extend_from_slice(&(name.len() as u32).to_le_bytes());
        payload.extend_from_slice(name);
        while payload.len() % 4 != 0 { payload.push(0); }
        payload.extend_from_slice(&(content.len() as u32).to_le_bytes());
        payload.extend_from_slice(content);
    }

    #[test]
    fn exact_unique_text_assets_only() -> Result<()> {
        let mut payload = Vec::new();
        append_asset(&mut payload, b"OtherXUiMain.lua", b"wrong");
        append_asset(&mut payload, b"XUiMain.lua", b"return 1");
        assert_eq!(extract_text_asset(&payload, "XUiMain.lua")?, b"return 1");
        assert!(extract_text_asset(&payload, "Missing.lua").is_err());
        assert!(extract_text_asset(&payload, "").is_err());
        append_asset(&mut payload, b"XUiMain.lua", b"return 2");
        assert!(extract_text_asset(&payload, "XUiMain.lua").is_err());
        Ok(())
    }

    #[test]
    fn truncated_assets_and_controls_are_rejected() {
        let mut payload = Vec::new();
        append_asset(&mut payload, b"X.lua", b"content");
        payload.pop();
        assert!(extract_text_asset(&payload, "X.lua").is_err());
        assert!(decode(b"UnityFS\0").is_err());
        assert!(transform_controls(&mut [0xf0], &[0; 32], 0, true).is_err());
    }

    #[test]
    fn protected_lz4_round_trips() -> Result<()> {
        let mut context = [0; 32];
        for (index, value) in context[..16].iter_mut().enumerate() { *value = index as u8; }
        context[16..].fill(7);
        let input = b"a repeated string a repeated string a repeated string";
        let mut compressed = lz4_flex::block::compress(input);
        let original = compressed.clone();
        transform_controls(&mut compressed, &context, 3, true)?;
        assert_ne!(compressed, original);
        transform_controls(&mut compressed, &context, 3, false)?;
        assert_eq!(compressed, original);
        assert_eq!(decompress(&compressed, 2, input.len())?, input);
        Ok(())
    }

    struct Tmp(PathBuf);
    impl Tmp {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!("ascnet-bundle-{}", uuid::Uuid::new_v4()));
            fs::create_dir_all(&path).unwrap();
            Tmp(path)
        }
        fn write(&self, relative: &str, data: &[u8]) -> PathBuf {
            let path = self.0.join(relative);
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(&path, data).unwrap();
            path
        }
    }
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn ident_context() -> [u8; 32] {
        let mut context = [7u8; 32];
        for (index, value) in context[..16].iter_mut().enumerate() { *value = index as u8; }
        context
    }

    /// A 32-byte seed (no NUL) whose AES-CTR decryption yields `ident_context()`.
    fn seed() -> [u8; 32] {
        let plain: [u8; 16] = [0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77];
        for k in 1..=255u8 {
            let iv = [k; 16];
            let mut sealed = plain;
            Aes128Ctr::new_from_slices(KEY, &iv).unwrap().apply_keystream(&mut sealed);
            if sealed.iter().all(|b| *b != 0) {
                let mut out = [0u8; 32];
                out[..16].copy_from_slice(&sealed);
                out[16..].copy_from_slice(&iv);
                return out;
            }
        }
        unreachable!()
    }

    /// Synthetic UnityFS bundle: flags 0x1243 (protected) or 0x243, LZ4HC blocks of `chunk` payload bytes,
    /// one serialized-file node, optional 4.9 `HARUINKUROGAME` prefix (not counted in `file_size`).
    fn bundle(payload: &[u8], protected: bool, prefixed: bool, chunk: usize) -> Vec<u8> {
        let context = ident_context();
        let mut blocks = Vec::new();
        for (index, part) in payload.chunks(chunk).enumerate() {
            let mut packed = lz4_flex::block::compress(part);
            if protected {
                transform_controls(&mut packed, &context, index, true).unwrap();
            }
            blocks.push((part.len(), packed));
        }
        let mut directory = vec![0u8; 16];
        directory.extend((blocks.len() as u32).to_be_bytes());
        for (plain, packed) in &blocks {
            directory.extend((*plain as u32).to_be_bytes());
            directory.extend((packed.len() as u32).to_be_bytes());
            directory.extend(3u16.to_be_bytes());
        }
        directory.extend(1u32.to_be_bytes());
        directory.extend(0u64.to_be_bytes());
        directory.extend((payload.len() as u64).to_be_bytes());
        directory.extend(4u32.to_be_bytes());
        directory.extend(b"CAB-test\0");
        let info = lz4_flex::block::compress(&directory);
        let flags: u32 = if protected { 0x1243 } else { 0x243 };
        let mut body = b"UnityFS\0".to_vec();
        body.extend(6u32.to_be_bytes());
        body.extend(b"5.x.x\0");
        body.extend(b"2022.3.52f1c1\0");
        let fields = body.len();
        body.extend([0u8; 8]);
        body.extend((info.len() as u32).to_be_bytes());
        body.extend((directory.len() as u32).to_be_bytes());
        body.extend(flags.to_be_bytes());
        if protected {
            body.extend([0u8; 4]);
            body.extend(seed());
            body.extend([0u8, 0u8]); // seed terminator, empty second string
        } else {
            body.resize(align16(body.len()).unwrap(), 0);
        }
        body.extend(&info);
        body.resize(align16(body.len()).unwrap(), 0);
        for (_, packed) in &blocks {
            body.extend(packed);
        }
        let size = body.len() as u64;
        body[fields..fields + 8].copy_from_slice(&size.to_be_bytes());
        if prefixed {
            let mut file = PREFIX_MAGIC.to_vec();
            file.resize(PREFIX_LEN, 0);
            file.extend(body);
            file
        } else {
            body
        }
    }

    /// Serialized file v22 with a GameObject and an AssetBundle object named `name` (little-endian metadata).
    fn serialized(name: &str, tree: bool) -> Vec<u8> {
        let mut m = vec![0u8; 48];
        m[8..12].copy_from_slice(&22u32.to_be_bytes());
        m.extend(b"2022.3.52f1c1\0");
        m.extend(19i32.to_le_bytes());
        m.push(tree as u8);
        m.extend(2u32.to_le_bytes());
        for class in [1u32, 142] {
            m.extend(class.to_le_bytes());
            m.push(0);
            m.extend((-1i16).to_le_bytes());
            m.extend([0xaa; 16]);
            if tree {
                m.extend(1u32.to_le_bytes());
                m.extend(4u32.to_le_bytes());
                m.extend([0u8; 32]);
                m.extend(*b"abcd");
                m.extend(0u32.to_le_bytes());
            }
        }
        m.extend(2u32.to_le_bytes());
        for (path_id, start, type_id) in [(1i64, 0u64, 0u32), (2, 8, 1)] {
            while m.len() % 4 != 0 { m.push(0); }
            m.extend(path_id.to_le_bytes());
            m.extend(start.to_le_bytes());
            m.extend(4u32.to_le_bytes());
            m.extend(type_id.to_le_bytes());
        }
        let data_offset = align16(m.len()).unwrap();
        m.resize(data_offset, 0);
        m.extend(*b"abcd\0\0\0\0");
        m.extend((name.len() as u32).to_le_bytes());
        m.extend(name.as_bytes());
        m.resize(align4(m.len()).unwrap() + 4, 0);
        m[32..40].copy_from_slice(&(data_offset as u64).to_be_bytes());
        m
    }

    fn str8(s: &str) -> Vec<u8> {
        let mut v = vec![0xd9, s.len() as u8];
        v.extend(s.as_bytes());
        v
    }

    /// 4.9 index payload: `fixarray(6)`, first element a map32 of `file -> [name, sha1, size, ver, bases, 0]`.
    fn pack_table(rows: &[(String, String, u64)]) -> Vec<u8> {
        let mut v = vec![0x96, 0xdf];
        v.extend((rows.len() as u32).to_be_bytes());
        for (file, sha1, size) in rows {
            v.extend(str8(file));
            v.extend([0x96, 0xa0]);
            v.extend(str8(sha1));
            v.push(0xce);
            v.extend((*size as u32).to_be_bytes());
            v.push(0xce);
            v.extend(1_739_029u32.to_be_bytes());
            v.extend([0x90, 0x00]);
        }
        v.extend([0xc0; 5]);
        v
    }

    fn row(file: &str, data: &[u8]) -> (String, String, u64) {
        (file.into(), format!("{:x}", Sha1::digest(data)), data.len() as u64)
    }

    #[test]
    fn prefixed_bundles_decode_and_rewrite_with_the_prefix_kept() -> Result<()> {
        let payload = serialized(LOGICAL_ASSET, false);
        for prefixed in [false, true] {
            let file = bundle(&payload, true, prefixed, 64);
            assert_eq!(file.starts_with(PREFIX_MAGIC), prefixed);
            let decoded = decode(&file)?;
            assert_eq!(decoded.payload, payload);
            assert_eq!(decoded.prefix.len(), if prefixed { PREFIX_LEN } else { 0 });
            let at = payload.len() - 4;
            let output = rewrite(&file, decode(&file)?, at, b"WXYZ".to_vec())?;
            assert_eq!(output.starts_with(PREFIX_MAGIC), prefixed);
            if prefixed {
                assert_eq!(output[..PREFIX_LEN], file[..PREFIX_LEN]);
            }
            let again = decode(&output)?;
            assert_eq!(&again.payload[at..], b"WXYZ");
            assert_eq!(again.payload[..at], payload[..at]);
        }
        // The header size must not count the prefix: a size that does is rejected.
        let mut file = bundle(&payload, true, true, 64);
        let size_at = PREFIX_LEN + 32;
        let size = u64::from_be_bytes(file[size_at..size_at + 8].try_into()?) + PREFIX_LEN as u64;
        file[size_at..size_at + 8].copy_from_slice(&size.to_be_bytes());
        assert!(decode(&file).is_err());
        assert!(decode(&PREFIX_MAGIC.to_vec()).is_err());
        Ok(())
    }

    #[test]
    fn unprotected_bundles_are_accepted_only_as_indexes() -> Result<()> {
        let payload = b"index payload index payload index payload".repeat(10);
        for prefixed in [false, true] {
            let plain = bundle(&payload, false, prefixed, 100);
            assert!(decode(&plain).err().unwrap().to_string().contains("not PGR-protected"));
            assert_eq!(decode_index(&plain)?.payload, payload);
            assert_eq!(decode_index(&bundle(&payload, true, prefixed, 100))?.payload, payload);
        }
        Ok(())
    }

    #[test]
    fn bundle_name_comes_from_the_asset_bundle_object() -> Result<()> {
        let tmp = Tmp::new();
        for (tree, prefixed) in [(false, false), (true, false), (false, true), (true, true)] {
            let file = tmp.write("a.uab", &bundle(&serialized("assets/x/y.prefab.ab", tree), true, prefixed, 64));
            assert_eq!(bundle_name(&file)?.as_deref(), Some("assets/x/y.prefab.ab"));
        }
        // Unprotected bundles are never the matrix bundle; garbage is an error, not a name.
        let plain = tmp.write("p.uab", &bundle(&serialized(LOGICAL_ASSET, false), false, true, 64));
        assert_eq!(bundle_name(&plain)?, None);
        assert!(bundle_name(&tmp.write("raw.bytes", b"not a bundle")).is_err());
        Ok(())
    }

    #[test]
    fn file_table_reads_map32_rows_beyond_the_old_65536_limit() -> Result<()> {
        let rows: Vec<_> = (0..70_000).map(|i| (format!("{i:016x}.uab"), "ab".repeat(20), 100_000 + i as u64)).collect();
        let mut payload = vec![0x96, 0x01, 0x96, 0x80, 0x00]; // decoys: not a 6-array with a table first
        payload.extend(pack_table(&rows));
        let table = file_table(&payload)?.unwrap();
        assert_eq!(table.len(), 70_000);
        assert_eq!((table[69_999].file.as_str(), table[69_999].size), ("000000000001116f.uab", 169_999));
        assert_eq!(table[0].sha1.len(), 40);
        assert!(file_table(&[0x96, 0xc0, 0xc0])?.is_none());
        Ok(())
    }

    fn game() -> Tmp {
        let tmp = Tmp::new();
        tmp.write("PGR.exe", b"MZ");
        tmp
    }
    fn matrix(name: &str, prefixed: bool, salt: u8) -> Vec<u8> {
        let mut payload = serialized(name, false);
        payload.push(salt);
        bundle(&payload, true, prefixed, 64)
    }
    const DOC: &str = "PGR_Data/StreamingAssets/document/matrix";
    const RES: &str = "PGR_Data/StreamingAssets/resource/matrix";

    #[test]
    fn resolve_49_finds_matrix_by_m_name_document_first() -> Result<()> {
        let tmp = game();
        let patched = matrix(LOGICAL_ASSET, true, 1);
        tmp.write(&format!("{DOC}/aaaa.uab"), &patched);
        tmp.write(&format!("{DOC}/bbbb.uab"), &matrix("assets/other.ab", true, 2));
        tmp.write(&format!("{DOC}/cccc.uab"), &bundle(&serialized(LOGICAL_ASSET, false), false, true, 64));
        let stale = matrix(LOGICAL_ASSET, true, 3);
        tmp.write(&format!("{RES}/aaaa.uab"), &stale);
        tmp.write(&format!("{RES}/dddd.uab"), &matrix(LOGICAL_ASSET, true, 4));
        let document = pack_table(&[row("aaaa.uab", &stale), row("bbbb.uab", b"b"), row("cccc.uab", b"c")]);
        tmp.write(&format!("{DOC}/index"), &bundle(&document, false, false, 4096));
        let package = pack_table(&[row("aaaa.uab", &stale), row("dddd.uab", b"d")]);
        tmp.write(&format!("{RES}/InPkgIndex"), &bundle(&package, false, false, 4096));
        let active = resolve(&tmp.0)?;
        assert_eq!(active.scope, "document");
        assert_eq!(fs::read(&active.path)?, patched); // the launcher's own rewrite never matches the index entry; still used
        assert_eq!(active.path.file_name().unwrap(), "aaaa.uab");
        Ok(())
    }

    #[test]
    fn resolve_49_resource_only_skips_files_the_document_snapshot_omits() -> Result<()> {
        let tmp = game();
        let shipped = matrix(LOGICAL_ASSET, true, 1);
        tmp.write(&format!("{RES}/rrrr.uab"), &shipped);
        tmp.write(&format!("{RES}/dddd.uab"), &matrix(LOGICAL_ASSET, true, 4)); // obsolete: not in the snapshot
        let document = pack_table(&[row("rrrr.uab", &shipped)]);
        tmp.write(&format!("{DOC}/index"), &bundle(&document, false, false, 4096));
        let package = pack_table(&[row("rrrr.uab", &shipped), row("dddd.uab", b"d")]);
        tmp.write(&format!("{RES}/InPkgIndex"), &bundle(&package, false, false, 4096));
        let active = resolve(&tmp.0)?;
        assert_eq!(active.scope, "resource");
        assert_eq!(active.path.file_name().unwrap(), "rrrr.uab");
        Ok(())
    }

    #[test]
    fn resolve_49_duplicate_claims_need_exactly_one_intact_file() -> Result<()> {
        let intact = matrix(LOGICAL_ASSET, true, 1);
        let modified = matrix(LOGICAL_ASSET, true, 2);
        let build = |index: Vec<(String, String, u64)>| {
            let tmp = game();
            tmp.write(&format!("{RES}/one.uab"), &intact);
            tmp.write(&format!("{RES}/two.uab"), &modified);
            tmp.write(&format!("{RES}/InPkgIndex"), &bundle(&pack_table(&index), false, false, 4096));
            tmp
        };
        let tmp = build(vec![row("one.uab", &intact), row("two.uab", b"not the file")]);
        assert_eq!(resolve(&tmp.0)?.path.file_name().unwrap(), "one.uab");
        let tmp = build(vec![row("one.uab", b"x"), row("two.uab", b"y")]);
        assert!(resolve(&tmp.0).err().unwrap().to_string().contains("ambiguous"));
        Ok(())
    }

    #[test]
    fn resolve_48_index_maps_the_logical_asset_to_its_file() -> Result<()> {
        let tmp = game();
        let mut index = vec![0x93, 0x81];
        index.extend(str8(LOGICAL_ASSET));
        index.push(0x91);
        index.extend(str8("m.ab"));
        index.extend([0xc0, 0xc0]);
        tmp.write(&format!("{DOC}/index"), &bundle(&index, true, false, 4096)); // 4.8 index: protected, unprefixed
        tmp.write(&format!("{DOC}/m.ab"), &matrix(LOGICAL_ASSET, false, 0));
        let active = resolve(&tmp.0)?;
        assert_eq!((active.scope, active.path.file_name().unwrap()), ("document", std::ffi::OsStr::new("m.ab")));
        Ok(())
    }

    fn real_client(var: &str, default: &str) -> PathBuf {
        PathBuf::from(std::env::var(var).unwrap_or_else(|_| default.into()))
    }

    /// Read-only run against the 4.9.1 Steam install (`ASCNET_CLIENT`); `cargo test -- --ignored real_49`.
    #[test]
    #[ignore]
    fn real_49_install_resolves_decodes_and_rewrites_the_matrix_bundle() -> Result<()> {
        let game = real_client("ASCNET_CLIENT", "/Volumes/Lucia/Steam Games/SteamLibrary/steamapps/common/Punishing Gray Raven");
        let streaming = game.join(STREAMING);
        let table = |scope: &str, index: &str| -> Result<Vec<Entry>> {
            let payload = decode_index(&read_bounded(&streaming.join(scope).join("matrix").join(index))?)?.payload;
            Ok(file_table(&payload)?.expect("4.9 file table"))
        };
        // A fresh install has no document/matrix yet (resource only); a patched one has the full-snapshot index.
        let document = table("document", "index").unwrap_or_default();
        let package = table("resource", "InPkgIndex")?;
        eprintln!("document index {} entries, resource InPkgIndex {} entries", document.len(), package.len());
        let started = std::time::Instant::now();
        let active = resolve(&game)?;
        eprintln!("resolved {} ({}) in {:?}", active.path.display(), active.scope, started.elapsed());
        let bytes = read_bounded(&active.path)?;
        assert!(bytes.starts_with(PREFIX_MAGIC));
        let name = active.path.file_name().unwrap().to_string_lossy().into_owned();
        let entry = document.iter().chain(&package).find(|e| e.file == name).expect("index entry");
        eprintln!("index sha1/size match: {}", matches_entry(&active.path, entry)?);
        assert_eq!(bundle_name(&active.path)?.as_deref(), Some(LOGICAL_ASSET));
        let decoded = decode(&bytes)?;
        let (offset, content) = text_asset(&decoded.payload, b"XUiMain.lua")?;
        let rewritten = rewrite(&bytes, decode(&bytes)?, offset, content.to_vec())?;
        assert!(rewritten.starts_with(&bytes[..PREFIX_LEN]));
        assert_eq!(decode(&rewritten)?.payload, decoded.payload);
        assert!(load_matrix_payload(&game)?.len() == decoded.payload.len());
        Ok(())
    }

    /// Read-only run against the 4.8.0 copy (`ASCNET_CLIENT_48`); `cargo test -- --ignored real_48`.
    #[test]
    #[ignore]
    fn real_48_install_still_resolves_through_the_logical_index() -> Result<()> {
        let game = real_client("ASCNET_CLIENT_48", "/Volumes/Miyu/Punishing Gray Raven 4.8");
        let active = resolve(&game)?;
        eprintln!("4.8 resolved {} ({})", active.path.display(), active.scope);
        let bytes = read_bounded(&active.path)?;
        assert!(!bytes.starts_with(PREFIX_MAGIC));
        let payload = decode(&bytes)?.payload;
        text_asset(&payload, b"XUiMain.lua")?;
        Ok(())
    }
}
