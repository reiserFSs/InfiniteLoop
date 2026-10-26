"""Import EN 4.8 VersionGift / AccumulateExpendShop / GameCollection share tables (all authored columns kept)."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render
from import_team_recommend_4_8 import expand

SOURCE = Path('../PGR_DATA/en/bytes')
TARGET = Path('Resources/table')
TABLES = {
    'share/miniactivity/versiongift/VersionGiftActivity': 'Id Name DailyGiftReward VersionGiftReward MailId ActivityTaskGroup NormalTaskGroup DailyTaskGroup',
    'share/miniactivity/versiongift/VersionGiftProcess': 'ActivityId TaskCompleteCount[5] RewardId[5]',
    'share/miniactivity/accumulateexpendshop/AccumulateExpendShopActivity': 'Id ItemId ShopId ShopItemId ItemExchangeRate SignRewardId EndTipsMailId PromptLeftTime ShopItemMaxCount OpenConditionIds[2]',
    'share/miniactivity/accumulateexpendshop/AccumulateExpendShopSignReward': 'Id RewardIds[2]',
    'share/miniactivity/gamecollection/GameCollection': 'Id Name GameType StageId Image',
    'share/miniactivity/gamecollection/GameCollectionActivity': 'Id Name TimeId TaskTimeLimitIds[2] ShopId',
}
KEYS = {'share/miniactivity/versiongift/VersionGiftProcess': 'ActivityId'}


def main():
    manifest_path = TARGET / 'manifest.json'
    text_manifest = manifest_path.read_text()
    for name in TABLES:  # drop stale entries textually (the shared manifest is never reformatted)
        marker = f'"path": "{name}.tsv"'
        if marker in text_manifest:
            at = text_manifest.rindex('    {\n', 0, text_manifest.index(marker))
            end = text_manifest.index('    },\n', at) + len('    },\n')
            text_manifest = text_manifest[:at] + text_manifest[end:]
    added = []
    for name, spec in TABLES.items():
        rows = json.loads((SOURCE / f'{name}.json').read_text())
        header = expand(spec)
        key = KEYS.get(name, 'Id')
        text = render(header, project(rows, header, [], key=key))
        table = TARGET / f'{name}.tsv'
        table.parent.mkdir(parents=True, exist_ok=True)
        table.write_text(text)
        print(f'{name}: {len(rows)} authored rows')
        added.append({
            'columns': len(header),
            'contentSha256': hashlib.sha256(text.encode()).hexdigest(),
            'filter': 'all',
            'naturalKey': key,
            'path': f'{name}.tsv',
            'rows': len(rows),
            'source': [f'EN PGR_DATA/en/bytes/{name}.json'],
            'transformation': 'Direct projection; arrays expanded to 1-based columns.',
        })
    if added:  # splice textually so the shared manifest is not reformatted
        block = ''.join(
            '    ' + json.dumps(entry, indent=2, ensure_ascii=False).replace('\n', '\n    ') + ',\n' for entry in added)
        marker = '"tables": [\n'
        at = text_manifest.index(marker) + len(marker)
        manifest_path.write_text(text_manifest[:at] + block + text_manifest[at:])

if __name__ == '__main__':
    main()
