"""Import the 4.9 Time Trace (Anniversary pre-heating) tables (retail rows, no local columns).

Share: Activity (TimeId, GroupIds), Group (TimeId, RewardId, ContentIds), Content (reading entries).
Client: AnniversaryPreHeatingClientConfig. ActivityScheduleService derives the missing-calendar windows from the
Activity/Group TimeIds, so a new version needs only a re-import.

    import_anniversary_preheating_4_9.py
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render
from import_fuben_activity_4_9 import header_for

SOURCE = Path('../PGR_DATA/en-4.9/bytes')
TARGET = Path('Resources/table')
MANIFEST = TARGET / 'manifest.json'
TABLES = [f'{side}/miniactivity/anniversarypreheating/AnniversaryPreHeating{name}' for side, name in
          [('share', 'Activity'), ('share', 'Group'), ('share', 'Content'), ('client', 'ClientConfig')]]


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
            'transformation': ['import_anniversary_preheating_4_9: retail rows verbatim; list columns widened to the longest row'],
        }
        print(f'{name}: {len(rows)} rows, {len(header)} columns')
    manifest['tables'] = [by_path.pop(table['path'], table) for table in manifest['tables']] + list(by_path.values())
    MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')


if __name__ == '__main__':
    main()
