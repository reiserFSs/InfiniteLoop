#!/usr/bin/env python3
"""Rebuilds the bundled Noto Sans subsets (SIL OFL, see OFL.txt) for the launcher UI.

Usage: build_fonts.py <dir holding Noto{Sans,SansSC,SansTC,SansJP,SansKR}-VF.ttf>
  (google/fonts: ofl/notosans*/NotoSans*[wght].ttf, saved as <Name>-VF.ttf)

Each face is a static instance of the variable font, subset to Basic Latin + the characters the
launcher's .po translations (plus the five edition names) actually use. The instanced faces keep a
single family name per script ("Noto Sans", "Noto Sans SC"/"TC"/"JP"/"KR") and differ only by
weight: Slint/fontique matches family + OS/2 weight, so the default "Regular"/"Bold" instancer names
(which collapse every weight to one family+style) must be rewritten. Re-run after changing a .po.
"""
import re, sys, pathlib
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
from fontTools import subset

src = pathlib.Path(sys.argv[1])
here = pathlib.Path(__file__).parent
lang = here.parent / "lang"
NAMES = "English简体中文繁體中文한국어日本語"  # edition tiles show every native name in its own face
LATIN = (list(range(0x20, 0x7F)) + list(range(0xA0, 0x180)) +
         [0x2010, 0x2011, 0x2012, 0x2013, 0x2014, 0x2015, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026,
          0x2030, 0x20AC, 0x2190, 0x2191, 0x2192, 0x2193, 0x2212, 0x00B7])
CJK_PUNCT = list(range(0x3000, 0x3040)) + list(range(0xFF01, 0xFF5F))
FACES = [  # (file, family, po language dir or None, weights)
    ("NotoSans", "Noto Sans", None, [(400, "Regular"), (500, "Medium"), (600, "SemiBold")]),
    ("NotoSansSC", "Noto Sans SC", "zh-CN", [(400, "Regular"), (600, "SemiBold")]),
    ("NotoSansTC", "Noto Sans TC", "zh-TW", [(400, "Regular"), (600, "SemiBold")]),
    ("NotoSansJP", "Noto Sans JP", "ja", [(400, "Regular"), (600, "SemiBold")]),
    ("NotoSansKR", "Noto Sans KR", "ko", [(400, "Regular"), (600, "SemiBold")]),
]

def po_chars(code):
    chars = set()
    for po in lang.glob(f"{code}/LC_MESSAGES/*.po"):
        for m in re.finditer(r'^(?:msgstr(?:\[\d\])?)\s+"(.*)"$|^"(.*)"$', po.read_text("utf-8"), re.M):
            chars.update(m.group(1) or m.group(2) or "")
    return chars

for stem, family, code, weights in FACES:
    for weight, style in weights:
        vf = TTFont(src / f"{stem}-VF.ttf")
        axes = {"wght": weight}
        if "wdth" in [a.axisTag for a in vf["fvar"].axes]:
            axes["wdth"] = 100
        font = instantiateVariableFont(vf, axes, inplace=False)
        wanted = set(LATIN) | {ord(c) for c in NAMES}
        if code:
            wanted |= set(CJK_PUNCT) | {ord(c) for c in po_chars(code) if ord(c) > 0x7F}
        opts = subset.Options()
        opts.layout_features = ["kern", "locl", "ccmp", "mark", "mkmk", "liga", "calt"]
        opts.hinting = False
        opts.notdef_outline = True
        opts.name_IDs = [0, 1, 2, 3, 4, 5, 6, 13, 14, 16, 17]
        sub = subset.Subsetter(opts)
        sub.populate(unicodes=wanted)
        sub.subset(font)
        name = font["name"]
        ps = f"{stem}-{style}"
        for rec in list(name.names):
            if rec.nameID in (1, 2, 3, 4, 6, 16, 17, 21, 22, 25):
                name.removeNames(nameID=rec.nameID)
        for nid, text in {1: family, 2: style, 3: f"{ps};subset", 4: f"{family} {style}", 6: ps, 16: family, 17: style}.items():
            name.setName(text, nid, 3, 1, 0x409)
            name.setName(text, nid, 1, 0, 0)
        os2 = font["OS/2"]
        os2.usWeightClass = weight
        os2.fsSelection = (os2.fsSelection & ~0x21 & ~0x40) | (0x40 if weight == 400 else 0)
        font["head"].macStyle = 0
        for t in ("STAT", "MVAR", "fvar", "avar", "gvar", "HVAR"):
            if t in font: del font[t]
        out = here / f"{ps}.ttf"
        font.save(out)
        print(out.name, out.stat().st_size, len(font.getBestCmap()))
