"""Import a region's own id tables as a per-region overlay (Resources/regions/<region>/table/share/...).

The server serves ONE EN-derived table set to every region (Docs/development.md), so a TW/KR/JP/CN client is sent ids its own
tables lack (tasks, collection-box activities). The overlay holds the region's retail rows for exactly the tables that decide
which ids that client can resolve, projected onto the server table's own columns (never copied wholesale):

  share/task/Task                                         Id            (a client resolves task ids against Task.tab + BigWorldTask.tab)
  share/mailcollectionbox/MailCollectionBoxActivity       server columns
  share/mailcollectionbox/MailCollectionBoxFavoriteMail   server columns
  share/pay/Pay                                           server columns minus GoodsId (the client's SDK product string, never read by
                                                          the server; the table's int column cannot hold it). Natural key: Key.
  client/purchase/PurchasePackageYKUiConfig               server columns (the monthly-pass package ids the client resolves: CN 10000/201/202 vs EN 83028/90291/90292)
  share/pay/AccumulatedPayReward                          server columns (CN only: its Money tiers are the card amounts the server credits;
                                                          KR/JP Money is in another unit, so they keep the shared rows)

Source: Scripts/fetch_region_tables.py + scan_client_upgrade.py output (PGR_DATA/<region>-4.9/bytes). Provenance goes to
Resources/table/manifest.json `regionTables.<region>`.

    import_region_tables.py --region tw
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

from import_client_tables import project, render

TARGET = Path('Resources')
MANIFEST = TARGET / 'table/manifest.json'
# table -> (columns kept (None = the server table's own header), natural key, regions that get it (None = all))
TABLES = {
    'share/task/Task': (['Id'], 'Id', None),
    'share/mailcollectionbox/MailCollectionBoxActivity': (None, 'Id', None),
    'share/mailcollectionbox/MailCollectionBoxFavoriteMail': (None, 'Id', None),
    'share/pay/Pay': (['Key', 'Platform', 'Amount', 'Currency', 'ShowAmount', 'Name', 'Desc', 'MoneyCard', 'Icon', 'PayId', 'ShowUIType'],
                      'Key', None),
    'share/pay/AccumulatedPayReward': (None, 'Id', {'cn'}),
    'client/purchase/PurchasePackageYKUiConfig': (None, 'Id', None),
}
INDEXED = re.compile(r'(.+)\[(\d+)\]')


def header_for(table, rows, wanted):
    """The server table's header, widened when a source row carries more list items than the server table has columns."""
    if wanted is not None:
        return wanted
    server = (TARGET / f'table/{table}.tsv').read_text().split('\n', 1)[0].split('\t')
    header, seen = [], set()
    for column in server:
        match = INDEXED.fullmatch(column)
        name = match.group(1) if match else column
        if name in seen:
            continue
        seen.add(name)
        if match:
            width = max(len(row[name]) for row in rows)
            header.extend(f'{name}[{i}]' for i in range(int(match.group(2)), int(match.group(2)) + max(width, 1)))
        else:
            header.append(column)
    return header


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--region', required=True)
    args = parser.parse_args()
    source = Path(f'../PGR_DATA/{args.region}-4.9/bytes')
    index = json.loads((source / 'index-delta.json').read_text())['index_sha1']
    entries = []
    for table, (wanted, natural_key, regions) in TABLES.items():
        if regions is not None and args.region not in regions:
            continue
        rows = json.loads((source / f'{table}.json').read_text())
        header = header_for(table, rows, wanted)
        text = render(header, project(rows, header, [], key=natural_key))
        dest = TARGET / f'regions/{args.region}/table/{table}.tsv'
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(text)
        entries.append({
            'path': f'regions/{args.region}/table/{table}.tsv', 'naturalKey': natural_key, 'filter': 'all',
            'rows': len(rows), 'columns': len(header), 'contentSha256': hashlib.sha256(text.encode()).hexdigest(),
            'source': [f'PGR_DATA/{args.region}-4.9/bytes/{table}.json (json sha256:'
                       f'{hashlib.sha256((source / f"{table}.json").read_bytes()).hexdigest()})'],
            'transformation': 'import_region_tables: retail rows projected onto the server table columns' + (
                f' (columns {",".join(wanted)} only)' if wanted else '; list columns widened to the longest row'),
        })
        print(f'{args.region} {table}: {len(rows)} rows, {len(header)} columns')
    manifest = json.loads(MANIFEST.read_text())
    manifest.setdefault('regionTables', {})[args.region] = {
        'source': 'SHA-verified effective installed-package + document-patch table bundles from the region\'s own Kuro CDN, '
                  'Scripts/fetch_region_tables.py + scan_client_upgrade.py --extract into PGR_DATA/<region>-4.9/bytes',
        'indexSha1': index, 'capturesUsed': False, 'tables': entries,
    }
    MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')


if __name__ == '__main__':
    main()
