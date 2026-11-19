"""Import the EN 4.9 client share table Share/Medal/Medal.tab (the 8 player medals).

Server use: the /unlock command validates medal ids and keep-time against it
(XMedalManager reads the same table). Only server-relevant columns are projected.
Appends a manifest entry (idempotent by path). Run from the repo root.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render

SOURCE = Path('../PGR_DATA/en-4.9/bytes/share/medal/Medal.json')
NAME = 'share/medal/Medal'
TARGET = Path('Resources/table')
MANIFEST = TARGET / 'manifest.json'
HEADER = ['Id', 'Name', 'UnlockType', 'Priority', 'KeepTime']


def main():
    manifest = MANIFEST.read_text()
    rows = json.loads(SOURCE.read_text())
    output = render(HEADER, project(rows, HEADER, [], key='Id'))
    table = TARGET / f'{NAME}.tsv'
    table.parent.mkdir(parents=True, exist_ok=True)
    table.write_text(output)
    print(f'{NAME}: {len(rows)} rows')
    if f'"path": "{NAME}.tsv"' in manifest:
        return
    entry = {
        'path': f'{NAME}.tsv',
        'rows': len(rows),
        'columns': len(HEADER),
        'naturalKey': 'Id',
        'contentSha256': hashlib.sha256(output.encode()).hexdigest(),
        'source': ['EN 4.9 bytes/share/medal/Medal.json'],
        'sourceSha256': {SOURCE.name: hashlib.sha256(SOURCE.read_bytes()).hexdigest()},
        'filter': 'all rows; server-relevant columns only',
        'transformation': ['project listed columns'],
    }
    marker = manifest.index('\n  ],', manifest.index('"tables"'))
    text = '    ' + json.dumps(entry, indent=2).replace('\n', '\n    ')
    MANIFEST.write_text(manifest[:marker] + ',\n' + text + manifest[marker:])


if __name__ == '__main__':
    main()
