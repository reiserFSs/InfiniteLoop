"""Import share/robot/RobotPartner (retail rows, no local columns).

Robot.RobotPartnerId selects the CUB a premade robot carries (Adaptation Fitting, teaching and trial
stages); FightModule.BuildRobotDeployment reads it to fill the fight NpcData Partner.

    import_robot_partner_4_9.py   (run from the repo root)
"""
from __future__ import annotations

import hashlib
import json

import import_pacman2_4_9 as base
from import_client_tables import project, render

NAME = 'share/robot/RobotPartner'


def main():
    manifest = json.loads(base.MANIFEST.read_text())
    source = base.SOURCE / f'{NAME}.json'
    rows = json.loads(source.read_text())
    header = base.header_for(rows)
    text = render(header, project(rows, header, [], key=header[0]))
    (base.TARGET / f'{NAME}.tsv').write_text(text)
    entry = {
        'path': f'{NAME}.tsv', 'naturalKey': header[0], 'filter': 'all', 'rows': len(rows), 'columns': len(header),
        'contentSha256': hashlib.sha256(text.encode()).hexdigest(),
        'source': [f'{NAME}.json', f'4.9 installed table PGR_DATA/en-4.9/bytes/{NAME}.json '
                                   f'(json sha256:{hashlib.sha256(source.read_bytes()).hexdigest()})'],
        'transformation': ['import_robot_partner_4_9: retail rows verbatim; list columns widened to the longest row'],
    }
    manifest['tables'] = [t for t in manifest['tables'] if t['path'] != entry['path']] + [entry]
    base.MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')
    print(f'{NAME}: {len(rows)} rows, {len(header)} columns')


if __name__ == '__main__':
    main()
