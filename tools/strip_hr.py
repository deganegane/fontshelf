"""Napravi testnu kopiju fonta kojoj su iz cmap-a uklonjeni hrvatski znakovi (i po želji kvačica ˇ)."""
import sys
from fontTools.ttLib import TTFont

src, dst = sys.argv[1], sys.argv[2]
extra = sys.argv[3:]  # npr. "nocaron"
cps = [0x10D, 0x107, 0x161, 0x17E, 0x111, 0x10C, 0x106, 0x160, 0x17D, 0x110]
if "nocaron" in extra:
    cps += [0x02C7, 0x030C]
if "noeth" in extra:
    cps += [0x00D0]
f = TTFont(src)
for t in f["cmap"].tables:
    if "bare" in extra:
        # ostavi samo slova A-Z, a-z i razmak (font bez interpunkcije i akcenata)
        for cp in list(t.cmap):
            if not (cp == 32 or 65 <= cp <= 90 or 97 <= cp <= 122):
                del t.cmap[cp]
        continue
    for cp in cps:
        t.cmap.pop(cp, None)
f.save(dst)
