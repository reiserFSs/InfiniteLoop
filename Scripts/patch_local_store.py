"""Prepare/apply a reversible local-recharge callback patch to the installed client.

Requires UnityPy, pycryptodome and msgpack. The optional --catalog patch adds
reward-based missing-icon resolution and mutually exclusive ownership/status
overlays. Existing local recharge patches are preserved. Preparation never
modifies the installed game.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
from pathlib import Path

import msgpack
import UnityPy

MARKER = '-- AscNet local recharge completion'
KEY = bytes.fromhex('587865636f6472506547616b61326536')
CATALOG_MARKER = '-- AscNet server-owned purchase catalog icons'
CATALOG_CALLERS = {
    'XUiPurchaseLBListItem.lua': 'self.ItemData',
    'XUiPurchaseCoatingLBListItem.lua': 'self.ItemData',
    'XUiPurchaseBuyTips.lua': 'data',
    'XUiPurchaseBundleGrid.lua': 'data',
    'XUiPurchaseComboSubGrid.lua': 'data',
    'XUiBigListGrid.lua': 'data',
    'XUiChongzhiTanchuang.lua': 'data',
    'XUiDrawPanelLbItem.lua': 'self.ItemData',
    'XUiGridRegressionGift.lua': 'data',
    'XUiMonthlyCardEn.lua': 'item.Data',
    'XUiPanelRecommendComboPackageGrid.lua': 'data',
    'XUiPanelRecommendEmojiItem.lua': 'package.Data',
}


def patch_catalog_lua(name, script):
    newline = '\r\n' if '\r\n' in script else '\n'
    if name == 'XPurchaseConfigs.lua':
        if CATALOG_MARKER in script:
            raise RuntimeError('Client already has the catalog patch; use a fresh output only for an unpatched bundle')
        anchor = ('function XPurchaseConfigs.GetIconPathByIconName(iconName)\n'
                  '    return PurchaseIconAssetPathConfig[iconName]\nend').replace('\n', newline)
        if script.count(anchor) != 1:
            raise RuntimeError('Purchase icon resolver changed; refusing an ambiguous patch')
        replacement = Path(__file__).with_name('store_catalog_icons.lua').read_text(encoding='utf-8')
        return script.replace(anchor, replacement.rstrip().replace('\n', newline))
    if name in CATALOG_CALLERS:
        owner = CATALOG_CALLERS[name]
        anchor = f'XPurchaseConfigs.GetIconPathByIconName({owner}.Icon)'
        if script.count(anchor) != 1:
            raise RuntimeError(f'{name}: purchase icon call changed')
        script = script.replace(anchor, f'XPurchaseConfigs.GetIconPathByIconName({owner}.Icon, {owner})')
        if name in ('XUiPurchaseLBListItem.lua', 'XUiPurchaseCoatingLBListItem.lua'):
            pattern = r'(?m)^([ \t]*)self\.ImgSellout\.gameObject:SetActive\(true\)'
            script, count = re.subn(pattern, lambda match: match.group(0) + newline + match[1]
                + 'if self.ImgHave then self.ImgHave.gameObject:SetActive(false) end', script)
            if count != 4:
                raise RuntimeError(f'{name}: expected four status transitions, found {count}')
    return script


def digest(data):
    return hashlib.sha256(data).hexdigest()


def text_assets(env):
    return {obj.path_id: (obj.read().m_Name, digest(obj.get_raw_data()))
            for obj in env.objects if obj.type.name == 'TextAsset'}


def prepare(game, output, catalog_patch=False):
    if output.exists() and any(output.iterdir()):
        raise RuntimeError('Output must be empty to preserve existing rollback backups')
    UnityPy.set_assetbundle_decrypt_key(KEY)
    base = game / 'PGR_Data' / 'StreamingAssets'
    index = UnityPy.load(str(base / 'document/matrix/index'))
    index_asset = next(obj.read() for obj in index.objects if obj.type.name == 'TextAsset')
    catalog = msgpack.unpackb(index_asset.m_Script.encode('utf-8', 'surrogateescape'), strict_map_key=False)[0]
    filename = catalog['assets/temp/lua/matrix.ab'][0]
    source = next(path for folder in ('document/matrix', 'resource/matrix')
                  if (path := base / folder / filename).is_file())
    original = source.read_bytes()
    env = UnityPy.load(original)
    before = text_assets(env)
    target_names = {'XPayManager.lua'}
    if catalog_patch:
        target_names |= {'XPurchaseConfigs.lua', *CATALOG_CALLERS}
    targets = [obj for obj in env.objects if obj.type.name == 'TextAsset' and obj.read().m_Name in target_names]
    if len(targets) != len(target_names):
        raise RuntimeError('Expected exactly one TextAsset for each store patch target')
    changed = []
    scripts = {}
    for target in targets:
        data = target.read()
        previous = data.m_Script
        if data.m_Name == 'XPayManager.lua':
            data.m_Script = patch_recharge_lua(data.m_Script)
        elif catalog_patch:
            data.m_Script = patch_catalog_lua(data.m_Name, data.m_Script)
        if data.m_Script != previous:
            changed.append(target.path_id)
            scripts[data.m_Name] = data.m_Script
            data.save()
    if not changed:
        raise RuntimeError('No new patch to prepare')
    patched = env.file.save(packer='lz4')
    verified = UnityPy.load(patched)
    after = text_assets(verified)
    assert before.keys() == after.keys()
    assert {key for key in before if before[key] != after[key]} == set(changed)
    for obj in verified.objects:
        if obj.path_id in changed:
            data = obj.read()
            assert data.m_Script == scripts[data.m_Name]
    output.mkdir(parents=True, exist_ok=True)
    (output / 'original.bundle').write_bytes(original)
    (output / 'patched.bundle').write_bytes(patched)
    for name, script in scripts.items():
        (output / name.replace('.lua', '.patched.lua')).write_text(script, encoding='utf-8', newline='')
    (output / 'manifest.json').write_text(json.dumps({
        'source': str(source.resolve()), 'original_sha256': digest(original),
        'patched_sha256': digest(patched), 'verified_text_assets': len(before),
        'changed_scripts': sorted(scripts),
    }, indent=2), encoding='utf-8')
    print(f'Prepared and verified {len(changed)} changed Lua scripts among {len(before)} TextAssets: {output}')


def patch_recharge_lua(script):
    if MARKER in script:
        return script
    anchor = '            DoPay(productKey, res.GameOrder, template.GoodsId)'
    if script.count(anchor) != 1:
        raise RuntimeError('Client callback changed; refusing an ambiguous patch')
    insertion = '''            -- AscNet local recharge completion
            if res.LocalCompleted then
                XDataCenter.KickOutManager.Unlock(XEnumConst.KICK_OUT.LOCK.RECHARGE, true)
                XUiManager.OpenUiObtain(res.RewardList or {})
                XEventManager.DispatchEvent(XEventId.EVENT_SUCCESS_PAY)
                return
            end
'''
    if '\r\n' in script:
        insertion = insertion.replace('\n', '\r\n')
    return script.replace(anchor, insertion + anchor)


def apply(output, restore=False):
    if os.name == 'nt':
        processes = subprocess.check_output(['tasklist', '/FI', 'IMAGENAME eq PGR.exe', '/FO', 'CSV'], text=True)
        if 'PGR.exe' in processes:
            raise RuntimeError('Exit PGR before applying/restoring the client patch')
    manifest = json.loads((output / 'manifest.json').read_text(encoding='utf-8'))
    target = Path(manifest['source'])
    expected = manifest['patched_sha256' if restore else 'original_sha256']
    if digest(target.read_bytes()) != expected:
        raise RuntimeError('Installed bundle changed since preparation; refusing to overwrite')
    payload = (output / ('original.bundle' if restore else 'patched.bundle')).read_bytes()
    assert digest(payload) == manifest['original_sha256' if restore else 'patched_sha256']
    temporary = target.with_suffix(target.suffix + '.ascnet-store.tmp')
    temporary.write_bytes(payload)
    temporary.replace(target)
    assert digest(target.read_bytes()) == digest(payload)
    print('Restored original client bundle' if restore else 'Applied verified local store client patch')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game-dir', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--restore', action='store_true')
    parser.add_argument('--catalog', action='store_true', help='Add missing purchase icons and status overlay fixes')
    args = parser.parse_args()
    if args.apply or args.restore:
        apply(args.output, restore=args.restore)
    else:
        if args.game_dir is None:
            parser.error('--game-dir is required for preparation')
        prepare(args.game_dir, args.output, args.catalog)
