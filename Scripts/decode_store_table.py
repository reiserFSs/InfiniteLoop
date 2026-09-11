"""Decode current client BinaryTable scalar and numeric-list columns using Binary/Reader.lua's format.

Input is an extracted TextAsset, not network traffic. Produces authoritative TSV
for the server table generator. Deliberately rejects unsupported column types.
"""
import csv
import struct
import sys
from pathlib import Path


class DecodeError(ValueError):
    """The payload is not a bounded BinaryTable supported by this decoder."""


def decode(data):
    pos = 4
    if len(data) < 4:
        raise DecodeError("BinaryTable is shorter than its header")

    def integer():
        nonlocal pos
        value = shift = 0
        while True:
            if pos >= len(data):
                raise DecodeError("truncated variable-length integer")
            byte = data[pos]
            pos += 1
            value |= (byte & 127) << shift
            if byte < 128:
                return value if value <= 0x7fffffff else value - 0x100000000
            shift += 7
            if shift > 35:
                raise DecodeError("variable-length integer exceeds 32 bits")

    def string():
        nonlocal pos
        try:
            end = data.index(0, pos)
        except ValueError as exc:
            raise DecodeError("unterminated BinaryTable string") from exc
        value = data[pos:end].decode('utf-8')
        pos = end + 1
        return value

    header_size = struct.unpack_from('<I', data)[0]
    if header_size > len(data) - 4:
        raise DecodeError("BinaryTable header exceeds payload")
    columns = [(integer(), string()) for _ in range(integer())]
    if len(columns) > 10000:
        raise DecodeError("BinaryTable has too many columns")
    primary_size = 0
    if integer():
        integer()  # primary key column
        primary_size = integer()
    row_size, row_count, content_size = integer(), integer(), integer()
    if min(row_size, row_count, content_size) < 0:
        raise DecodeError("BinaryTable header contains a negative size")
    content = 4 + header_size + primary_size + row_size
    if content > len(data) or content_size > len(data) - content:
        raise DecodeError("BinaryTable content exceeds payload")
    if columns and row_count > max(1, content_size):
        raise DecodeError("BinaryTable row count is inconsistent with content size")
    pool = content + content_size
    pool_columns, strings = set(), []
    if pool < len(data):
        pos = pool
        pool_header = integer()
        if pool_header > len(data) - pool:
            raise DecodeError("BinaryTable string-pool header exceeds payload")
        if pool_header:
            pos = pool + 4
            column_count, string_count, column_size, offset_size = [integer() for _ in range(4)]
            if min(column_count, string_count, column_size, offset_size) < 0:
                raise DecodeError("BinaryTable string-pool header contains a negative size")
            if column_count > len(columns) or offset_size != string_count * 4:
                raise DecodeError(
                    "unsupported BinaryTable string-pool layout "
                    f"(columns={column_count}, strings={string_count}, "
                    f"column_size={column_size}, offset_size={offset_size})"
                )
            pos = pool + 4 + pool_header
            pool_columns = {integer() for _ in range(column_count)}
            offsets = pool + 4 + pool_header + column_size
            if offsets + offset_size > len(data):
                raise DecodeError("BinaryTable string-pool offsets exceed payload")
            start = offsets + offset_size
            previous = 0
            for i in range(string_count):
                if offsets + i * 4 + 4 > len(data):
                    raise DecodeError("BinaryTable string-pool offset exceeds payload")
                end = struct.unpack_from('<I', data, offsets + i * 4)[0]
                if end < previous or start + end > len(data):
                    raise DecodeError("BinaryTable string-pool string exceeds payload")
                strings.append(data[start + previous:start + end].rstrip(b'\0').decode('utf-8'))
                previous = end
    pos = content
    content_end = content + content_size
    rows = []
    for _ in range(row_count):
        if pos > content_end:
            raise DecodeError("BinaryTable row cursor exceeds content")
        row = []
        for index, (kind, name) in enumerate(columns):
            if kind == 2:
                if index in pool_columns:
                    string_index = integer()
                    if string_index < 0 or string_index >= len(strings):
                        raise DecodeError("BinaryTable string-pool index exceeds pool")
                    value = strings[string_index]
                else:
                    value = string()
            elif kind == 7:
                count = integer()
                if count < 0 or count > content_end - pos:
                    raise DecodeError("BinaryTable decimal-list count exceeds content")
                value = [format(integer() / 10000, ".4f") for _ in range(count)]
            elif kind == 6:
                count = integer()
                if count < 0 or count > content_end - pos:
                    raise DecodeError("BinaryTable integer-list count exceeds content")
                value = [integer() for _ in range(count)]
            elif kind in (1, 23):
                if pos >= len(data):
                    raise DecodeError("BinaryTable byte column exceeds payload")
                value = data[pos]
                pos += 1
            elif kind == 14:
                value = integer()
            elif kind == 15:
                value = format(integer() / 10000, '.4f')
            else:
                raise ValueError(f'Unsupported column {name}: {kind}')
            row.append(value)
            if pos > content_end:
                raise DecodeError("BinaryTable column exceeds content")
        rows.append(row)
    if pos != content + content_size:
        raise DecodeError('Content size mismatch')
    # The C# table generator represents lists as repeated, numbered TSV columns.
    widths = [max(1, max((len(row[i]) for row in rows), default=0)) if kind in (6, 7) else 1
              for i, (kind, _) in enumerate(columns)]
    headers = [f'{name}[{j + 1}]' if kind in (6, 7) else name
               for (kind, name), width in zip(columns, widths) for j in range(width)]
    expanded = []
    for row in rows:
        flattened = []
        for value, width in zip(row, widths):
            flattened.extend(value + [''] * (width - len(value)) if isinstance(value, list) else [value])
        expanded.append(flattened)
    return headers, expanded


if __name__ == '__main__':
    columns, rows = decode(Path(sys.argv[1]).read_bytes())
    output = Path(sys.argv[2])
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open('w', encoding='utf-8', newline='') as stream:
        writer = csv.writer(stream, delimiter='\t', lineterminator='\n')
        writer.writerow(columns)
        writer.writerows(rows)
    print(f'Decoded {len(rows)} rows to {output}')
