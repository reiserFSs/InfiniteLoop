#!/usr/bin/env python3
"""In-repo port of the GameAssembly.dll Rosetta patch (the out-of-repo pgr_apply_patches.py patch_gameassembly), 4.9.1 EN/TW/KR/JP/CN.

The VM section .tvm0 holds exactly one hand-placed junk instruction, a register-form `nop` (`[REX] 0F 1F C0-C7`, modrm mod=11) that is
immediately followed by a load into the same register (`mov r9d,[rdx]` in EN/TW/KR/JP: `41 0F 1F C1 | 44 8B 0A`; `mov edx,[r9]` in
CN: `0F 1F C2 | 41 8B 11`). Rosetta 2 cannot translate register-form 0F 1F, so the VM faults; real CPUs execute it as a no-op, so
replacing it with the same number of `90` is behaviour-neutral. The surrounding junk differs per build/region (why the old per-build
"fallbackN" patterns never matched TW/KR/JP/CN), but the structural signature below matches exactly once in .tvm0 (and in the whole
file) of EN, TW, KR, JP and CN 4.9.1 (verified against the CDN-MD5-verified originals). Idempotency: KNOWN_PATCHED sha256 list.

  apply_rosetta_patches.py [--root <game dir> | --file <GameAssembly.dll>] [--dry-run]

Exit 0 patched/already patched, 1 error, 2 signature missing or not unique (nothing written).
"""
from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import struct
import sys
from pathlib import Path

# [REX] 0F 1F /0 mod=11 directly followed by a load (`[REX] 8B ..`); the lookahead keeps the load out of the match.
SIGNATURE = re.compile(rb"[\x40-\x4f]?\x0f\x1f[\xc0-\xc7](?=[\x40-\x4f]?\x8b)", re.S)
SECTION = b".tvm0"
KNOWN_PATCHED = {  # sha256 of patched 4.9.1 files; UNTESTED in game except EN (3e9e3d54 is the long-running EN install)
    "3e9e3d54aa67e5014dcdaa8af36910deceebeb182539dd1f5d07992e2449a39e",  # en
    "c45344a7f74c133eaa42720f21c308e40668e7620516ba953588b5aff448079d",  # tw
    "a18db6b966de19c039d9d1d812efe547b2ef98adc2537e92115b87716c88dc68",  # kr
    "e4fe4f09435b63d403ceacdd96ded5cf44b597330df361959f4df29ddf2bbfc1",  # jp
    "641486e2ffaecf6c4a131fd36c66060e88af399cde9ce9f2bcb7d9af6c3c1a41",  # cn (3-byte nop; the only non-4-byte site)
}


def section_range(data: bytes, name: bytes) -> tuple[int, int]:
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    count, = struct.unpack_from("<H", data, pe + 6)
    opt, = struct.unpack_from("<H", data, pe + 20)
    for i in range(count):
        off = pe + 24 + opt + 40 * i
        if data[off:off + 8].rstrip(b"\0") == name:
            raw_size, raw_off = struct.unpack_from("<II", data, off + 16)
            return raw_off, raw_off + raw_size
    raise ValueError(f"section {name.decode()} not found (not a protected 4.9 GameAssembly.dll?)")


def patch(data: bytearray) -> str:
    """Return 'patched' | 'already' | 'missing' | 'ambiguous'; mutates data only for 'patched'."""
    if hashlib.sha256(data).hexdigest() in KNOWN_PATCHED:
        return "already"
    start, end = section_range(bytes(data[:4096]), SECTION)
    hits = [(m.start(), m.end() - m.start()) for m in SIGNATURE.finditer(data, start, end)]
    if len(hits) > 1:
        return "ambiguous"
    if not hits:
        return "missing"
    offset, length = hits[0]
    data[offset:offset + length] = b"\x90" * length
    print(f"patched Rosetta NOP ({length} bytes) at file+0x{offset:x}")
    return "patched"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", type=Path, help="PGR install directory")
    ap.add_argument("--file", type=Path, help="GameAssembly.dll path (overrides --root)")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()
    path = args.file or (args.root / "GameAssembly.dll" if args.root else None)
    if not path or not path.exists():
        print("ERROR: give --root or --file pointing at an existing GameAssembly.dll", file=sys.stderr)
        return 1
    data = bytearray(path.read_bytes())
    try:
        result = patch(data)
    except ValueError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 1
    if result == "already":
        print("Rosetta NOP already patched")
        return 0
    if result != "patched":
        print(f"GameAssembly Rosetta NOP signature {result}; nothing written", file=sys.stderr)
        return 2
    print("sha256 after patch:", hashlib.sha256(data).hexdigest())
    if not args.dry_run:
        backup = path.with_name(path.name + ".pre-rosetta")
        if not backup.exists():
            shutil.copy2(path, backup)
        path.write_bytes(data)
    return 0


if __name__ == "__main__":
    sys.exit(main())
