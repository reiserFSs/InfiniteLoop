#!/usr/bin/env python3
"""Rebuild the launcher's background.mp4 / background.wav / background.bmp from an installed PGR client (read-only).

  extract_launcher_background.py [--install DIR] [--out DIR] all        spine + audio + render
  extract_launcher_background.py ... spine    login Spine layers (UiLoginSpineAnimPath prefab) -> OUT/spine/ + report
  extract_launcher_background.py ... audio    UiLogin XPlayMusic BGM cue -> OUT/background.wav (smpl loop, 48 kHz PCM16 stereo)
  extract_launcher_background.py ... render   spine-webgl frames in headless Chrome -> OUT/background.mp4 (H.264 1920x1080
                                              30 fps, macOS AVAssetWriter) + OUT/background.bmp (frame 0, 24-bit)

Sources, as the client plays them: ClientConfig UiLoginSpineAnimPath (Assets/Product/Ui/Spine/SpineLogin.prefab) holds the
login Spine rigs; the UiLogin prefab's XPlayMusic component holds the BGM cue ids, the unbounded entry is the everyday one;
share/audio Cue + CueSheet map a cue id to External/Criware/<sheet>.acb/.awb. The document-scope bundle wins over resource.
The still/video loop is the LCM of the layers' `idle` durations (an `idle` shorter than the cut would jump at the seam).
Needs: UnityPy (Scripts' shared vendor dir), vgmstream-cli, node + npm, swift, Pillow. Shared render deps are npm-installed
into OUT/render on first use.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import shutil
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import pgr_bundles  # noqa: E402  (also puts the shared UnityPy vendor dir on sys.path)

PREFAB = "assets/product/ui/spine/spinelogin.prefab"
LOGIN_PREFAB = "assets/product/ui/prefab/uilogin.prefab"
TABLES = HERE.parent.parent / "PGR_Data/en-4.9/bytes/share/audio"  # Cue.json / CueSheet.json of the client's tables
FPS = 30
RENDER_FILES = HERE / "launcher_background"


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def duration(value: Any) -> float:
    """Longest keyframe time anywhere in a Spine animation (= its authored length)."""
    if isinstance(value, dict):
        return max([float(value["time"]) if isinstance(value.get("time"), (int, float)) else 0.0, *map(duration, value.values())])
    if isinstance(value, list):
        return max([0.0, *map(duration, value)])
    return 0.0


def load_bundle(resolver: pgr_bundles.Resolver, asset: str):
    import UnityPy
    from io import BytesIO
    UnityPy.set_assetbundle_decrypt_key(pgr_bundles.KEY.decode())  # protected UnityFS; keeps .resS texture streams reachable
    found = resolver.find_asset(asset)  # document scope first, then resource
    if not found:
        raise SystemExit(f"no bundle holds {asset}")
    return found, UnityPy.load(BytesIO(pgr_bundles.strip_prefix(found[0].read_bytes())))


def spine(resolver: pgr_bundles.Resolver, out: Path) -> dict:
    (path, logical), env = load_bundle(resolver, PREFAB)
    objects = {(o.assets_file.name, o.path_id): o for o in env.objects}
    wanted = {"GameObject", "RectTransform", "Transform", "MonoBehaviour", "MonoScript", "MeshRenderer"}
    trees = {k: o.read_typetree() for k, o in objects.items() if o.type.name in wanted}

    def deref(owner, pointer):
        if int(pointer["m_PathID"]) == 0:
            return None
        if int(pointer["m_FileID"]) != 0:
            raise SystemExit("prefab references an external bundle; load it too")
        return owner[0], int(pointer["m_PathID"])

    scripts = {k: t["m_ClassName"] for k, t in trees.items() if objects[k].type.name == "MonoScript"}

    def script_of(key):
        t = trees.get(key)
        return scripts.get(deref(key, t["m_Script"])) if t and "m_Script" in t else None

    transforms = {k: t for k, t in trees.items() if objects[k].type.name in ("RectTransform", "Transform")}
    root_file = env.container[PREFAB].assetsfile.name
    root = next(k for k, t in transforms.items() if k[0] == root_file and int(t["m_Father"]["m_PathID"]) == 0
                and trees[deref(k, t["m_GameObject"])]["m_Name"].lower() == "spinelogin")

    hierarchy, layers_src, queue = [], [], [(root, 0, True)]
    while queue:
        key, depth, parent_active = queue.pop(0)
        t = transforms[key]
        go_key = deref(key, t["m_GameObject"])
        go = trees[go_key]
        active = parent_active and bool(go["m_IsActive"])
        comps = [deref(go_key, c["component"]) for c in go["m_Component"]]
        names = [script_of(c) or objects[c].type.name for c in comps]
        hierarchy.append({"depth": depth, "name": go["m_Name"], "active": active, "components": names,
                          "local_scale": t["m_LocalScale"], "local_position": t["m_LocalPosition"]})
        if active and "SkeletonAnimation" in names:
            layers_src.append((go_key, t, comps))
        queue.extend((deref(key, c), depth + 1, active) for c in t["m_Children"])

    def text(key):
        a = objects[key].read()
        return a.m_Name, a.m_Script.encode("utf-8", "surrogateescape") if isinstance(a.m_Script, str) else bytes(a.m_Script)

    textures = {o.read().m_Name: o for o in env.objects if o.type.name == "Texture2D"}
    spine_dir = out / "spine"
    layers = []
    for go_key, t, comps in layers_src:
        go = trees[go_key]
        anim_key = next(c for c in comps if script_of(c) == "SkeletonAnimation")
        renderer_key = next((c for c in comps if script_of(c) == "SkeletonRenderer"), anim_key)
        mesh_key = next((c for c in comps if c in objects and objects[c].type.name == "MeshRenderer"), None)
        data_key = deref(renderer_key, trees[renderer_key]["skeletonDataAsset"])
        data = trees[data_key]
        json_name, raw = text(deref(data_key, data["skeletonJSON"]))
        skeleton = json.loads(raw)
        stem = Path(json_name).stem
        target = spine_dir / stem
        target.mkdir(parents=True, exist_ok=True)
        (target / f"{stem}.json").write_bytes(raw)
        atlas_key = deref(data_key, data["atlasAssets"][0])
        _, atlas = text(deref(atlas_key, trees[atlas_key]["atlasFile"]))
        (target / f"{stem}.atlas").write_bytes(atlas)
        pages = [l.strip() for l in atlas.decode().splitlines() if l and not l[0].isspace() and l.lower().endswith((".png", ".jpg"))]
        for page in pages:
            textures[Path(page).stem].read().image.save(target / page)
        scale = float(data["scale"])
        local = t["m_LocalScale"]
        mesh = trees[mesh_key] if mesh_key else {}
        slots = skeleton.get("slots", [])
        layers.append({
            "game_object": go["m_Name"], "name": stem,
            "sorting_order": mesh.get("m_SortingOrder") or 0,
            "initial_animation": trees[anim_key].get("animationName"),
            "spine_version": skeleton["skeleton"].get("spine"),
            "composite_scale": {"x": scale * float(local["x"]), "y": scale * float(local["y"])},
            "animations_seconds": {n: duration(a) for n, a in skeleton.get("animations", {}).items()},
            "slot_blend_modes": dict(Counter(s.get("blend", "normal") for s in slots)),
            "files": {str(p.relative_to(spine_dir)): sha256(p) for p in sorted(target.iterdir())},
        })
    layers.sort(key=lambda l: l["sorting_order"])
    report = {"prefab_bundle": str(path), "logical": logical, "prefab_bundle_sha256": sha256(path),
              "unity_version": getattr(next(iter(env.files.values())), "version_engine", None),
              "hierarchy": hierarchy, "layers_back_to_front": layers}
    (spine_dir / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n")
    for l in layers:
        print(l["sorting_order"], l["game_object"], l["name"], l["animations_seconds"], l["slot_blend_modes"])
    return report


def audio(resolver: pgr_bundles.Resolver, out: Path) -> dict:
    (path, logical), env = load_bundle(resolver, LOGIN_PREFAB)
    music = next(t["Music"] for o in env.objects if o.type.name == "MonoBehaviour"
                 for t in [o.read_typetree()] if "Music" in t and "IsTimeCheck" in t)
    cue_id = next(m["CueId"] for m in music if not m["TimedateStart"] and not m["TimedateEnd"])
    cue = next(c for c in json.loads((TABLES / "Cue.json").read_text()) if c["Id"] == cue_id)
    sheet = next(s for s in json.loads((TABLES / "CueSheet.json").read_text()) if s["Id"] == cue["CueSheetId"])
    src = {}
    for ext, name in (("acb", sheet["CueSheetName"]), ("awb", sheet["CueAwb"])):
        entry = resolver.entry(name)
        src[ext] = next(p for s in pgr_bundles.SCOPES if (p := pgr_bundles.matrix_dir(resolver.install, s) / entry[0]).is_file())
    with tempfile.TemporaryDirectory() as tmp:
        stem = Path(tmp) / cue["CueName"]
        for ext, p in src.items():
            shutil.copyfile(p, f"{stem}.{ext}")
        wav = out / "background.wav"
        subprocess.run(["vgmstream-cli", "-L", "-o", str(wav), f"{stem}.awb"], check=True, capture_output=True)
        info = json.loads(subprocess.run(["vgmstream-cli", "-I", f"{stem}.awb"], check=True, capture_output=True, text=True).stdout)
    report = {"uilogin_bundle": str(path), "uilogin_logical": logical, "music": music, "cue": cue, "sheet": sheet,
              "acb": str(src["acb"]), "awb": str(src["awb"]), "awb_sha256": sha256(src["awb"]),
              "loop": info.get("loopingInfo"), "samples": info["numberOfSamples"], "wav_sha256": sha256(wav)}
    (out / "audio-report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n")
    print(f"cue {cue_id} {cue['CueName']} -> {wav} loop {report['loop']}")
    return report


def lcm_frames(report: dict) -> int:
    frames = [round(l["animations_seconds"]["idle"] * FPS) for l in report["layers_back_to_front"]]
    return math.lcm(*frames)


def render(out: Path) -> None:
    report = json.loads((out / "spine/report.json").read_text())
    frames = lcm_frames(report)
    work = out / "render"
    work.mkdir(exist_ok=True)
    for f in ("index.html", "render.mjs", "encode.swift", "probe.swift", "package.json"):
        shutil.copyfile(RENDER_FILES / f, work / f)
    if not (work / "node_modules").is_dir():
        subprocess.run(["npm", "install", "--no-audit", "--no-fund"], cwd=work, check=True)
    shutil.rmtree(work / "frames", ignore_errors=True)
    subprocess.run(["node", "render.mjs", str(out / "spine"), "frames", f"0-{frames - 1}"], cwd=work, check=True)
    subprocess.run(["swift", "encode.swift", "frames", str(out / "background.mp4")], cwd=work, check=True)
    from PIL import Image
    Image.open(work / "frames/frame-000.png").convert("RGB").save(out / "background.bmp")
    print(f"rendered {frames} frames ({frames / FPS:g} s)")


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("command", choices=("spine", "audio", "render", "all"))
    p.add_argument("--install", type=Path, help="game dir (default: native-starter manifest source_install)")
    p.add_argument("--out", type=Path, default=Path("/tmp/launcher-background"))
    args = p.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    resolver = pgr_bundles.Resolver(args.install)
    if args.command in ("spine", "all"):
        spine(resolver, args.out)
    if args.command in ("audio", "all"):
        audio(resolver, args.out)
    if args.command in ("render", "all"):
        render(args.out)


if __name__ == "__main__":
    main()
