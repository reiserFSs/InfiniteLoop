#!/usr/bin/env python3
"""Decode Punishing: Gray Raven/AscNet TCP packets from a tcpdump pcap.

Input: classic pcap with Ethernet/IPv4/TCP frames, e.g.
  sudo tcpdump -i en0 -s 0 -w .runtime/retail.pcap '(tcp or udp)'

Output: JSONL packet summary and optional decoded payload JSON files.
No third-party Python packages required.
"""

from __future__ import annotations

import argparse
import json
import re
import struct
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

CONTENT_TYPES = {
    0: "Request",
    1: "Response",
    2: "Push",
    3: "Exception",
}


def load_haru_key(repo_root: Path) -> bytes:
    crypto_path = repo_root / "AscNet.Common" / "Util" / "Crypto.cs"
    text = crypto_path.read_text(encoding="utf-8-sig")
    match = re.search(r"private static readonly byte\[\] key = new byte\[\] \{([^}]*)\}", text, re.S)
    if not match:
        raise RuntimeError(f"Could not locate HaruCrypt key in {crypto_path}")

    return bytes(int(part.strip()) for part in match.group(1).split(",") if part.strip())


def haru_decrypt(content: bytes, key: bytes) -> bytes:
    data = bytearray(content)
    count = len(data)
    key_offset = count % len(key)

    for i in range(count - 1, -1, -1):
        value = data[i]
        rotation = (((data[i + 1] if i + 1 < count else 0) + count) % 8)
        value = ((value >> (8 - rotation)) | ((value << rotation) & 0xFFFFFFFF)) & 0xFF
        value ^= key[i % len(key)]
        if i > 0:
            value ^= data[i - 1]
        value ^= key[key_offset]
        data[i] = value & 0xFF

    return bytes(data)


def lz4_decompress_block(data: bytes, expected_len: int | None = None) -> bytes:
    out = bytearray()
    i = 0
    n = len(data)

    while i < n:
        token = data[i]
        i += 1

        literal_len = token >> 4
        if literal_len == 15:
            while True:
                if i >= n:
                    raise ValueError("truncated LZ4 literal length")
                extra = data[i]
                i += 1
                literal_len += extra
                if extra != 255:
                    break

        if i + literal_len > n:
            raise ValueError("truncated LZ4 literal bytes")
        out.extend(data[i : i + literal_len])
        i += literal_len

        if i >= n:
            break

        if i + 2 > n:
            raise ValueError("truncated LZ4 match offset")
        offset = data[i] | (data[i + 1] << 8)
        i += 2
        if offset == 0 or offset > len(out):
            raise ValueError(f"invalid LZ4 offset {offset} with output length {len(out)}")

        match_len = token & 0x0F
        if match_len == 15:
            while True:
                if i >= n:
                    raise ValueError("truncated LZ4 match length")
                extra = data[i]
                i += 1
                match_len += extra
                if extra != 255:
                    break
        match_len += 4

        start = len(out) - offset
        for j in range(match_len):
            out.append(out[start + j])

    if expected_len is not None and len(out) != expected_len:
        raise ValueError(f"LZ4 expected {expected_len} bytes, got {len(out)}")
    return bytes(out)


class MessagePackReader:
    def __init__(self, data: bytes):
        self.data = data
        self.offset = 0

    def read(self, count: int) -> bytes:
        if self.offset + count > len(self.data):
            raise EOFError(f"read {count} at {self.offset}, length {len(self.data)}")
        value = self.data[self.offset : self.offset + count]
        self.offset += count
        return value

    def u8(self) -> int:
        return self.read(1)[0]

    def unpack(self) -> Any:
        code = self.u8()

        if code <= 0x7F:
            return code
        if code >= 0xE0:
            return code - 256
        if 0x80 <= code <= 0x8F:
            return {self.unpack(): self.unpack() for _ in range(code & 0x0F)}
        if 0x90 <= code <= 0x9F:
            return [self.unpack() for _ in range(code & 0x0F)]
        if 0xA0 <= code <= 0xBF:
            return self.read(code & 0x1F).decode("utf-8", "replace")

        if code == 0xC0:
            return None
        if code == 0xC2:
            return False
        if code == 0xC3:
            return True
        if code == 0xC4:
            return self.read(self.u8())
        if code == 0xC5:
            return self.read(int.from_bytes(self.read(2), "big"))
        if code == 0xC6:
            return self.read(int.from_bytes(self.read(4), "big"))
        if code == 0xC7:
            length = self.u8()
            ext_type = self.u8()
            return {"__ext_type__": ext_type, "data": self.read(length)}
        if code == 0xC8:
            length = int.from_bytes(self.read(2), "big")
            ext_type = self.u8()
            return {"__ext_type__": ext_type, "data": self.read(length)}
        if code == 0xC9:
            length = int.from_bytes(self.read(4), "big")
            ext_type = self.u8()
            return {"__ext_type__": ext_type, "data": self.read(length)}
        if code == 0xCA:
            return struct.unpack("!f", self.read(4))[0]
        if code == 0xCB:
            return struct.unpack("!d", self.read(8))[0]
        if code == 0xCC:
            return self.u8()
        if code == 0xCD:
            return int.from_bytes(self.read(2), "big")
        if code == 0xCE:
            return int.from_bytes(self.read(4), "big")
        if code == 0xCF:
            return int.from_bytes(self.read(8), "big")
        if code == 0xD0:
            return int.from_bytes(self.read(1), "big", signed=True)
        if code == 0xD1:
            return int.from_bytes(self.read(2), "big", signed=True)
        if code == 0xD2:
            return int.from_bytes(self.read(4), "big", signed=True)
        if code == 0xD3:
            return int.from_bytes(self.read(8), "big", signed=True)
        if code == 0xD4:
            ext_type = self.u8()
            return {"__ext_type__": ext_type, "data": self.read(1)}
        if code == 0xD5:
            ext_type = self.u8()
            return {"__ext_type__": ext_type, "data": self.read(2)}
        if code == 0xD6:
            ext_type = self.u8()
            return {"__ext_type__": ext_type, "data": self.read(4)}
        if code == 0xD7:
            ext_type = self.u8()
            return {"__ext_type__": ext_type, "data": self.read(8)}
        if code == 0xD8:
            ext_type = self.u8()
            return {"__ext_type__": ext_type, "data": self.read(16)}
        if code == 0xD9:
            return self.read(self.u8()).decode("utf-8", "replace")
        if code == 0xDA:
            return self.read(int.from_bytes(self.read(2), "big")).decode("utf-8", "replace")
        if code == 0xDB:
            return self.read(int.from_bytes(self.read(4), "big")).decode("utf-8", "replace")
        if code == 0xDC:
            return [self.unpack() for _ in range(int.from_bytes(self.read(2), "big"))]
        if code == 0xDD:
            return [self.unpack() for _ in range(int.from_bytes(self.read(4), "big"))]
        if code == 0xDE:
            return {self.unpack(): self.unpack() for _ in range(int.from_bytes(self.read(2), "big"))}
        if code == 0xDF:
            return {self.unpack(): self.unpack() for _ in range(int.from_bytes(self.read(4), "big"))}

        raise ValueError(f"unknown MessagePack code 0x{code:02x} at {self.offset - 1}")


def unpack_messagepack(data: bytes) -> Any:
    reader = MessagePackReader(data)
    value = reader.unpack()
    if reader.offset != len(data):
        return {"__value__": value, "__trailing_bytes__": len(data) - reader.offset}
    return value


def maybe_decompress_messagepack_lz4(data: bytes) -> bytes:
    if not data or data[0] not in (0xC7, 0xC8, 0xC9):
        return data

    value = unpack_messagepack(data)
    if not isinstance(value, dict) or value.get("__ext_type__") != 99:
        return data

    payload = value["data"]
    reader = MessagePackReader(payload)
    expected_len = reader.unpack()
    compressed = payload[reader.offset :]
    if not isinstance(expected_len, int):
        raise ValueError(f"invalid LZ4 ext expected length {expected_len!r}")
    return lz4_decompress_block(compressed, expected_len)


def json_safe(value: Any) -> Any:
    if isinstance(value, bytes):
        return {"__bytes_length__": len(value)}
    if isinstance(value, list):
        return [json_safe(item) for item in value]
    if isinstance(value, dict):
        return {str(json_safe(key)): json_safe(item) for key, item in value.items()}
    return value


def payload_summary(value: Any) -> Any:
    if isinstance(value, dict):
        result: dict[str, Any] = {"kind": "map", "keys": list(value.keys())[:32], "key_count": len(value)}
        for key in ("ItemList", "EquipList", "CharacterList", "FashionList", "WeaponFashionList", "PartnerList"):
            if isinstance(value.get(key), list):
                result[f"{key}.count"] = len(value[key])
        return result
    if isinstance(value, list):
        return {"kind": "list", "count": len(value)}
    if isinstance(value, bytes):
        return {"kind": "bytes", "length": len(value)}
    return value


def parse_pcap(path: Path) -> Iterable[tuple[float, bytes]]:
    raw = path.read_bytes()
    if len(raw) < 24:
        raise ValueError("pcap too short")

    magic = raw[:4]
    if magic in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1"):
        endian = "<"
        nano = magic == b"\x4d\x3c\xb2\xa1"
    elif magic in (b"\xa1\xb2\xc3\xd4", b"\xa1\xb2\x3c\x4d"):
        endian = ">"
        nano = magic == b"\xa1\xb2\x3c\x4d"
    else:
        raise ValueError("unsupported capture format: expected classic pcap, not pcapng")

    offset = 24
    while offset + 16 <= len(raw):
        ts_sec, ts_frac, incl_len, _orig_len = struct.unpack(endian + "IIII", raw[offset : offset + 16])
        offset += 16
        frame = raw[offset : offset + incl_len]
        offset += incl_len
        divisor = 1_000_000_000 if nano else 1_000_000
        yield ts_sec + ts_frac / divisor, frame


def ipv4_to_string(data: bytes) -> str:
    return ".".join(str(part) for part in data)


def collect_tcp_segments(path: Path) -> dict[tuple[str, int, str, int], list[tuple[float, int, bytes]]]:
    segments: dict[tuple[str, int, str, int], list[tuple[float, int, bytes]]] = defaultdict(list)

    for timestamp, frame in parse_pcap(path):
        if len(frame) < 14:
            continue
        eth_type = struct.unpack("!H", frame[12:14])[0]
        if eth_type != 0x0800:
            continue

        ip = frame[14:]
        if len(ip) < 20:
            continue
        version = ip[0] >> 4
        ihl = (ip[0] & 0x0F) * 4
        if version != 4 or len(ip) < ihl:
            continue
        if ip[9] != 6:
            continue

        total_len = struct.unpack("!H", ip[2:4])[0]
        src_ip = ipv4_to_string(ip[12:16])
        dst_ip = ipv4_to_string(ip[16:20])
        tcp = ip[ihl:total_len]
        if len(tcp) < 20:
            continue

        src_port, dst_port, seq, _ack, data_offset_flags = struct.unpack("!HHIIH", tcp[:14])
        data_offset = ((data_offset_flags >> 12) & 0x0F) * 4
        payload = tcp[data_offset:]
        if not payload:
            continue

        segments[(src_ip, src_port, dst_ip, dst_port)].append((timestamp, seq, payload))

    return segments


def reassemble(segments: list[tuple[float, int, bytes]]) -> tuple[bytes, float, float, int]:
    if not segments:
        return b"", 0, 0, 0

    first_ts = min(timestamp for timestamp, _seq, _payload in segments)
    last_ts = max(timestamp for timestamp, _seq, _payload in segments)
    base_seq = min(seq for _timestamp, seq, _payload in segments)
    data = bytearray()
    gap_count = 0

    for _timestamp, seq, payload in sorted(segments, key=lambda item: (item[1], item[0])):
        start = seq - base_seq
        if start < 0:
            continue
        if len(data) < start:
            gap_count += 1
            data.extend(b"\x00" * (start - len(data)))
        end = start + len(payload)
        if len(data) < end:
            data.extend(b"\x00" * (end - len(data)))
        data[start:end] = payload

    return bytes(data), first_ts, last_ts, gap_count


def decode_frames(stream: bytes, key: bytes) -> Iterable[tuple[int, int, bytes, Any]]:
    offset = 0
    frame_index = 0
    while offset + 4 <= len(stream):
        packet_len = struct.unpack("<I", stream[offset : offset + 4])[0]
        if packet_len <= 0 or packet_len > 16 * 1024 * 1024:
            break
        if offset + 4 + packet_len > len(stream):
            break

        encrypted = stream[offset + 4 : offset + 4 + packet_len]
        decrypted = maybe_decompress_messagepack_lz4(haru_decrypt(encrypted, key))
        packet = unpack_messagepack(decrypted)
        yield frame_index, packet_len, decrypted, packet
        offset += 4 + packet_len
        frame_index += 1


def decode_packet(packet: Any) -> dict[str, Any]:
    if not isinstance(packet, list) or len(packet) != 3:
        return {"decode_error": "packet envelope was not [No, Type, Content]", "raw": json_safe(packet)}

    packet_no, packet_type, content = packet
    result: dict[str, Any] = {
        "packet_no": packet_no,
        "packet_type": packet_type,
        "packet_type_name": CONTENT_TYPES.get(packet_type, f"Unknown({packet_type})"),
    }

    if not isinstance(content, bytes):
        result["content"] = json_safe(content)
        return result

    inner = unpack_messagepack(content)
    result["inner"] = json_safe(inner)

    payload: bytes | None = None
    if packet_type == 0 and isinstance(inner, list) and len(inner) == 3:
        result["request_id"] = inner[0]
        result["name"] = inner[1]
        payload = inner[2]
    elif packet_type == 1 and isinstance(inner, list) and len(inner) == 3:
        result["response_id"] = inner[0]
        result["name"] = inner[1]
        payload = inner[2]
    elif packet_type == 2 and isinstance(inner, list) and len(inner) == 2:
        result["name"] = inner[0]
        payload = inner[1]
    elif packet_type == 3 and isinstance(inner, list) and len(inner) == 3:
        result["exception_id"] = inner[0]
        result["code"] = inner[1]
        result["message"] = inner[2]

    if isinstance(payload, bytes):
        result["payload_len"] = len(payload)
        if payload:
            payload_value = unpack_messagepack(payload)
            result["payload_summary"] = payload_summary(payload_value)
            result["payload_value"] = payload_value

    return result


def sanitize_filename(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("_") or "packet"


def stream_pairs(segments: dict[tuple[str, int, str, int], list[tuple[float, int, bytes]]], ports: set[int]) -> Iterable[tuple[tuple[str, int, str, int], tuple[str, int, str, int]]]:
    seen: set[frozenset[tuple[str, int, str, int]]] = set()
    for key in segments:
        src_ip, src_port, dst_ip, dst_port = key
        if ports and src_port not in ports and dst_port not in ports:
            continue
        reverse = (dst_ip, dst_port, src_ip, src_port)
        group = frozenset((key, reverse))
        if group in seen:
            continue
        seen.add(group)
        yield key, reverse


def main() -> int:
    parser = argparse.ArgumentParser(description="Decode PGR/AscNet TCP packets from tcpdump pcap files.")
    parser.add_argument("pcap", type=Path)
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--port", action="append", type=int, default=[2335], help="Candidate game TCP port. Repeatable. Use --port 0 to inspect all TCP streams.")
    parser.add_argument("--summary", type=Path, help="Write packet summaries as JSONL.")
    parser.add_argument("--dump-dir", type=Path, help="Write decoded payload JSON for matching packet names.")
    parser.add_argument("--dump-name", action="append", default=[], help="Packet name to dump. Repeatable. Defaults to no full payload dumps.")
    parser.add_argument("--max-streams", type=int, default=8)
    args = parser.parse_args()

    ports = set(args.port or [])
    if 0 in ports:
        ports = set()

    key = load_haru_key(args.repo_root)
    segments = collect_tcp_segments(args.pcap)
    summary_rows: list[dict[str, Any]] = []
    dump_names = set(args.dump_name)

    if args.dump_dir:
        args.dump_dir.mkdir(parents=True, exist_ok=True)

    decoded_streams = 0
    for forward, reverse in stream_pairs(segments, ports):
        if decoded_streams >= args.max_streams:
            break

        f_stream, f_first, f_last, f_gaps = reassemble(segments.get(forward, []))
        r_stream, r_first, r_last, r_gaps = reassemble(segments.get(reverse, []))
        if not f_stream and not r_stream:
            continue

        stream_id = f"{forward[0]}:{forward[1]}-{forward[2]}:{forward[3]}"
        stream_rows: list[dict[str, Any]] = []
        decode_ok = False

        for direction, stream, first_ts, last_ts, gap_count in (
            (f"{forward[0]}:{forward[1]}->{forward[2]}:{forward[3]}", f_stream, f_first, f_last, f_gaps),
            (f"{reverse[0]}:{reverse[1]}->{reverse[2]}:{reverse[3]}", r_stream, r_first, r_last, r_gaps),
        ):
            if not stream:
                continue
            try:
                for frame_index, wire_len, _decoded_bytes, packet in decode_frames(stream, key):
                    decoded = decode_packet(packet)
                    name = decoded.get("name")
                    row = {
                        "stream": stream_id,
                        "direction": direction,
                        "stream_first_ts": first_ts,
                        "stream_last_ts": last_ts,
                        "stream_gap_count": gap_count,
                        "frame_index": frame_index,
                        "wire_len": wire_len,
                        **{k: v for k, v in decoded.items() if k != "payload_value"},
                    }
                    stream_rows.append(row)
                    decode_ok = True

                    if args.dump_dir and name in dump_names and "payload_value" in decoded:
                        filename = f"{len(summary_rows) + len(stream_rows):04d}-{sanitize_filename(str(name))}.json"
                        (args.dump_dir / filename).write_text(
                            json.dumps(json_safe(decoded["payload_value"]), ensure_ascii=False, indent=2),
                            encoding="utf-8",
                        )
            except Exception as exc:
                stream_rows.append({"stream": stream_id, "direction": direction, "decode_error": str(exc)})

        if decode_ok:
            decoded_streams += 1
            summary_rows.extend(stream_rows)

    if args.summary:
        args.summary.parent.mkdir(parents=True, exist_ok=True)
        with args.summary.open("w", encoding="utf-8") as handle:
            for row in summary_rows:
                handle.write(json.dumps(json_safe(row), ensure_ascii=False, separators=(",", ":")) + "\n")
    else:
        for row in summary_rows:
            payload_value = row.pop("payload_value", None)
            print(json.dumps(json_safe(row), ensure_ascii=False))
            if payload_value is not None:
                row["payload_value"] = payload_value

    print(f"decoded_packets={len([row for row in summary_rows if 'packet_type' in row])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
