#!/usr/bin/env python3
"""Import the BigWorld sectors' static BoxColliders (statussyncfight/scenecollider) onto SceneCollider.tsv.

Source (--version): 4.8 .runtime/upgrade-4.8/tables, 4.9 ../PGR_DATA/en-4.9/bytes, each share/statussyncfight/scenecollider/<sector>/*.json.
Only the skygarden* sectors (BigWorld) and BoxCollider rows are kept: the rocks, cliffs and invisible walls that bound a level.
A box's world half extents are Size * Scale / 2; Id is unique per source file.

The 4.9 NewScene0700 sector (15 collider tables) is not imported: no Level row, scene config or scene asset ships for it.

Run from the AscNet root: python3 Scripts/import_scene_colliders_4_8.py [--version 4.8|4.9]
"""
import argparse
import json
from pathlib import Path

ROOTS = {'4.8': Path('.runtime/upgrade-4.8/tables'), '4.9': Path('../PGR_DATA/en-4.9/bytes')}
ap = argparse.ArgumentParser()
ap.add_argument('--version', choices=sorted(ROOTS), default='4.9')
SOURCE = ROOTS[ap.parse_args().version] / 'share/statussyncfight/scenecollider'
TABLE = Path('Resources/table/share/statussyncfight/scenecollider/SceneCollider.tsv')
COLUMNS = ['Sector', 'Scene', 'ColliderId', 'PosX', 'PosY', 'PosZ', 'QuatX', 'QuatY', 'QuatZ', 'QuatW', 'ScaleX', 'ScaleY', 'ScaleZ', 'SizeX', 'SizeY', 'SizeZ']
SOURCE_KEYS = ['PositionX', 'PositionY', 'PositionZ', 'QuaternionX', 'QuaternionY', 'QuaternionZ', 'QuaternionW', 'ScaleX', 'ScaleY', 'ScaleZ', 'SizeX', 'SizeY', 'SizeZ']

lines = ['\t'.join(COLUMNS)]
for sector in sorted(p for p in SOURCE.glob('skygarden*') if p.is_dir()):
    for file in sorted(sector.glob('*.json')):
        for row in json.loads(file.read_text(encoding='utf-8')):
            if row['Type'] == 'BoxCollider':
                lines.append('\t'.join([sector.name, file.stem, str(row['Id']), *(repr(float(row[k])) for k in SOURCE_KEYS)]))
TABLE.parent.mkdir(parents=True, exist_ok=True)
TABLE.write_text('\n'.join(lines) + '\n', encoding='utf-8')
print(f'{TABLE}: {len(lines) - 1} rows')
