"""Import the Dreamscape Disequilibrium (Reform Balance) server tables (retail rows, no local columns).

ReformCfg/ReformChapter/ReformStage pick the activity and its stages, ReformUseCharaGroup and the seven
ReformBalance* tables drive weight selection, mass and bond rules, ReformEnemyTarget carries the monster rows
ReformModule turns into NpcGroupList. ReformBalanceRecommend is client-only; it is imported for completeness.

    import_reform_4_9.py
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render

SOURCE = Path('../PGR_DATA/en-4.9/bytes')
TARGET = Path('Resources/table')
MANIFEST = TARGET / 'manifest.json'
BALANCE = ['Affix', 'AffixGroup', 'Bond', 'Character', 'Config', 'Mass', 'TargetGroup']
TABLES = ['share/fuben/reform/' + name for name in
          ['ReformCfg', 'ReformChapter', 'ReformStage', 'ReformUseCharaGroup', 'ReformEnemyTarget']
          + [f'ReformBalance{name}' for name in BALANCE]] + ['client/fuben/reform/ReformBalanceRecommend']


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
            'transformation': ['import_reform_4_9: retail rows verbatim; list columns widened to the longest row'],
        }
        print(f'{name}: {len(rows)} rows, {len(header)} columns')
    manifest = json.loads(MANIFEST.read_text())  # re-read: sibling importers rewrite the shared manifest
    current = {table['path']: table for table in manifest['tables']}
    current.update({path: by_path[path] for path in (f'{name}.tsv' for name in TABLES)})
    manifest['tables'] = [current.pop(table['path']) for table in manifest['tables']] + list(current.values())
    MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')


if __name__ == '__main__':
    main()
