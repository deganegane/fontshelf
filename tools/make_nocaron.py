"""Napravi testnu kopiju fonta bez kvačice (ˇ), da se provjeri zrcaljenje ˆ."""
import sys
from fontTools.ttLib import TTFont

src, dst = sys.argv[1], sys.argv[2]
f = TTFont(src)
for t in f["cmap"].tables:
    for cp in (0x02C7, 0x030C):
        t.cmap.pop(cp, None)
f.save(dst)
