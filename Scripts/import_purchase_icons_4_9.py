"""Import the 4.9 EN client purchase icon table (name -> artwork path) verbatim.

The client resolves a package's `Icon` through XPurchaseConfigs.GetIconPathByIconName (this table); the server must only
send names it contains whose artwork the 4.9 bundle registry also holds.

    import_purchase_icons_4_9.py
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render

NAME = 'client/purchase/PurchaseIconAssetPath'
SOURCE = Path('../PGR_DATA/en-4.9/bytes') / f'{NAME}.json'
TARGET = Path('Resources/table')
MANIFEST = TARGET / 'manifest.json'


def main():
    rows = json.loads(SOURCE.read_text())
    header = ['Icon', 'AssetPath', 'CoverImgPath']
    text = render(header, project(rows, header, [], key='Icon'))
    (TARGET / f'{NAME}.tsv').write_text(text)
    entry = {
        'path': f'{NAME}.tsv', 'naturalKey': 'Icon', 'filter': 'all', 'rows': len(rows), 'columns': len(header),
        'contentSha256': hashlib.sha256(text.encode()).hexdigest(),
        'source': [f'{NAME}.json', f'4.9 installed table PGR_DATA/en-4.9/bytes/{NAME}.json '
                                   f'(json sha256:{hashlib.sha256(SOURCE.read_bytes()).hexdigest()})'],
        'transformation': ['import_purchase_icons_4_9: retail rows verbatim'],
    }
    manifest = json.loads(MANIFEST.read_text())
    manifest['tables'] = [t for t in manifest['tables'] if t['path'] != entry['path']] + [entry]
    MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')
    print(f'{NAME}: {len(rows)} rows')


if __name__ == '__main__':
    main()
