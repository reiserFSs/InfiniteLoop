//! PE-derived Unity startup redirect. Binary identity policy belongs to the caller.
use anyhow::{anyhow, bail, ensure, Result};

const STUB_LEN: usize = 124;
const EXECUTE: u32 = 0x2000_0000;

fn bytes(data: &[u8], offset: usize, len: usize) -> Result<&[u8]> {
    let end = offset.checked_add(len).ok_or_else(|| anyhow!("PE offset overflow"))?;
    data.get(offset..end).ok_or_else(|| anyhow!("Truncated PE data"))
}

fn u16_at(data: &[u8], offset: usize) -> Result<u16> {
    Ok(u16::from_le_bytes(bytes(data, offset, 2)?.try_into()?))
}

fn u32_at(data: &[u8], offset: usize) -> Result<u32> {
    Ok(u32::from_le_bytes(bytes(data, offset, 4)?.try_into()?))
}

fn u64_at(data: &[u8], offset: usize) -> Result<u64> {
    Ok(u64::from_le_bytes(bytes(data, offset, 8)?.try_into()?))
}

struct Section {
    rva: u32,
    size: u32,
    raw: usize,
    raw_size: usize,
    executable: bool,
}

struct Pe<'a> {
    data: &'a [u8],
    image_base: u64,
    directories: &'a [u8],
    sections: Vec<Section>,
}

impl<'a> Pe<'a> {
    fn parse(data: &'a [u8]) -> Result<Self> {
        ensure!(bytes(data, 0, 2)? == b"MZ", "Not an MZ image");
        let pe = u32_at(data, 0x3c)? as usize;
        let header = bytes(data, pe, 24)?;
        ensure!(&header[..4] == b"PE\0\0", "Not a PE image");
        ensure!(u16_at(header, 4)? == 0x8664, "Expected AMD64 PE image");
        let count = u16_at(header, 6)? as usize;
        ensure!(count > 0, "PE has no sections");
        let optional_size = u16_at(header, 20)? as usize;
        let optional = bytes(data, pe + 24, optional_size)?;
        ensure!(u16_at(optional, 0)? == 0x20b, "Expected PE32+ image");
        let image_base = u64_at(optional, 24)?;
        let image_size = u32_at(optional, 56)?;
        ensure!(image_base.checked_add(u64::from(image_size)).is_some(), "Image address overflow");
        let headers_size = u32_at(optional, 60)? as usize;
        bytes(data, 0, headers_size)?;
        let directory_count = u32_at(optional, 108)? as usize;
        let directory_size = directory_count.checked_mul(8).ok_or_else(|| anyhow!("Directory size overflow"))?;
        let directories = bytes(optional, 112, directory_size)?;
        let table_start = pe + 24 + optional_size;
        let table = bytes(data, table_start, count * 40)?;
        ensure!(table_start + table.len() <= headers_size, "Sections extend beyond PE headers");
        let mut sections: Vec<Section> = Vec::with_capacity(count);
        for row in table.chunks_exact(40) {
            let raw_size = u32_at(row, 16)? as usize;
            let section = Section {
                rva: u32_at(row, 12)?,
                size: u32_at(row, 8)?.max(raw_size as u32),
                raw: u32_at(row, 20)? as usize,
                raw_size,
                executable: u32_at(row, 36)? & EXECUTE != 0,
            };
            ensure!(u64::from(section.rva) + u64::from(section.size) <= u64::from(image_size), "Section exceeds image");
            ensure!(section.size == 0 || u64::from(section.rva) >= headers_size as u64, "Section overlaps headers");
            if raw_size > 0 {
                ensure!(section.raw >= headers_size, "Raw section overlaps headers");
                bytes(data, section.raw, raw_size)?;
            }
            for other in &sections {
                ensure!(!overlap(u64::from(section.rva), u64::from(section.size), u64::from(other.rva), u64::from(other.size)), "Overlapping virtual sections");
                ensure!(!overlap(section.raw as u64, section.raw_size as u64, other.raw as u64, other.raw_size as u64), "Overlapping raw sections");
            }
            sections.push(section);
        }
        Ok(Self { data, image_base, directories, sections })
    }

    fn directory(&self, index: usize) -> Result<(u32, usize)> {
        let row = bytes(self.directories, index * 8, 8)?;
        let rva = u32_at(row, 0)?;
        let size = u32_at(row, 4)? as usize;
        ensure!(rva != 0 && size != 0, "Missing PE directory {index}");
        self.at(rva, size)?;
        Ok((rva, size))
    }

    fn section(&self, rva: u32, len: usize) -> Result<&Section> {
        self.sections.iter().find(|s| {
            rva >= s.rva && u64::from(rva - s.rva) + len as u64 <= s.raw_size as u64
        }).ok_or_else(|| anyhow!("Unbacked PE RVA {rva:#x} ({len} bytes)"))
    }

    fn offset(&self, rva: u32, len: usize) -> Result<usize> {
        let s = self.section(rva, len)?;
        Ok(s.raw + (rva - s.rva) as usize)
    }

    fn at(&self, rva: u32, len: usize) -> Result<&'a [u8]> {
        bytes(self.data, self.offset(rva, len)?, len)
    }

    fn executable(&self, rva: u32, len: usize) -> Result<usize> {
        ensure!(self.section(rva, len)?.executable, "RVA {rva:#x} is not executable");
        self.offset(rva, len)
    }

    fn string(&self, rva: u32) -> Result<&'a [u8]> {
        let s = self.section(rva, 1)?;
        let delta = (rva - s.rva) as usize;
        let rest = bytes(self.data, s.raw + delta, s.raw_size - delta)?;
        let end = rest.iter().position(|&b| b == 0).ok_or_else(|| anyhow!("Unterminated PE string"))?;
        Ok(&rest[..end])
    }

    fn export(&self, name: Option<&[u8]>) -> Result<u32> {
        let (rva, size) = self.directory(0)?;
        ensure!(size >= 40, "Truncated export directory");
        let header = self.at(rva, 40)?;
        let count = u32_at(header, 20)? as usize;
        let functions = self.at(u32_at(header, 28)?, count.checked_mul(4).ok_or_else(|| anyhow!("Export count overflow"))?)?;
        let index = if let Some(wanted) = name {
            let names_count = u32_at(header, 24)? as usize;
            let names = self.at(u32_at(header, 32)?, names_count.checked_mul(4).ok_or_else(|| anyhow!("Export name count overflow"))?)?;
            let ordinals = self.at(u32_at(header, 36)?, names_count.checked_mul(2).ok_or_else(|| anyhow!("Export ordinal count overflow"))?)?;
            let mut found = None;
            for i in 0..names_count {
                let ordinal = u16_at(ordinals, i * 2)? as usize;
                ensure!(ordinal < count, "Export ordinal out of bounds");
                if self.string(u32_at(names, i * 4)?)? == wanted {
                    ensure!(found.is_none(), "Duplicate named export");
                    found = Some(ordinal);
                }
            }
            found.ok_or_else(|| anyhow!("UnityMain export not found"))?
        } else {
            ensure!(count == 1, "Expected exactly one PGRBase export");
            0
        };
        let target = u32_at(functions, index * 4)?;
        ensure!(target != 0, "Null export");
        ensure!(!(u64::from(rva)..u64::from(rva) + size as u64).contains(&u64::from(target)), "Forwarded export is unsupported");
        self.executable(target, 1)?;
        Ok(target)
    }

    fn load_library_iat(&self) -> Result<u32> {
        let (rva, size) = self.directory(1)?;
        let descriptors = self.at(rva, size)?;
        let mut found = None;
        let mut terminated = false;
        for row in descriptors.chunks_exact(20) {
            if row.iter().all(|&b| b == 0) {
                terminated = true;
                break;
            }
            let name = self.string(u32_at(row, 12)?)?;
            if !name.eq_ignore_ascii_case(b"kernel32.dll") {
                continue;
            }
            let first = u32_at(row, 16)?;
            ensure!(first != 0, "Missing import address table");
            let original = u32_at(row, 0)?;
            let lookup = if original == 0 { first } else { original };
            let section = self.section(lookup, 8)?;
            let slots = (section.raw_size - (lookup - section.rva) as usize) / 8;
            let table = self.at(lookup, slots * 8)?;
            let mut thunk_terminated = false;
            for (i, slot) in table.chunks_exact(8).enumerate() {
                let thunk = u64_at(slot, 0)?;
                if thunk == 0 {
                    thunk_terminated = true;
                    break;
                }
                let iat = u64::from(first) + (i as u64) * 8;
                let iat = u32::try_from(iat)?;
                self.at(iat, 8)?;
                if thunk & (1 << 63) == 0 {
                    let hint = u32::try_from(thunk)?;
                    self.at(hint, 2)?;
                    let name_rva = hint.checked_add(2).ok_or_else(|| anyhow!("Import name overflow"))?;
                    if self.string(name_rva)? == b"LoadLibraryA" {
                        ensure!(found.is_none(), "Ambiguous LoadLibraryA import");
                        found = Some(iat);
                    }
                }
            }
            ensure!(thunk_terminated, "Unterminated import lookup table");
        }
        ensure!(terminated, "Unterminated import descriptors");
        found.ok_or_else(|| anyhow!("kernel32.dll!LoadLibraryA import not found"))
    }
}

fn overlap(a: u64, a_len: u64, b: u64, b_len: u64) -> bool {
    a_len != 0 && b_len != 0 && a < b + b_len && b < a + a_len
}

fn rel32(target: u32, instruction_end: u64) -> Result<[u8; 4]> {
    Ok(i32::try_from(i64::from(target) - i64::try_from(instruction_end)?)
        .map_err(|_| anyhow!("Startup redirect is outside rel32 range"))?.to_le_bytes())
}

// Exact upstream build_unity_stub instruction layout, without its Rosetta NOP patch.
fn stub(rva: u32, iat: u32, unity_main: u32, game_base: u64) -> Result<[u8; STUB_LEN]> {
    // ADD rax, imm32 sign-extends: larger RVAs would silently call the wrong address.
    ensure!(unity_main <= i32::MAX as u32, "UnityMain RVA exceeds signed immediate range");
    let mut code = [0u8; STUB_LEN];
    code[..27].copy_from_slice(&[
        0x48, 0x83, 0xec, 0x28, 0x48, 0x8d, 0x0d, 69, 0, 0, 0,
        0xff, 0x15, 0, 0, 0, 0, 0x48, 0x85, 0xc0, 0x74, 44,
        0x49, 0x89, 0xc3, 0x48, 0xb9,
    ]);
    code[13..17].copy_from_slice(&rel32(iat, u64::from(rva) + 17)?);
    code[27..35].copy_from_slice(&game_base.to_le_bytes());
    code[35..55].copy_from_slice(&[
        0x31, 0xd2, 0x4c, 0x8d, 0x05, 52, 0, 0, 0,
        0x41, 0xb9, 1, 0, 0, 0, 0x4c, 0x89, 0xd8, 0x48, 0x05,
    ]);
    code[55..59].copy_from_slice(&unity_main.to_le_bytes());
    code[59..76].copy_from_slice(&[
        0xff, 0xd0, 0x48, 0x83, 0xc4, 0x28, 0xc3,
        0xb8, 0xde, 0, 0, 0, 0x48, 0x83, 0xc4, 0x28, 0xc3,
    ]);
    code[76..80].fill(0x90);
    code[80..96].copy_from_slice(b"UnityPlayer.dll\0");
    Ok(code)
}

struct Startup<'a> {
    pe: Pe<'a>,
    entry: u32,
    iat: u32,
    unity_main: u32,
    game_base: u64,
}

impl<'a> Startup<'a> {
    fn parse(base: &'a [u8], game: &[u8], unity: &[u8]) -> Result<Self> {
        let pe = Pe::parse(base)?;
        let entry = pe.export(None)?;
        pe.executable(entry, 16)?;
        let iat = pe.load_library_iat()?;
        let unity_main = Pe::parse(unity)?.export(Some(b"UnityMain"))?;
        let game_base = Pe::parse(game)?.image_base;
        ensure!(unity_main <= i32::MAX as u32, "UnityMain RVA exceeds signed immediate range");
        Ok(Self { pe, entry, iat, unity_main, game_base })
    }

    fn stub(&self, rva: u32) -> Result<[u8; STUB_LEN]> {
        stub(rva, self.iat, self.unity_main, self.game_base)
    }

    fn is_patched(&self) -> Result<bool> {
        let entry = self.pe.at(self.entry, 16)?;
        if entry[0] != 0xe9 || entry[5..] != [0xcc; 11] {
            return Ok(false);
        }
        let displacement = i32::from_le_bytes(entry[1..5].try_into()?);
        let target = i64::from(self.entry) + 5 + i64::from(displacement);
        let Ok(target) = u32::try_from(target) else { return Ok(false) };
        if overlap(u64::from(target), STUB_LEN as u64, u64::from(self.entry), 16)
            || self.pe.executable(target, STUB_LEN).is_err()
        {
            return Ok(false);
        }
        let Ok(expected) = self.stub(target) else { return Ok(false) };
        Ok(self.pe.at(target, STUB_LEN)? == expected)
    }
}

#[cfg(test)]
pub(crate) fn is_patched(base: &[u8], game: &[u8], unity: &[u8]) -> Result<bool> {
    Startup::parse(base, game, unity)?.is_patched()
}

/// Recover a candidate only; the caller MUST match its full hash to verified stock.
pub(crate) fn original(base: &[u8], game: &[u8], unity: &[u8], export_jump: &[u8]) -> Result<Vec<u8>> {
    ensure!(export_jump.len() == 5 && export_jump[0] == 0xe9, "Invalid original export jump");
    let startup = Startup::parse(base, game, unity)?;
    let mut output = base.to_vec();
    if startup.is_patched()? {
        let entry = startup.pe.at(startup.entry, 5)?;
        let target = i64::from(startup.entry) + 5 + i64::from(i32::from_le_bytes(entry[1..5].try_into()?));
        let cave = startup.pe.executable(u32::try_from(target)?, STUB_LEN)?;
        let entry_offset = startup.pe.executable(startup.entry, 16)?;
        output[cave..cave + STUB_LEN].fill(0xcc);
        output[entry_offset..entry_offset + 5].copy_from_slice(export_jump);
    }
    let rosetta_patched = [
        0x21, 0xca, 0x21, 0xca, 0x81, 0xf2, 0xb3, 0xa5, 0xd6, 0x7a,
        0x90, 0x90, 0x90, 0x41, 0x8b, 0x0a, 0x50, 0x48, 0x8d, 0x05,
    ];
    let mut matches = output.windows(rosetta_patched.len())
        .enumerate().filter_map(|(i, window)| (window == rosetta_patched).then_some(i));
    let offset = matches.next();
    ensure!(matches.next().is_none(), "Ambiguous Rosetta recovery signature");
    if let Some(offset) = offset {
        output[offset + 10..offset + 13].copy_from_slice(&[0x0f, 0x1f, 0xc2]);
    }
    Ok(output)
}

pub(crate) fn patch(base: &[u8], game: &[u8], unity: &[u8]) -> Result<Vec<u8>> {
    let startup = Startup::parse(base, game, unity)?;
    if startup.is_patched()? {
        return Ok(base.to_vec());
    }
    let entry = startup.pe.at(startup.entry, 16)?;
    ensure!(entry[0] == 0xe9 && entry[5..] == [0xcc; 11], "PGRBase export preimage is not E9 rel32 followed by eleven CC bytes");
    let old_target = i64::from(startup.entry) + 5 + i64::from(i32::from_le_bytes(entry[1..5].try_into()?));
    startup.pe.executable(u32::try_from(old_target)?, 1)?;
    for section in &startup.pe.sections {
        if !section.executable {
            continue;
        }
        let mut run = 0;
        for (delta, &byte) in bytes(base, section.raw, section.raw_size)?.iter().enumerate() {
            let rva = section.rva + delta as u32;
            if byte == 0xcc && !overlap(u64::from(rva), 1, u64::from(startup.entry), 16) {
                run += 1;
            } else {
                run = 0;
            }
            if run < STUB_LEN {
                continue;
            }
            let cave = rva - (STUB_LEN as u32 - 1);
            let Ok(jump) = rel32(cave, u64::from(startup.entry) + 5) else { continue };
            let Ok(code) = startup.stub(cave) else { continue };
            let cave_offset = startup.pe.executable(cave, STUB_LEN)?;
            let entry_offset = startup.pe.executable(startup.entry, 16)?;
            let mut output = base.to_vec();
            output[cave_offset..cave_offset + STUB_LEN].copy_from_slice(&code);
            output[entry_offset + 1..entry_offset + 5].copy_from_slice(&jump);
            return Ok(output);
        }
    }
    bail!("No non-overlapping executable 124-byte CC cave within rel32 range")
}

#[cfg(test)]
pub(crate) fn fixture() -> (Vec<u8>, Vec<u8>, Vec<u8>) {
    (tests::image(), tests::image(), tests::image())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn put32(data: &mut [u8], offset: usize, value: u32) {
        data[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
    }

    // A synthetic PE, not a supported retail identity. RVAs equal offsets + 0xe00.
    pub(super) fn image() -> Vec<u8> {
        let mut data = vec![0u8; 0xa00];
        data[..2].copy_from_slice(b"MZ");
        put32(&mut data, 0x3c, 0x80);
        data[0x80..0x84].copy_from_slice(b"PE\0\0");
        data[0x84..0x86].copy_from_slice(&0x8664u16.to_le_bytes());
        data[0x86..0x88].copy_from_slice(&1u16.to_le_bytes());
        data[0x94..0x96].copy_from_slice(&240u16.to_le_bytes());
        data[0x98..0x9a].copy_from_slice(&0x20bu16.to_le_bytes());
        data[0xb0..0xb8].copy_from_slice(&0x140000000u64.to_le_bytes());
        put32(&mut data, 0xd0, 0x2000);
        put32(&mut data, 0xd4, 0x200);
        put32(&mut data, 0x104, 16);
        put32(&mut data, 0x108, 0x1000);
        put32(&mut data, 0x10c, 0x80);
        put32(&mut data, 0x110, 0x1100);
        put32(&mut data, 0x114, 40);
        put32(&mut data, 0x190, 0x800);
        put32(&mut data, 0x194, 0x1000);
        put32(&mut data, 0x198, 0x800);
        put32(&mut data, 0x19c, 0x200);
        put32(&mut data, 0x1ac, EXECUTE);
        put32(&mut data, 0x214, 1);
        put32(&mut data, 0x218, 1);
        put32(&mut data, 0x21c, 0x1040);
        put32(&mut data, 0x220, 0x1044);
        put32(&mut data, 0x224, 0x1048);
        put32(&mut data, 0x240, 0x1300);
        put32(&mut data, 0x244, 0x1050);
        data[0x250..0x25a].copy_from_slice(b"UnityMain\0");
        put32(&mut data, 0x300, 0x1150);
        put32(&mut data, 0x30c, 0x1140);
        put32(&mut data, 0x310, 0x1170);
        data[0x340..0x34d].copy_from_slice(b"KERNEL32.dll\0");
        put32(&mut data, 0x350, 0x1180);
        data[0x382..0x38f].copy_from_slice(b"LoadLibraryA\0");
        data[0x500] = 0xe9;
        put32(&mut data, 0x501, 0x3b);
        data[0x505..0x510].fill(0xcc);
        data[0x600..0x600 + STUB_LEN].fill(0xcc);
        data
    }

    #[test]
    fn patch_is_exact_idempotent_and_detects_tampering() {
        let original = image();
        let patched = patch(&original, &original, &original).unwrap();
        assert!(is_patched(&patched, &original, &original).unwrap());
        assert_eq!(patch(&patched, &original, &original).unwrap(), patched);
        let mut expected = original.clone();
        expected[0x501..0x505].copy_from_slice(&0xfbi32.to_le_bytes());
        expected[0x600..0x600 + STUB_LEN].copy_from_slice(&stub(0x1400, 0x1170, 0x1300, 0x140000000).unwrap());
        assert_eq!(patched, expected);
        for offset in [0x600, 0x600 + 13, 0x600 + 27, 0x600 + 55, 0x600 + 123] {
            let mut corrupt = patched.clone();
            corrupt[offset] ^= 1;
            assert!(!is_patched(&corrupt, &original, &original).unwrap());
        }
        let mut different_game = original.clone();
        different_game[0xb1] ^= 1;
        assert!(!is_patched(&patched, &different_game, &original).unwrap());
    }

    #[test]
    fn rejects_malformed_tables_forwarders_and_preimages() {
        let original = image();
        for len in [0, 2, 0x3f, 0x97, 0x187, 0x1af, 0x9ff] {
            assert!(patch(&original[..len], &original, &original).is_err());
        }
        for (offset, value) in [(0x3c, u32::MAX), (0x198, u32::MAX), (0x214, u32::MAX), (0x240, 0x1050), (0x21c, 0x17ff), (0x114, 20)] {
            let mut corrupt = original.clone();
            put32(&mut corrupt, offset, value);
            assert!(patch(&corrupt, &original, &original).is_err());
        }
        let mut corrupt = original.clone();
        corrupt[0x50f] = 0x90;
        assert!(patch(&corrupt, &original, &original).is_err());
        let mut corrupt = original.clone();
        corrupt[0x448..].fill(b'x');
        put32(&mut corrupt, 0x30c, 0x1248);
        assert!(patch(&corrupt, &original, &original).is_err());
    }

    #[test]
    fn cave_cannot_consume_export_padding_or_nonexecutable_data() {
        let original = image();
        let mut base = original.clone();
        base[0x600..0x600 + STUB_LEN].fill(0);
        base[0x505..0x505 + STUB_LEN].fill(0xcc);
        assert!(patch(&base, &original, &original).is_err());
        base[0x505..0x510 + STUB_LEN].fill(0xcc);
        let patched = patch(&base, &original, &original).unwrap();
        assert_eq!(&patched[0x505..0x510], &[0xcc; 11]);
        assert_eq!(&patched[0x510..0x510 + STUB_LEN], &stub(0x1310, 0x1170, 0x1300, 0x140000000).unwrap());
        put32(&mut base, 0x1ac, 0);
        assert!(patch(&base, &original, &original).is_err());
    }

    #[test]
    fn relative_immediates_reject_overflow() {
        assert_eq!(rel32(0, 1 << 31).unwrap(), i32::MIN.to_le_bytes());
        assert!(rel32(0, (1 << 31) + 1).is_err());
        assert!(rel32(u32::MAX, 0).is_err());
        assert!(stub(0, 0, 0x80000000, 0).is_err());
        assert!(stub(u32::MAX - 124, 0, 1, 0).is_err());
    }

    #[test]
    fn upstream_stub_bytes_are_preserved() {
        let hex = concat!(
            "4883ec28488d0d45000000ff15000000004885c0742c4989c348b9",
            "000000400100000031d24c8d053400000041b9010000004c89d84805",
            "56341200ffd04883c428c3b8de0000004883c428c390909090",
            "556e697479506c617965722e646c6c00",
            "00000000000000000000000000000000000000000000000000000000"
        );
        let expected: Vec<u8> = (0..hex.len()).step_by(2)
            .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).unwrap()).collect();
        assert_eq!(stub(0, 17, 0x123456, 0x140000000).unwrap().as_slice(), expected);
    }

    #[test]
    fn candidate_recovery_restores_startup_and_unique_rosetta_patch_only() {
        let stock = image();
        let installed = patch(&stock, &stock, &stock).unwrap();
        assert_eq!(original(&installed, &stock, &stock, &stock[0x500..0x505]).unwrap(), stock);
        assert_eq!(original(&stock, &stock, &stock, &stock[0x500..0x505]).unwrap(), stock);
        assert!(original(&installed, &stock, &stock, &[0xe9]).is_err());
        let signature = [
            0x21, 0xca, 0x21, 0xca, 0x81, 0xf2, 0xb3, 0xa5, 0xd6, 0x7a,
            0x90, 0x90, 0x90, 0x41, 0x8b, 0x0a, 0x50, 0x48, 0x8d, 0x05,
        ];
        let mut wine = installed.clone();
        wine[0x800..0x814].copy_from_slice(&signature);
        let recovered = original(&wine, &stock, &stock, &stock[0x500..0x505]).unwrap();
        let mut expected = stock.clone();
        expected[0x800..0x814].copy_from_slice(&signature);
        expected[0x80a..0x80d].copy_from_slice(&[0x0f, 0x1f, 0xc2]);
        assert_eq!(recovered, expected);
        wine[0x820..0x834].copy_from_slice(&signature);
        assert!(original(&wine, &stock, &stock, &stock[0x500..0x505]).is_err());
    }

    /// Needs a local copy of the CN 4.8.0 client (`ASCNET_CN_CLIENT`, default /tmp/cndl); run with `--ignored`.
    #[test]
    #[ignore]
    fn real_cn_pgrbase_is_patched_and_recovers_to_retail_hash() {
        use sha2::{Digest, Sha256};
        let dir = std::path::PathBuf::from(std::env::var("ASCNET_CN_CLIENT").unwrap_or_else(|_| "/tmp/cndl".into()));
        let read = |name: &str| std::fs::read(dir.join(name)).unwrap_or_else(|e| panic!("{name}: {e}"));
        let (stock, game, unity) = (read("PGRBase.dll"), read("PGR.exe"), read("UnityPlayer.dll"));
        let cn_jump = [0xe9, 0x0b, 0x1b, 0x00, 0x01];
        let patched = patch(&stock, &game, &unity).unwrap();
        assert_ne!(patched, stock);
        assert!(!is_patched(&stock, &game, &unity).unwrap());
        assert!(is_patched(&patched, &game, &unity).unwrap());
        assert_eq!(patch(&patched, &game, &unity).unwrap(), patched);
        let recovered = original(&patched, &game, &unity, &cn_jump).unwrap();
        assert_eq!(format!("{:x}", Sha256::digest(&recovered)), "6c2abc3486218a2ee21ecbe55540e3f2083022a339a2e10b698eaa4293f9102d");
        assert_eq!(recovered, stock);
    }

    /// Read-only 4.9.1 check (`ASCNET_CLIENT_49` holds PGR.exe + UnityPlayer.dll; `ASCNET_BASE_49` is a folder with a
    /// retail-CDN `PGRBase.dll`, default /tmp/cdn49). The Steam-folder PGRBase may already carry our startup stub:
    /// both must patch/recover to the allowlisted retail hash through the allowlisted export jumps.
    #[test]
    #[ignore]
    fn real_49_pgrbase_is_patched_and_recovers_to_allowlisted_stock() {
        use sha2::{Digest, Sha256};
        let dir = |var: &str, default: &str| std::path::PathBuf::from(std::env::var(var).unwrap_or_else(|_| default.into()));
        let client = dir("ASCNET_CLIENT_49", "/Volumes/Lucia/Steam Games/SteamLibrary/steamapps/common/Punishing Gray Raven");
        let retail = dir("ASCNET_BASE_49", "/tmp/cdn49");
        let read = |d: &std::path::Path, name: &str| std::fs::read(d.join(name)).unwrap_or_else(|e| panic!("{name}: {e}"));
        let metadata: serde_json::Value = serde_json::from_slice(&std::fs::read(concat!(env!("CARGO_MANIFEST_DIR"), "/supported-client.json")).unwrap()).unwrap();
        let originals: Vec<&str> = metadata["pgrBase"]["originals"].as_array().unwrap().iter().map(|v| v.as_str().unwrap()).collect();
        let jumps: Vec<Vec<u8>> = metadata["pgrBase"]["originalExportJumps"].as_array().unwrap().iter().map(|j| j.as_array().unwrap().iter().map(|b| b.as_u64().unwrap() as u8).collect()).collect();
        let (game, unity) = (read(&client, "PGR.exe"), read(&client, "UnityPlayer.dll"));
        let hash = |b: &[u8]| format!("{:x}", Sha256::digest(b));
        assert!(metadata["pgrBase"]["unityPlayers"].as_array().unwrap().iter().any(|v| v == &hash(&unity)));
        for (label, base) in [("retail", read(&retail, "PGRBase.dll")), ("steam folder", read(&client, "PGRBase.dll"))] {
            let recovered = jumps.iter().filter_map(|j| original(&base, &game, &unity, j).ok()).find(|o| originals.contains(&hash(o).as_str()));
            let recovered = recovered.unwrap_or_else(|| panic!("{label}: no allowlisted recovery"));
            let patched = patch(&recovered, &game, &unity).unwrap();
            assert!(is_patched(&patched, &game, &unity).unwrap() && !is_patched(&recovered, &game, &unity).unwrap());
            eprintln!("{label}: {} -> stock {} -> patched {}", hash(&base), hash(&recovered), hash(&patched));
        }
    }
}
