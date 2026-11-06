"""Re-import 4.9 client tables onto the existing server TSV schemas.

Compares the staged 4.8 install tables (.runtime/upgrade-4.8/tables) with the staged 4.9 install
tables (../PGR_DATA/en-4.9/bytes) and, for every runtime TSV that has a same-path source in both,
applies only the 4.8->4.9 retail delta with import_client_tables.project(baseline_rows=...):
changed retail cells overwrite, new rows are appended, local-only cells/rows (including rows retail
removed) are kept. Derived/policy tables (no same-path source) are not touched here.

    import_tables_4_9.py            dry run, prints per-table delta
    import_tables_4_9.py --apply    write TSVs, manifest entries and .runtime/upgrade-4.9/import-report.json
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from import_client_tables import parse, project, render
from import_theatre6_4_8 import widen

TARGET = Path('Resources/table')
MANIFEST = TARGET / 'manifest.json'
OLD = Path('.runtime/upgrade-4.8/tables')
NEW = Path('../PGR_DATA/en-4.9/bytes')
REPORT = Path('.runtime/upgrade-4.9/import-report.json')
# Filtered local subsets: only rows already present locally are refreshed (manifest filter says so).
EXISTING_ONLY = {'client/config/ClientConfig', 'client/text/Text', 'share/chat/Emoji',
                 'share/task/CurrentTask', 'share/task/CurrentCondition', 'share/task/CurrentReward',
                 'share/task/CurrentRewardGoods', 'share/task/StoryTask', 'share/task/StoryTaskCondition'}
# Alias subset tables fed by a differently named retail table (rows keyed by the same Id).
ALIAS = {
    'share/task/CurrentTask': 'share/task/Task', 'share/task/StoryTask': 'share/task/Task',
    'share/character/skill/CharacterSkillUpgrade': 'client/character/skill/CharacterSkillUpgradeIdOptimize',
    'share/character/skill/CharacterGeneralSkill': 'share/character/CharacterGeneralSkill',
    'share/task/CurrentCondition': 'share/task/Condition', 'share/task/StoryTaskCondition': 'share/task/Condition',
    'share/task/CurrentReward': 'share/reward/Reward', 'share/task/CurrentRewardGoods': 'share/reward/RewardGoods',
}
# Retail renumbered Id (ordinal) in 4.9; handlers resolve by (EquipId, Times).
KEYS = {'share/equip/EquipBreakThrough': ('EquipId', 'Times'),
        'share/character/skill/CharacterSkillLevelEffect': ('SkillId', 'Level'),
        'share/character/skill/CharacterSkillUpgrade': ('SkillId', 'Level'),
        'share/equip/EquipConfig': ('SuitId', 'ItemId')}
# Tables whose runtime shape is not a plain projection of the same-named source; handled elsewhere.

def split_skill_level(rows):
    """Runtime TSV carries SkillId/Level; retail packs them as SkillLevelId = SkillId*100 + Level (Level 0 allowed)."""
    return [{**r, 'SkillId': r['SkillLevelId'] // 100, 'Level': r['SkillLevelId'] % 100} for r in rows]


def zip_discounts(rows):
    """Retail ships three parallel Config arrays; the runtime table is one row per (suit, item) discount."""
    values = {r['Key']: r['Values'] for r in rows}
    suits, items, counts = (values[f'ChipResonanceUseItemDiscount{k}'] for k in ('SuitId', 'ItemId', 'ItemCount'))
    assert len(suits) == len(items) == len(counts)
    return [{'SuitId': a, 'ItemId': b, 'DiscountCount': c} for a, b, c in zip(suits, items, counts)]


TRANSFORMS = {'share/character/skill/CharacterSkillLevelEffect': split_skill_level,
              'share/character/skill/CharacterSkillUpgrade': split_skill_level, 'share/equip/EquipConfig': zip_discounts}
ASSIGN_ID = {'share/character/skill/CharacterSkillLevelEffect', 'share/character/skill/CharacterSkillUpgrade'}  # legacy ordinal Id: new rows continue after the max


def index(root):
    return {str(p.relative_to(root).with_suffix('')).lower(): p for p in root.rglob('*.json') if p.parent != root}


def unwrap(rows, header):
    """Scalar TSV column over a one-element source list (4.8 runtime convention): take the element."""
    scalar = {c for c in header if '[' not in c}

    def cell(k, v):
        if isinstance(v, dict) and v and all(i.isdigit() for i in v):  # typed arrays decoded as {"0": .., "1": ..}
            v = [v.get(str(i)) for i in range(max(map(int, v)) + 1)]
        return (v[0] if v else None) if k in scalar and isinstance(v, list) and len(v) <= 1 else v

    return [{k: cell(k, v) for k, v in r.items()} for r in rows]


def raw_tab(json_path):
    tabs = [p for p in json_path.parent.glob('*.tab') if p.stem.lower() == json_path.stem.lower()]
    return tabs[0] if tabs else None  # a few typed tables (e.g. Attrib) are staged JSON-only


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    apply = parser.parse_args().apply
    old, new = index(OLD), index(NEW)
    document = json.loads((NEW / 'index-delta.json').read_text())['index_sha1']['document/matrix']
    report = json.loads(REPORT.read_text()) if apply and REPORT.exists() else {}  # re-runs keep earlier entries
    for tsv in sorted(TARGET.rglob('*.tsv')):
        name = str(tsv.relative_to(TARGET).with_suffix(''))
        src = ALIAS.get(name, name).lower()
        if src not in old or src not in new:
            continue
        before, after = json.loads(old[src].read_text()), json.loads(new[src].read_text())
        if before == after:
            continue
        before, after = [TRANSFORMS.get(name, list)(rows) for rows in (before, after)]
        old_text = tsv.read_text(encoding='utf-8-sig')
        header, current = parse(old_text)
        key = KEYS.get(name, header[0])
        ident = (lambda r: tuple(str(r[k]) for k in key)) if isinstance(key, tuple) else (lambda r: str(r[key]))
        for rows in (before, after, current):
            assert len({ident(r) for r in rows}) == len(rows), f'{name}: {key} not unique'
        after, before = unwrap(after, header), unwrap(before, header)
        in_current = {ident(r) for r in current}
        retail_added = {ident(r) for r in after} - {ident(r) for r in before}
        removed_ids = {ident(r) for r in before} - {ident(r) for r in after}
        # Local TSVs are often scoped subsets of retail: refresh existing rows, take only rows retail added in 4.9
        # (EXISTING_ONLY tables take no new rows at all).
        in_current |= set() if name in EXISTING_ONLY else retail_added
        after = [r for r in after if ident(r) in in_current]
        before = [r for r in before if ident(r) in in_current]
        header = widen(header, after)
        try:
            projected = project(after, header, current, baseline_rows=before, key=key)
        except ValueError as error:
            print(f'{name}: FAILED {error}')
            report[name] = {'failed': str(error)}
            continue
        removed_in_retail = sorted(map(str, removed_ids))
        by_ident = {ident(r): r for r in projected}  # keep the local row order; new retail rows follow
        projected = [by_ident.pop(ident(r), None) or {c: r.get(c, '') for c in header} for r in current] + list(by_ident.values())
        if name in ASSIGN_ID:
            top = max(int(r['Id']) for r in projected if r['Id'] != '')
            for r in projected:
                if r['Id'] == '':
                    top += 1
                    r['Id'] = str(top)
        text = render(header, projected)
        assert len(projected) - len(current) <= len(retail_added), name
        entry = report[name] = {
            'rows_before': len(current), 'rows_after': len(projected),
            'added': len(projected) - len(current), 'text_changed': text != old_text,
            'retail_removed_kept_local': removed_in_retail, 'columns_widened': len(header) - len(old_text.split('\n')[0].split('\t')),
        }
        print(f'{name}: {len(current)} -> {len(projected)}' + (' CHANGED' if text != old_text else ' (no text change)')
              + (f' retail-removed kept: {len(removed_in_retail)}' if removed_in_retail else ''))
        if not apply or text == old_text:
            continue
        tsv.write_text(text)
        source = new[src]
        entry['tsv_sha256'] = hashlib.sha256(text.encode()).hexdigest()
        entry['source'] = f'4.9 installed document index sha1:{document}; PGR_DATA/en-4.9/bytes/{name}.tab'
        entry['source_json_sha256'] = sha(source)
        tab = raw_tab(source)
        entry['source_tab_sha256'] = sha(tab) if tab else None
        entry['columns'] = len(header)
        entry['key'] = key if isinstance(key, str) else ','.join(key)
    if apply:
        REPORT.write_text(json.dumps(report, indent=1) + '\n')
        manifest = json.loads(MANIFEST.read_text())
        by_path = {t['path']: t for t in manifest['tables']}
        for name, entry in report.items():
            if 'tsv_sha256' not in entry:
                continue
            row = by_path.setdefault(f'{name}.tsv', {'path': f'{name}.tsv', 'naturalKey': entry['key'], 'filter': 'all',
                                                       'source': [], 'transformation': []})
            if row not in manifest['tables']:
                manifest['tables'].append(row)
            stale = {k: row.pop(k) for k in ('sourceSha256', 'sourceSha1', 'indexSha1', 'sourceTableSha1', 'sourceBundleSha1', 'sourceScope') if k in row}
            if stale:  # hashes describe the previous (4.8) source; keep them as the import baseline
                row.setdefault('baseline', stale)
            row['rows'], row['columns'], row['contentSha256'] = entry['rows_after'], entry['columns'], entry['tsv_sha256']
            row['source'] = [s for s in row['source'] if not str(s).startswith('4.9 ')] + [
                f"{entry['source']} (json sha256:{entry['source_json_sha256']}; tab sha256:{entry['source_tab_sha256'] or 'staged JSON only'})"]
            note = 'import_tables_4_9: 4.8->4.9 retail delta via project(baseline_rows); local-only cells/rows kept'
            if isinstance(row.get('transformation'), list):
                if note not in row['transformation']:
                    row['transformation'].append(note)
            else:
                row['transformation'] = [t for t in [row.get('transformation'), note] if t]
        MANIFEST.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n')


if __name__ == '__main__':
    main()
