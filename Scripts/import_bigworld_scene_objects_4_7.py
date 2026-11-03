#!/usr/bin/env python3
"""Extract BigWorld level scene objects (place ids + CollectableComponent) from the installed client.

Source: assets/temp/bytes/share/statussyncfight/level/sceneconfig/skygarden*.ab, TextAssets
ConfigGroup_<id> = native XTableLevelSceneConfigGroupNew (dump.cs D:795528) MessagePack.
The installed build has drifted from dump.cs (extra keys), so a full schema decode is not possible.
Scene objects are located structurally: map key placeId -> array [placeId, groupId, baseId(SceneObjectBase),
Position(3 bare float32), EulerAngles(3 bare float32), ...] (XTableSceneObjectNew keys 0-4).
CollectableComponent (XTableSceneObjectCollectableComp [CollectWay, RewardId, CollectableType,
CourseGroupId, CollectCueId]) is the 5-int array inside that object whose RewardId exists in BigWorldReward.
TeleporterComponent (XTableSceneObjectTeleporterComp [ActivateMode, DefaultActive, InactiveOptionId,
ActiveOptionId, InactiveAnimation:string, ...], 11 keys) is the 11-array starting int, bool, int, int, string.
Validation: per group, the number of collectables equals the native CollectableSceneObjectCount (key 3);
the script aborts otherwise.
Interact options: scene objects' InteractableComponent (18-array: ShouldReactToPlayerInteract, LauncherSpots,
DefaultOptionId, NarrativeId, Options, ...) and level NPCs' InteractOptions (key 18) hold XTableInteractOption lists
(dump.cs order, 4 extra installed fields skipped). They go to LevelInteractOption.tsv (ActorType 2 scene object / 1 npc,
ConfigGroupId, PlaceId, OptionId, ..., Config JSON with CompleteActionList / LevelConditionGroup in the quest Config format).

Map pins (XTableLevelMapPinNew, 20-array Key0-19) go to LevelMapPin.tsv (teleport target of RpcPlayerSwitchLevelRequest).
Spots (XTableLevelSpotNew, group key 4: [Id, GroupId, Name, Position, Rotation], vectors bare float32) go to LevelSpot.tsv;
XGameplayDormitory places the photo wall / frame wall / frame goods at spots of the lounge groups 5001|5002.
Scene place ids start at 1: the lounge's interaction anchors (ConfigGroup_5001 place ids 1-8: photo wall, frame wall, DIY)
are below 1000 (Level_4003_Present.lua GetSceneObjectUUID(1|2|5|8)).

Run from the AscNet root: python3 Scripts/import_bigworld_scene_objects_4_7.py
"""
import json
import re
import struct
from pathlib import Path

SCRIPT = Path('Scripts/import_bigworld_tables_4_7.py')
_g = {'__name__': 'import_bigworld_tables', '__file__': str(SCRIPT)}
exec(compile(SCRIPT.read_text(encoding='utf-8').split('\ndef main')[0], str(SCRIPT), 'exec'), _g)
dbt, _assets, pgr_bundles = _g['dbt'], _g['_assets'], _g['pgr_bundles']

TABLE = Path('Resources/table/share/statussyncfight/level/sceneconfig/LevelSceneObject.tsv')
PREFIX = 'assets/temp/bytes/share/statussyncfight/level/sceneconfig/skygarden'


def ids(path):
    return {int(line.split('\t')[0]) for line in Path(path).read_text(encoding='utf-8').splitlines()[1:] if line}


BASE_IDS = ids('Resources/table/share/statussyncfight/sceneobject/SceneObjectBase.tsv')
REWARD_IDS = ids('Resources/table/share/bigworld/common/reward/BigWorldReward.tsv')


def read_int(d, o):
    c = d[o]
    if c <= 0x7F:
        return c, o + 1
    fmt = {0xCC: '>B', 0xCD: '>H', 0xCE: '>I', 0xD0: '>b', 0xD1: '>h', 0xD2: '>i'}.get(c)
    if fmt is None:
        return None, o
    return struct.unpack_from(fmt, d, o + 1)[0], o + 1 + struct.calcsize(fmt)


NUMBER_BYTES = {0xCC: 1, 0xD0: 1, 0xCD: 2, 0xD1: 2, 0xCA: 4, 0xCE: 4, 0xD2: 4, 0xCB: 8, 0xCF: 8, 0xD3: 8}


def inside_number(d, o):
    """True when byte `o` may be a payload byte of a preceding multi-byte number (300036 = ce 00 04 94 04 hides 04 94 04);
    only applied to the one-byte place id forms, which those payload bytes can imitate."""
    return any(NUMBER_BYTES.get(d[o - k], 0) >= k for k in range(1, 9))


def scene_objects(d, group_id, base_ids=None):
    base_ids = BASE_IDS if base_ids is None else base_ids
    found = []
    for o in range(len(d) - 40):
        place_id, p = read_int(d, o)  # any integer encoding: fixint/uint8 for place ids below 256
        if place_id is None or place_id < 1 or (d[o] not in (0xCD, 0xCE) and inside_number(d, o)):
            continue
        if 0x90 <= d[p] <= 0x9F:
            p += 1
        elif d[p] == 0xDC:
            p += 3
        else:
            continue
        same, p = read_int(d, p)
        group, p = read_int(d, p)
        base, p = read_int(d, p)
        if same != place_id or group != group_id or base not in base_ids:
            continue
        if all(d[p + 5 * i] == 0xCA for i in range(6)):
            found.append((o, place_id, base))
    return found


def collectable(span):
    hits = []
    for o in range(len(span) - 6):
        if span[o] != 0x95:
            continue
        values, p = [], o + 1
        for _ in range(5):
            if span[p] == 0xC0:
                values.append(0)
                p += 1
                continue
            value, p = read_int(span, p)
            if value is None:
                break
            values.append(value)
        # Reward collectables carry a BigWorldReward id; jumper coins (ECollectableType JumperGold/BigGold/ReduceGold 2000-2002,
        # touch-collected) have RewardId 0 and score through the quest step StatisticModule instead.
        if len(values) == 5 and (values[1] in REWARD_IDS or (values[1] == 0 and values[2] in (2000, 2001, 2002))):
            hits.append(values)
    if len(hits) > 1:
        raise ValueError(f'ambiguous CollectableComponent: {hits}')
    return hits[0] if hits else [0, 0, 0, 0, 0]

def teleporter(span):
    """[IsTeleporter, ActivateMode, DefaultActive] from an 11-array int, bool, int, int, str."""
    hits = []
    for o in range(len(span) - 6):
        if span[o] != 0x9B:
            continue
        mode, p = read_int(span, o + 1)
        if mode is None or span[p] not in (0xC2, 0xC3):
            continue
        default_active = int(span[p] == 0xC3)
        inactive, p = read_int(span, p + 1)
        active, p = read_int(span, p) if inactive is not None else (None, p)
        if active is not None and (0xA0 <= span[p] <= 0xBF or span[p] in (0xD9, 0xC0)):
            hits.append([1, mode, default_active])
    if len(hits) > 1:
        raise ValueError(f'ambiguous TeleporterComponent: {hits}')
    return hits[0] if hits else [0, 0, 0]


def spots(d, group_id):
    """[(SpotId, Name, PosX, PosY, PosZ, RotX, RotY, RotZ)] from the group's Spots map (key 4)."""
    _, o = array_header(d, 0)
    for _ in range(4):
        o = skip(d, o)
    c = d[o]
    if c == 0xC0:
        return []
    count, o = (c & 0x0F, o + 1) if 0x80 <= c <= 0x8F else (int.from_bytes(d[o + 1:o + 3], 'big'), o + 3)
    rows = []
    for _ in range(count):
        key, o = read_int(d, o)
        _, o = array_header(d, o)
        spot_id, o = read_int(d, o)
        group, o = read_int(d, o)
        if spot_id != key or group != group_id:
            raise ValueError(f'ConfigGroup_{group_id}: spot {key} decodes as {spot_id}/{group}')
        end = skip(d, o)
        name = d[o + 1:end].decode('utf-8') if d[o] != 0xC0 else ''
        o = end
        floats = []
        for _ in range(6):
            if d[o] != 0xCA:
                raise ValueError(f'ConfigGroup_{group_id} spot {spot_id}: vector float expected at {o}')
            floats.append(struct.unpack_from('>f', d, o + 1)[0])
            o += 5
        rows.append((spot_id, name.replace('\t', ' '), *floats))
    return rows


def map_pins(d, group_id):
    """XTableLevelMapPinNew rows (dump.cs 859965, 20-array Key0-19) of a group: [(PinId, SceneObjectPlaceId, NpcPlaceId,
    TeleportEnable, TeleportX, TeleportY, TeleportZ, TeleportEulerY, TeleportLevelId)]. Located structurally:
    dc 0014 [Id, GroupId, ints.., Vector3, int, Vector3, bool, Vector3, float, int]; the Id also precedes it as the map key."""
    def opt_int(o):
        if d[o] == 0xC0:
            return 0, o + 1
        return read_int(d, o)

    def floats(o, n):
        if any(d[o + 5 * i] != 0xCA for i in range(n)):
            return None, o
        return [struct.unpack_from('>f', d, o + 5 * i + 1)[0] for i in range(n)], o + 5 * n

    rows = {}
    for o in range(len(d) - 80):
        if d[o:o + 3] != b'\xdc\x00\x14':
            continue
        pin_id, p = read_int(d, o + 3)
        group, p = read_int(d, p) if pin_id is not None else (None, p)
        if group != group_id or pin_id is None or pin_id < 1:
            continue
        vals = []
        for _ in range(5):  # SceneObjectPlaceId, NpcPlaceId, StyleId, ActivityId, MapAreaGroupId
            v, p = opt_int(p)
            vals.append(v)
        world, p = floats(p, 3)
        nav, p = opt_int(p) if world else (None, p)
        offset, p = floats(p, 3) if nav is not None else (None, p)
        if None in vals or offset is None or d[p] not in (0xC2, 0xC3):
            continue
        enable, tele, p = int(d[p] == 0xC3), *floats(p + 1, 3)
        if tele is None:
            continue
        euler, p = floats(p, 1)
        level, p = opt_int(p) if euler else (None, p)
        if level is None or rows.setdefault(pin_id, (pin_id, vals[0], vals[1], enable, *tele, euler[0], level)) is None:
            continue
    return list(rows.values())


def skip(d, o):
    """Offset after the MessagePack value at `o` (structure only; map keys may be arrays, which a decoder rejects)."""
    c = d[o]
    if c <= 0x7F or c >= 0xE0 or c in (0xC0, 0xC2, 0xC3):
        return o + 1
    if 0xA0 <= c <= 0xBF:
        return o + 1 + (c & 0x1F)
    is_map = False
    if 0x80 <= c <= 0x8F:
        n, o, is_map = c & 0x0F, o + 1, True
    elif 0x90 <= c <= 0x9F:
        n, o = c & 0x0F, o + 1
    elif c in (0xDC, 0xDD, 0xDE, 0xDF):
        width = 2 if c in (0xDC, 0xDE) else 4
        n, is_map = int.from_bytes(d[o + 1:o + 1 + width], 'big'), c >= 0xDE
        o += 1 + width
    elif c in (0xC4, 0xC5, 0xC6, 0xD9, 0xDA, 0xDB):
        width = {0xC4: 1, 0xD9: 1, 0xC5: 2, 0xDA: 2, 0xC6: 4, 0xDB: 4}[c]
        return o + 1 + width + int.from_bytes(d[o + 1:o + 1 + width], 'big')
    else:
        size = {0xCA: 4, 0xCB: 8, 0xCC: 1, 0xCD: 2, 0xCE: 4, 0xCF: 8, 0xD0: 1, 0xD1: 2, 0xD2: 4, 0xD3: 8,
                0xD4: 2, 0xD5: 3, 0xD6: 5, 0xD7: 9, 0xD8: 17}.get(c)
        if size is None:
            raise ValueError(f'unsupported MessagePack code {c:#x}')
        return o + 1 + size
    for _ in range(n):
        if is_map:
            o = skip(d, o)
            # Named-key maps write a Vector2/Vector3 value as bare float32s; the next key is never a float.
            if d[o] == 0xCA:
                while d[o] == 0xCA:
                    o += 5
                continue
        o = skip(d, o)
    return o


def tail_start(rec, count=10):
    """Offset of IsOnlyClient: the 10th-last top-level field of an XTableSceneObjectNew record.

    Top-level fields count a Vector2/Vector3 (written as bare consecutive float32 values) as one field, so a run of
    floats is one field. The record must end exactly after `count` fields and IsOnlyClient must be a bool."""
    for s in range(len(rec) - count, 30, -1):
        if rec[s] not in (0xC2, 0xC3):
            continue
        o, fields, prev_float = s, 0, False
        try:
            while o < len(rec) and fields <= count:
                is_float = rec[o] == 0xCA
                o = skip(rec, o)
                fields += 0 if is_float and prev_float else 1
                prev_float = is_float
        except (ValueError, IndexError):
            continue
        if o == len(rec) and fields == count:
            return s
    return None


def record_end(data, start, end, group_id, count=10):
    """End of the record at `start`: `end` if the tail resolves there, else (last object of the SceneObjects map) the
    header of the group's next dictionary (Npcs): an empty map, or a map whose first entry is key -> [key, group_id, ...]."""
    if tail_start(data[start:end], count) is not None:
        return end
    for h in range(start + 40, end):
        c = data[h]
        if c == 0x80:
            entry = None
        elif 0x81 <= c <= 0x8F:
            entry = h + 1
        elif c == 0xDE:
            entry = h + 3
        else:
            continue
        if entry is not None:
            key, p = read_int(data, entry)
            if key is None or p >= len(data) or not (0x90 <= data[p] <= 0x9F or data[p] == 0xDC):
                continue
            p += 3 if data[p] == 0xDC else 1
            same, p = read_int(data, p)
            group = read_int(data, p)[0] if same is not None else None
            if same != key or group != group_id:
                continue
        if tail_start(data[start:h], count) is not None:
            return h
    return None


INTERACTABLE = re.compile(rb'\xdc\x00\x12[\xc2\xc3][\x90-\x9f\xdc]')
# BeScannedComponent (XTableBeScannedComp: AutoHide, ShowNavigationPoint, NavigationPointOffset (3 bare floats),
# NavigationPointStyleId, ShowEffectId, NoLongerHide, RecoverTime; the installed build appends fields to the array).
BE_SCANNED = re.compile(rb'[\x97-\x9f][\xc2\xc3][\xc2\xc3]\xca.{4}\xca.{4}\xca.{4}(?=[\x00-\x7f\xcc-\xd2])', re.S)


def be_scanned(rec):
    """1 when the record carries a BeScannedComponent config (bools, 3 floats, 2 ints, bool, float)."""
    for match in BE_SCANNED.finditer(rec, 30):
        style, p = read_int(rec, match.end())
        effect, p = read_int(rec, p) if style is not None else (None, p)
        if effect is not None and rec[p] in (0xC2, 0xC3) and rec[p + 1] == 0xCA:  # NoLongerHide, RecoverTime
            return 1
    return 0


def layout(data, key_offset, end, group_id):
    """[PosX, PosY, PosZ, RotX, RotY, RotZ, DeferLoading, IsOnlyClient, Interactable, SpaceAudio, BeScanned].

    IsOnlyClient is -1 when the record's trailing fields cannot be delimited: the installed build writes vectors per
    field schema (not recoverable without the 4.8 metadata), so a few records elsewhere stay unresolved. The tail rule
    reproduces every scene object retail replicates for level 4001; the server does not replicate unknown records."""
    _, start = read_int(data, key_offset)
    p = start + (3 if data[start] == 0xDC else 1)
    for _ in range(3):
        _, p = read_int(data, p)
    floats = [struct.unpack_from('>f', data, p + 5 * i + 1)[0] for i in range(6)]
    defer = data[p + 30] == 0xC3
    record = record_end(data, start, end, group_id)
    rec = data[start:record] if record is not None else None
    s = tail_start(rec) if rec is not None else None
    if s is None:
        return [*floats, int(defer), -1, 0, 0, 0]
    after = rec[s + 1]
    space_audio = 0x90 <= after <= 0x9F or after == 0xDC  # SpaceAudio config array follows IsOnlyClient
    return [*floats, int(defer), int(rec[s] == 0xC3),
            int(bool(INTERACTABLE.search(rec, 30))), int(space_audio), be_scanned(rec[:s])]


OPTION_TABLE = Path('Resources/table/share/statussyncfight/level/sceneconfig/LevelInteractOption.tsv')
SCENE_OBJECT, NPC = 2, 1  # EActorType (RpcPlayerInteractRequest TargetType)
OPTION_HEADER = ['ActorType', 'ConfigGroupId', 'PlaceId', 'OptionId', 'Type', 'InteractType', 'Time', 'Consumable', 'RegenTime',
                 'ShowConditionId', 'Config']
SPOT_TABLE = Path('Resources/table/share/statussyncfight/level/sceneconfig/LevelSpot.tsv')
NPC_TABLE = Path('Resources/table/share/statussyncfight/level/sceneconfig/LevelNpc.tsv')
PIN_TABLE = Path('Resources/table/share/statussyncfight/level/sceneconfig/LevelMapPin.tsv')
NPC_BASE_IDS = ids('Resources/table/share/statussyncfight/npc/LevelNpcBase.tsv')
NPC_TAIL = 25  # XTableLevelNpcInstNew: IsOnlyClient and the 24 fields after it (installed layout)


def npc_tail_fields(rec):
    """Offsets of the trailing NPC fields, a run of bare floats (Vector2/3) counted as one field."""
    s = tail_start(rec, NPC_TAIL)
    if s is None:
        return None
    fields, o, prev_float = [], s, False
    while o < len(rec):
        is_float = rec[o] == 0xCA
        end = skip(rec, o)
        if is_float and prev_float:
            fields[-1] = (fields[-1][0], end)
        else:
            fields.append((o, end))
        prev_float, o = is_float, end
    return fields


def npc_record_end(data, start, end, group_id):
    """End of an Npcs-map record; the last NPC ends at the group's next dictionary (Routes), anchored like scene objects."""
    return end if tail_start(data[start:end], NPC_TAIL) is not None else record_end(data, start, end, group_id, NPC_TAIL)


def npc_layout(data, key_offset, end, group_id):
    """[PosX..RotZ, DeferLoading, ScriptId, DefaultAction, CanInteract, IsOnlyClient, DisableGravity, ActorSignificance].

    Head fields follow XTableLevelNpcInstNew order (dump.cs): Position, EulerAngles (bare floats), DeferLoading, ScriptId,
    DefaultAction, InteractAction, ShowName, DialogDramaName, IgnoreCollisionSceneObjectPlaceId, MapPinId, CanInteract.
    Tail fields (validated against the retail level 4001 replication): [0] IsOnlyClient, [17] DisableGravity,
    [18] ActorSignificance. Unresolved tails are -1."""
    _, start = read_int(data, key_offset)
    p = start + (3 if data[start] == 0xDC else 1)
    for _ in range(3):
        _, p = read_int(data, p)
    floats = [struct.unpack_from('>f', data, p + 5 * i + 1)[0] for i in range(6)]
    p += 30
    head = []
    for _ in range(9):
        c = data[p]
        if c in (0xC2, 0xC3):
            head.append(int(c == 0xC3))
        elif c == 0xC0:
            head.append('')
        elif 0xA0 <= c <= 0xBF or c in (0xD9, 0xDA):
            head.append(data[p + (1 if c <= 0xBF else 2 if c == 0xD9 else 3):skip(data, p)].decode('utf-8'))
        else:
            head.append(read_int(data, p)[0])
        p = skip(data, p)
    defer, script_id, default_action, can_interact = head[0], head[1], head[2], head[8]
    record = npc_record_end(data, start, end, group_id)
    fields = npc_tail_fields(data[start:record]) if record is not None else None
    if fields is None:
        return [*floats, defer, script_id, default_action, can_interact, -1, 0, 0]
    rec = data[start:record]
    return [*floats, defer, script_id, default_action, can_interact,
            int(rec[fields[0][0]] == 0xC3), int(rec[fields[17][0]] == 0xC3), read_int(rec, fields[18][0])[0] or 0]


def array_header(d, o):
    c = d[o]
    if 0x90 <= c <= 0x9F:
        return c & 0x0F, o + 1
    if c in (0xDC, 0xDD):
        width = 2 if c == 0xDC else 4
        return int.from_bytes(d[o + 1:o + 1 + width], 'big'), o + 1 + width
    raise ValueError(f'array expected at {o}, got {c:#x}')


def interact_options(d, o):
    """Decode a List<XTableInteractOption> at `o` (dump.cs: Id, Type, InteractType, Time, Consumable, RegenTime,
    ShowConditionId, PlayerInteractAnimation, IsShowLockInteract, LevelConditionGroup, CompleteActionList; the installed
    build appends 4 more fields, skipped). Actions and conditions use the quest reader (union name + map, bare vectors).
    Returns [(option fields..., Config dict)]."""
    count, o = array_header(d, o)
    out = []
    for _ in range(count):
        fields, o = array_header(d, o)
        reader = _g['QuestReader'](d)
        reader.offset = o
        values = []
        for i in range(fields):
            if i < 11:
                values.append(reader.unpack())
            else:
                reader.offset = skip(d, reader.offset)
        o = reader.offset
        option_id, type_, interact_type, time, consumable, regen, show_cond, _, _, condition, actions = values
        config = {k: v for k, v in (('LevelConditionGroup', condition), ('CompleteActionList', actions)) if v}
        out.append((option_id, type_, interact_type, round(time, 6), int(consumable), round(regen, 6), show_cond, config))
    return out, o


def scene_object_options(span):
    """Options of the record's InteractableComponent (18-array: ShouldReactToPlayerInteract, LauncherSpots, DefaultOptionId,
    NarrativeId, Options ...); [] when the object has none."""
    match = INTERACTABLE.search(span, 30)
    if match is None:
        return []
    o = match.start() + 3 + 1
    spots, o = array_header(span, o)
    o += 15 * spots  # LauncherSpots: bare Vector3 floats
    for _ in range(2):
        _, o = read_int(span, o)
    return interact_options(span, o)[0]


def npc_options(data, key_offset):
    """InteractOptions (key 18) of an XTableLevelNpcInstNew: after the 9 head fields come DefaultInteractOptionId,
    FaceToInteractLauncher, ShouldReactToPlayerInteract, InteractionLauncherSpot (3 bare floats)."""
    _, start = read_int(data, key_offset)
    p = start + (3 if data[start] == 0xDC else 1)
    for _ in range(3):
        _, p = read_int(data, p)
    p += 30
    for _ in range(9):
        p = skip(data, p)
    p = skip(data, p)  # DefaultInteractOptionId
    p = skip(data, p)  # FaceToInteractLauncher
    p = skip(data, p)  # ShouldReactToPlayerInteract
    p += 15            # InteractionLauncherSpot
    return interact_options(data, p)[0]


def main():
    # Group ids are unique only per sector bundle (sceneconfig/<Level.SectorName lower>.ab): ConfigGroup_1001 exists in
    # seven bundles, ConfigGroup_5001 in skygarden (District B) and skygarden_sushe (Commandant's Lounge). Rows carry Sector.
    groups = {}
    for bundle in (k for k in pgr_bundles.Resolver().names(PREFIX, scope='resource') if k.count('/') == 7):
        sector = bundle.rsplit('/', 1)[1].removesuffix('.ab')
        path = pgr_bundles.resolve(bundle, scope='resource')
        _, _, payload = _assets.decode_bundle_payload(path.read_bytes())
        for item in dbt.iter_text_assets(_assets, payload):
            name = str(item['name'])
            if name.startswith('ConfigGroup_'):
                key = (sector, int(name.split('_')[1]))
                if key in groups:
                    raise ValueError(f'{bundle}: duplicate {name}')
                groups[key] = bytes(item['data'])
    rows = []
    for (sector, group_id), data in sorted(groups.items()):
        objects = scene_objects(data, group_id)
        ends = [o for o, _, _ in objects[1:]] + [len(data)]
        group_rows = [(sector, group_id, place_id, base, *collectable(data[o:end]), *teleporter(data[o:end]), *layout(data, o, end, group_id))
                      for (o, place_id, base), end in zip(objects, ends)]
        # Native CollectableSceneObjectCount = key 3 of the group array: [Id, Name, IdRangeSeqNo, Count, ...].
        _, p = read_int(data, 1)
        p = p + 1 if data[p] == 0xC0 else p + 1 + (data[p] & 0x1F if 0xA0 <= data[p] <= 0xBF else data[p + 1] + 1)
        _, p = read_int(data, p)
        expected, _ = read_int(data, p)
        actual = sum(1 for row in group_rows if row[5])
        if expected != actual:
            raise ValueError(f'{sector} ConfigGroup_{group_id}: {actual} collectables, native count {expected}')
        rows.extend(group_rows)
    spot_rows = [(sector, group_id, *spot) for (sector, group_id), data in sorted(groups.items()) for spot in spots(data, group_id)]
    SPOT_TABLE.write_text('\n'.join(['\t'.join(['Sector', 'ConfigGroupId', 'SpotId', 'Name', 'PosX', 'PosY', 'PosZ', 'RotX', 'RotY', 'RotZ'])]
                                    + ['\t'.join(map(str, row)) for row in spot_rows]) + '\n', encoding='utf-8')
    print(f'{SPOT_TABLE}: {len(spot_rows)} level spots')
    pin_rows = [(sector, group_id, *pin) for (sector, group_id), data in sorted(groups.items()) for pin in map_pins(data, group_id)]
    PIN_TABLE.write_text('\n'.join(['\t'.join(['Sector', 'ConfigGroupId', 'PinId', 'SceneObjectPlaceId', 'NpcPlaceId', 'TeleportEnable',
                                               'TeleportX', 'TeleportY', 'TeleportZ', 'TeleportEulerY', 'TeleportLevelId'])]
                                    + ['\t'.join(map(str, row)) for row in pin_rows]) + '\n', encoding='utf-8')
    print(f'{PIN_TABLE}: {len(pin_rows)} level map pins, {sum(1 for r in pin_rows if r[5])} teleport-enabled')
    npc_rows = []
    for (sector, group_id), data in sorted(groups.items()):
        npcs = scene_objects(data, group_id, NPC_BASE_IDS)
        ends = [o for o, _, _ in npcs[1:]] + [len(data)]
        npc_rows.extend((sector, group_id, place_id, base, *npc_layout(data, o, end, group_id)) for (o, place_id, base), end in zip(npcs, ends))
    option_rows = []
    for (sector, group_id), data in sorted(groups.items()):
        objects = scene_objects(data, group_id)
        ends = [o for o, _, _ in objects[1:]] + [len(data)]
        for (o, place_id, _), end in zip(objects, ends):
            option_rows.extend((sector, SCENE_OBJECT, group_id, place_id, *opt[:7], json.dumps(opt[7], separators=(',', ':'), ensure_ascii=False) if opt[7] else '')
                               for opt in scene_object_options(data[o:end]))
        npcs = scene_objects(data, group_id, NPC_BASE_IDS)
        for (o, place_id, _) in npcs:
            option_rows.extend((sector, NPC, group_id, place_id, *opt[:7], json.dumps(opt[7], separators=(',', ':'), ensure_ascii=False) if opt[7] else '')
                               for opt in npc_options(data, o))
    OPTION_TABLE.write_text('\n'.join(['\t'.join(['Sector', *OPTION_HEADER])] + ['\t'.join(map(str, row)) for row in option_rows]) + '\n', encoding='utf-8')
    print(f'{OPTION_TABLE}: {len(option_rows)} interact options, {sum(1 for r in option_rows if r[11])} with condition/action config')
    npc_header = ['Sector', 'ConfigGroupId', 'PlaceId', 'BaseId', 'PosX', 'PosY', 'PosZ', 'RotX', 'RotY', 'RotZ', 'DeferLoading',
                  'ScriptId', 'DefaultAction', 'CanInteract', 'IsOnlyClient', 'DisableGravity', 'ActorSignificance']
    NPC_TABLE.write_text('\n'.join(['\t'.join(npc_header)] + ['\t'.join(map(str, row)) for row in npc_rows]) + '\n', encoding='utf-8')
    print(f'{NPC_TABLE}: {len(npc_rows)} level NPCs, {sum(1 for r in npc_rows if r[14] == -1)} unresolved IsOnlyClient')
    header = ['Sector', 'ConfigGroupId', 'PlaceId', 'BaseId', 'CollectWay', 'CollectRewardId', 'CollectableType', 'CourseGroupId',
              'CollectCueId', 'IsTeleporter', 'TeleporterActivateMode', 'TeleporterDefaultActive',
              'PosX', 'PosY', 'PosZ', 'RotX', 'RotY', 'RotZ', 'DeferLoading', 'IsOnlyClient', 'Interactable', 'SpaceAudio', 'BeScanned']
    TABLE.parent.mkdir(parents=True, exist_ok=True)
    TABLE.write_text('\n'.join(['\t'.join(header)] + ['\t'.join(map(str, row)) for row in rows]) + '\n', encoding='utf-8')
    print(f'{TABLE}: {len(rows)} scene objects, {sum(1 for r in rows if r[5])} collectables, '
          f'{sum(1 for r in rows if r[9])} teleporters, {sum(1 for r in rows if r[19] == -1)} unresolved IsOnlyClient, '
          f'{len(groups)} groups')
    # FightConfig (share/statussyncfight.ab FightConfig.tab): RepFight.FloatConfigDict source.
    _, config_rows, source = _g['decode']('share/statussyncfight/FightConfig.json')
    config = Path('Resources/table/share/statussyncfight/FightConfig.tsv')
    config.write_text('\n'.join(['Key\tType\tValue'] + ['\t'.join(str(_g['_text'](row.get(c, ''))) for c in ('Key', 'Type', 'Value'))
                                                        for row in config_rows]) + '\n', encoding='utf-8')
    print(f'{config}: {len(config_rows)} rows ({source})')


if __name__ == '__main__':
    main()
