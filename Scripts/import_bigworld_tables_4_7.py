"""Import BigWorld/SkyGarden/DlcWorld tables from the installed client into Resources/table.

For every table of the selected version's corpus (.tab or .json), decode the matching .tab from the installed
client's matrix bundles (document layer first, then resource; ../tools/decode_binary_table.py). Binary decode is
the authority; the corpus JSON is used only when the bundle/table is absent or uses a column
type the decoder cannot read. List columns become Field[1..N] (N >= 2 so the generator
emits List<T>). DlcWorld.tsv is projected onto its existing schema (all worlds).
Writes .runtime/bigworld-tables-manifest.json (path, source, rows).

Run from the AscNet root: python3 Scripts/import_bigworld_tables_4_7.py [--version 4.8|4.9]
(the installed client is the source of truth; --version picks the corpus for the JSON fallback and
the table list; the scene-object importer and import_scene_colliders_4_8.py take the same flag).
"""
from __future__ import annotations
import argparse

import importlib.util
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from decode_pgr_pcap import MessagePackReader  # noqa: E402
from import_client_tables import parse, project, render  # noqa: E402
import pgr_bundles  # noqa: E402

VERSIONS = {'4.8': Path('../PGR_DATA/en/bytes'), '4.9': Path('../PGR_DATA/en-4.9/bytes')}


def _version():
    """--version of this script and of the scene-object importer, which execs this file up to main()."""
    ap = argparse.ArgumentParser(description=(__doc__ or '').split('\n\n')[0])
    ap.add_argument('--version', choices=sorted(VERSIONS), default='4.9')
    return ap.parse_args().version


CORPUS = VERSIONS[_version()]
TARGET = Path('Resources/table')
MANIFEST = Path('.runtime/bigworld-tables-manifest.json')
# Whole directories (recursive) and single tables, relative to CORPUS.
SOURCE_DIRS = ['share/bigworld', 'share/dlcworld/questsystem']
SKIP_DIRS = ('share/bigworld/common/audio/',)  # client audio cues; no server consumer
SOURCE_FILES = [
    'share/statussyncfight/level/World.json',
    'share/bigworld/common/fovsave/BigWorldLevelFovSave.json',  # binary-only (no corpus JSON)
    'share/statussyncfight/level/Level.json',
    'share/statussyncfight/level/BigWorldLevelPlay.json',
    'share/statussyncfight/level/InstanceLevelTeach.json',
    'share/statussyncfight/sceneobject/SceneObjectBase.json',
    'share/statussyncfight/sceneobject/SceneObjectAction.json',  # bases with actions carry XSceneObjectActionComponent
    # BigWorld actor replication: Npc.AttribId/ScriptId, Part rows, and the full attribute rows they reference.
    'share/statussyncfight/npc/Npc.json',
    'share/statussyncfight/npc/LevelNpcBase.json',  # level NPC base -> FightNpcId, RolePartModelIds
    'share/statussyncfight/part/Part.json',
    # Trial team members (level action AddTrialNpcToTeam): TrialNpc -> TrialPlayerNpc | TrialPlayerSelfNpc.
    'share/statussyncfight/npc/trialnpc/TrialNpc.json',
    'share/statussyncfight/npc/trialnpc/TrialPlayerNpc.json',
    'share/statussyncfight/npc/trialnpc/TrialPlayerSelfNpc.json',
    'share/text/CodeText.json',
    # BigWorld gameplay actors (XGameplay*.NeedCreate): GameplayLevelId / GameplayLevelIdsStr per gameplay config.
    'share/statussyncfight/gameplay/bigworldmain/BigWorldMainConfig.json',
    'share/statussyncfight/gameplay/dormitory/DormitoryConfig.json',
    'share/statussyncfight/gameplay/dormitory/DormitoryPhotoWall.json',  # container SceneObjId -> scene object base
    'share/statussyncfight/gameplay/dormitory/DormitoryFrameWall.json',
    'share/statussyncfight/gameplay/dormitory/DormitoryPhotos.json',
    'share/statussyncfight/gameplay/dormitory/DormitoryPhotoAdorns.json',
    'share/statussyncfight/gameplay/dormitory/DormitoryFrameGoods.json',
    'share/statussyncfight/gameplay/dormitory/DormitorySkins.json',
    'share/statussyncfight/gameplay/cafe/Config.json',
    'share/statussyncfight/gameplay/sgstreet/SgStreetConfig.json',
    'share/statussyncfight/gameplay/sguav/SkyGardenUAVConfig.json',
    'share/statussyncfight/gameplay/sgheavyartilleryfire/SgHeavyArtilleryFireConfig.json',
    'share/statussyncfight/gameplay/sgstreetshow/SgStreetShowConfig.json',
]
# Existing server TSVs whose (narrower) schema other code already consumes.
PROJECT_ONTO_EXISTING = {'share/dlcworld/DlcWorld.json', 'share/statussyncfight/level/World.json'}
# Existing server TSVs widened to the full shipped row and column set (consumers read columns by name).
REPLACE_EXISTING = {'share/dlcworld/DlcWorldAttrib.json'}
KEYS = {'share/statussyncfight/part/Part.json': ('Npc', 'PartId')}
SUPPORTED = {1, 2, 3, 4, 5, 6, 8, 10, 12, 14, 15}
LIST_TYPES = {4, 5, 6, 8, 10}


def _load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


dbt = _load(Path('../tools/decode_binary_table.py'), 'decode_binary_table')


def _read_value(self, type_index):
    # Type 4 is List<string>: count, then NUL-terminated strings (upstream reads one string).
    if type_index == 4:
        self.aot_index += 1
        if self.folder and self.folder.is_pool_column(self.aot_index - 1):
            raise ValueError('pooled List<string> column')
        return [self.read_raw_string() for _ in range(self.read_uint())]
    return _upstream_read_value(self, type_index)


_upstream_read_value = dbt.Reader.read_value
dbt.Reader.read_value = _read_value
_manifest = dbt.load_json(dbt.DEFAULT_MANIFEST)
_assets = dbt.load_module(dbt.ASSET_VIEWER, 'pgr_assets')
_bundles: dict[str, dict] = {}


def bundle_tables(directory: str) -> dict:
    """{casefolded table name: BinaryTable} for assets/temp/bytes/<directory>.ab."""
    if directory not in _bundles:
        tables = {}
        try:
            path = pgr_bundles.resolve(f'assets/temp/bytes/{directory}.ab')  # document layer overrides resource (4.9: npc, fashion)
            payload = pgr_bundles.payload(path)
            for item in dbt.iter_text_assets(_assets, payload):
                name = str(item['name']).rsplit('/', 1)[-1]
                if name.endswith('.bytes'):
                    continue
                try:
                    tables[name.removesuffix('.tab').casefold()] = dbt.BinaryTable(name, item['data'])
                except Exception:  # noqa: BLE001 - non-table TextAssets
                    pass
        except FileNotFoundError:
            pass
        _bundles[directory] = tables
    return _bundles[directory]


def _text(value):
    if isinstance(value, str):
        return value.replace('\\', '\\\\').replace('\t', '\\t').replace('\r', '\\r').replace('\n', '\\n')
    return value


def decode(rel: str):
    """Return (columns[(name, is_list)], rows, source)."""
    directory, name = rel.rsplit('/', 1)
    name = name.removesuffix('.json')
    table = bundle_tables(directory).get(name.casefold())
    if table is not None and all(c['type'] in SUPPORTED for c in table.columns):
        try:
            rows = table.decode_rows()
            columns = [(c['name'], c['type'] in LIST_TYPES) for c in table.columns]
            return columns, rows, 'binary'
        except Exception:  # noqa: BLE001 - fall back to corpus JSON
            pass
    rows = json.loads((CORPUS / rel).read_text(encoding='utf-8'))
    columns = []
    for row in rows:
        for key, value in row.items():
            if key not in [c for c, _ in columns]:
                columns.append((key, isinstance(value, list)))
    return columns, rows, 'json'


def header_for(columns, rows):
    header = []
    for name, is_list in columns:
        if is_list:
            width = max([2] + [len(r.get(name) or []) for r in rows])
            header += [f'{name}[{i}]' for i in range(1, width + 1)]
        else:
            header.append(name)
    return header


class QuestReader(MessagePackReader):
    """Native QuestData_<id> MessagePack: vectors are bare consecutive floats; polymorphic
    values are a type-name string ('XConfig...' or a Params-like key) followed by its map;
    ObjectiveList items are an int type tag followed by the objective map."""
    UNION_KEYS = {'Params', 'LeftVarToken', 'RightVarToken', 'Literal', 'LeftQuestVarToken', 'RightQuestVarToken'}

    def _count(self, code):
        return code & 0x0F if code <= 0x9F else int.from_bytes(self.read(2 if code in (0xDC, 0xDE) else 4), 'big')

    def _glue(self, name):
        code = self.data[self.offset] if self.offset < len(self.data) else 0
        return {'$type': name, **self.unpack()} if 0x80 <= code <= 0x8F or code in (0xDE, 0xDF) else name

    def unpack(self):
        code = self.data[self.offset]
        if 0x80 <= code <= 0x8F or code in (0xDE, 0xDF):
            self.offset += 1
            out = {}
            for _ in range(self._count(code)):
                key = self.unpack()
                if key == 'ObjectiveList':
                    inner = self.u8()
                    value = [{'$tag': MessagePackReader.unpack(self), **self.unpack()} for _ in range(self._count(inner))]
                else:
                    value = self.unpack()
                    if isinstance(value, float) and self.offset < len(self.data) and self.data[self.offset] in (0xCA, 0xCB):
                        value = [value]
                        while self.offset < len(self.data) and self.data[self.offset] in (0xCA, 0xCB):
                            value.append(MessagePackReader.unpack(self))
                    elif isinstance(value, str) and (key in self.UNION_KEYS or value.startswith('XConfig')):
                        value = self._glue(value)
                out[key] = value
            return out
        if 0x90 <= code <= 0x9F or code in (0xDC, 0xDD):
            self.offset += 1
            items = []
            for _ in range(self._count(code)):
                value = self.unpack()
                items.append(self._glue(value) if isinstance(value, str) and value.startswith('XConfig') else value)
            return items
        return MessagePackReader.unpack(self)


def _flat(row):
    """Keep scalar and scalar-list fields (TSV-representable); nested action/script graphs go to
    the row's Config cell as compact JSON (EnterActions, ExitActions, FixProcessors, TargetArgs,
    TracePosArgs, TraceActorArgs, ...), keyed by their native field names."""
    def nested(v):
        return isinstance(v, dict) or (isinstance(v, list) and any(isinstance(x, (dict, list)) for x in v))
    flat = {k.replace('$tag', 'ObjectiveType'): v for k, v in row.items() if not nested(v) and k not in ('StepList', 'ObjectiveList')}
    config = {k: v for k, v in row.items() if nested(v) and k not in ('StepList', 'ObjectiveList')}
    flat['Config'] = json.dumps(config, separators=(',', ':'), ensure_ascii=False, default=bytes.hex) if config else ''
    return flat


def import_quests(report):
    """Native quest config (XTableDlcQuest/Step/Objective) from QuestData_* into three TSVs.
    Objectives keep their native ObjectiveList position (Order): Serial steps run in that order,
    which is not id order."""
    path = pgr_bundles.resolve('assets/temp/bytes/share/statussyncfight/quest.ab')
    payload = pgr_bundles.payload(path)
    quests, steps, objectives = [], [], []
    for item in dbt.iter_text_assets(_assets, payload):
        if not str(item['name']).startswith('QuestData_'):
            continue
        reader = QuestReader(item['data'])
        quest = reader.unpack()
        if reader.offset != len(item['data']):
            raise ValueError(f"{item['name']}: trailing bytes")
        quests.append(_flat(quest))
        for step in quest.get('StepList') or []:
            steps.append({'QuestId': quest['Id'], **_flat(step), 'QuestId': quest['Id']})  # last wins: the native step QuestId is 0 for some quests; key stays first
            for order, objective in enumerate(step.get('ObjectiveList') or []):
                objectives.append({'QuestId': quest['Id'], 'StepId': step['Id'], 'Order': order, **_flat(objective), 'QuestId': quest['Id']})
    for name, rows in (('DlcQuest', quests), ('DlcQuestStep', steps), ('DlcQuestObjective', objectives)):
        rows.sort(key=lambda r: r['Id'])
        columns = []
        for row in rows:
            for key, value in row.items():
                if key not in [c for c, _ in columns]:
                    columns.append((key, isinstance(value, list)))
        columns.sort(key=lambda c: c[0] != 'Id')
        rows = [{k: ([_text(x) for x in v] if isinstance(v, list) else _text(v)) for k, v in r.items()} for r in rows]
        header = header_for(columns, rows)
        target = TARGET / f'share/statussyncfight/quest/{name}.tsv'
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(render(header, project(rows, header, [], key='Id')), encoding='utf-8')
        report[str(target)] = {'source': 'binary (QuestData_* MessagePack; nested fields as Config JSON)', 'rows': len(rows)}
        print(f'{target}: {len(rows)} rows (binary quest config)')
    import_level_action_exec_modes(report, quests, objectives)


# ALevelAction getters in the installed GameAssembly.dll (x64): `test al,al; jne; mov eax,imm32 |
# xor eax,eax; add rsp,20h; pop rbx; ret` after an il2cpp init check. get_ExecMode is the next
# method, 0x50 bytes after get_ActionType (every XLevelAction* class in dump.cs).
_GETTER = re.compile(rb'\x84\xC0\x75.(?:\xB8(....)|\x33\xC0)\x48\x83\xC4\x20\x5B\xC3', re.S)


def import_level_action_exec_modes(report, quests, objectives):
    """ELevelActionExecMode per ELevelActionType for every action type the quest config or the level interact options
    (LevelInteractOption.tsv Config, written by import_bigworld_scene_objects_4_7.py) use."""
    dll = (Path(str(_manifest['source_install'])) / 'GameAssembly.dll').read_bytes()
    options = TARGET / 'share/statussyncfight/level/sceneconfig/LevelInteractOption.tsv'
    if not options.exists():
        raise FileNotFoundError(f'{options}: run Scripts/import_bigworld_scene_objects_4_7.py first')
    configs = [row.get('Config') or '' for row in quests + objectives] + options.read_text(encoding='utf-8').splitlines()
    types = {}
    for config in configs:
        for match in re.finditer(r'"ActionType":(\d+),"Params":\{"\$type":"(\w+)"', config):
            types[int(match.group(1))] = match.group(2)
    rows = []
    for action_type, params in sorted(types.items()):
        modes = set()
        pattern = rb'\x84\xC0\x75.\xB8' + re.escape(action_type.to_bytes(4, 'little')) + rb'\x48\x83\xC4\x20\x5B\xC3'
        for hit in re.finditer(pattern, dll, re.S):
            start = hit.start()
            while start > 0 and not (start % 16 == 0 and dll[start - 1] == 0xCC):
                start -= 1
            mode = _GETTER.search(dll, start + 0x50, start + 0xA0)
            if mode and mode.start() - (start + 0x50) < 0x20:
                value = int.from_bytes(mode.group(1), 'little') if mode.group(1) else 0
                if value <= 4:
                    modes.add(value)
        if len(modes) != 1:
            raise ValueError(f'ELevelActionType {action_type} ({params}): ExecMode candidates {sorted(modes)}')
        rows.append({'ActionType': action_type, 'ExecMode': modes.pop(), 'Params': params})
    target = TARGET / 'share/statussyncfight/quest/LevelActionExecMode.tsv'
    target.write_text(render(['ActionType', 'ExecMode', 'Params'], rows), encoding='utf-8')
    report[str(target)] = {'source': 'GameAssembly.dll ALevelAction.get_ExecMode', 'rows': len(rows)}
    print(f'{target}: {len(rows)} action types')


def _same_content(target, header, rows):
    """True when the committed TSV already holds `rows`. Blank, 0, 0.0 and False are one value: the 4.9 binary tables spell
    the defaults the 4.8 JSON left blank, and rewriting them would turn nullable columns into plain ones for no change."""
    def norm(value):
        text = str(value)
        try:
            return '' if float(text) == 0 else str(float(text))
        except ValueError:
            return '' if text.lower() == 'false' else text
    old_header, old_rows = parse(target.read_text(encoding='utf-8-sig'))
    columns = [*old_header, *(c for c in header if c not in old_header)]
    keyed = lambda rs: [tuple(norm(r.get(c, '')) for c in columns) for r in rs]
    return keyed(old_rows) == keyed(rows)


def main():
    sources = sorted({*(str((p.parent / (p.stem + '.json')).relative_to(CORPUS)) for d in SOURCE_DIRS for p in (CORPUS / d).rglob('*')
                          if p.suffix in ('.json', '.tab') and not str(p.relative_to(CORPUS)).startswith(SKIP_DIRS)),
                      *SOURCE_FILES, *PROJECT_ONTO_EXISTING, *REPLACE_EXISTING})
    report = {}
    tracked = {t['path'] for t in json.loads((TARGET / 'manifest.json').read_text(encoding='utf-8'))['tables']}
    for rel in sources:
        try:
            columns, rows, source = decode(rel)
        except FileNotFoundError:  # undecodable column types and no corpus JSON: keep the committed TSV
            report[rel] = {'skipped': 'binary decode failed and the corpus has no JSON'}
            print(f'SKIP {rel}: not decodable')
            continue
        if any(isinstance(v, (dict,)) or (isinstance(v, list) and any(isinstance(x, (dict, list)) for x in v))
               for r in rows for v in r.values()):
            report[rel] = {'skipped': 'nested object values cannot be TSV cells'}
            print(f'SKIP {rel}: nested')
            continue
        rows = [{k: ([_text(x) for x in v] if isinstance(v, list) else _text(v)) for k, v in r.items()} for r in rows]
        target = TARGET / rel.replace('.json', '.tsv')
        if str(target.relative_to(TARGET)) in tracked:  # provenance-tracked: import_tables_4_9.py owns the refresh
            report[rel] = {'skipped': 'tracked in Resources/table/manifest.json'}
            continue
        key = KEYS.get(rel, columns[0][0])
        if rel in PROJECT_ONTO_EXISTING and target.exists():
            header, current = parse(target.read_text(encoding='utf-8-sig'))
            key = header[0]
            projected = project(rows, header, current, key=key)
        else:
            header = header_for(columns, rows)
            projected = project(rows, header, [], key=key)
        if target.exists() and _same_content(target, header, projected):
            report[str(target)] = {'source': source, 'rows': len(projected), 'from': rel, 'unchanged': True}
            print(f'{target}: unchanged ({source})')
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(render(header, projected), encoding='utf-8')
        report[str(target)] = {'source': source, 'rows': len(projected), 'from': rel}
        print(f'{target}: {len(projected)} rows ({source})')
    import_quests(report)
    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(json.dumps(report, indent=2) + '\n')


if __name__ == '__main__':
    main()
