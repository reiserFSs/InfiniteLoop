"""Import the Munchy Pixels (PacMan2) server tables (retail rows, no local columns).

PacMan2Activity.TimeId/GroupId pick the open activity and its stage group; PacMan2Stage carries the
progression (PreStageId), score stars and the key/star reward ids PacMan2Module serves. The client-only
pacman2 tables are not read by the server.

    import_pacman2_4_9.py
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render

SOURCE = Path('../PGR_DATA/en-4.9/bytes')
TARGET = Path('Resources/table')
MANIFEST = TARGET / 'manifest.json'
TABLES = ['share/miniactivity/pacman2/PacMan2Activity', 'share/miniactivity/pacman2/PacMan2Stage']


def header_for(rows):
    columns = []
    for field, sample in rows[0].items():
        if isinstance(sample, list):
            columns.extend(f'{field}[{i}]' for i in range(1, max(max(len(row[field]) for row in rows), 1) + 1))
        else:
            columns.append(field)
    return columns


def main():
    manifest = json.loads(MANIFEST.read_text())
    by_path = {table['path']: table for table in manifest['tables']}
    for name in TABLES:
        rows = json.loads((SOURCE / f'{name}.json').read_text())
        header = header_for(rows)
        text = render(header, project(rows, header, [], key=header[0]))
        (TARGET / f'{name}.tsv').parent.mkdir(parents=True, exist_ok=True)
        (TARGET / f'{name}.tsv').write_text(text)
        by_path[f'{name}.tsv'] = {
            'path': f'{name}.tsv', 'naturalKey': header[0], 'filter': 'all', 'rows': len(rows), 'columns': len(header),
            'contentSha256': hashlib.sha256(text.encode()).hexdigest(),
            'source': [f'{name}.json', f'4.9 installed table PGR_DATA/en-4.9/bytes/{name}.json '
                                       f'(json sha256:{hashlib.sha256((SOURCE / f"{name}.json").read_bytes()).hexdigest()})'],
            'transformation': ['import_pacman2_4_9: retail rows verbatim; list columns widened to the longest row'],
        }
        print(f'{name}: {len(rows)} rows, {len(header)} columns')
    manifest['tables'] = [by_path.pop(table['path'], table) for table in manifest['tables']] + list(by_path.values())
    MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')


if __name__ == '__main__':
    main()
