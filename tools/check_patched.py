"""Provjera fontova koje je napravio FontShelf --patch-hr (pokreće se u GitHub Actions)."""
import glob, io, os, subprocess, sys
from fontTools.ttLib import TTFont
from fontTools.pens.boundsPen import BoundsPen
from PIL import Image, ImageDraw, ImageFont

out_dir = sys.argv[1]
want = [0x10D, 0x107, 0x161, 0x17E, 0x111, 0x10C, 0x106, 0x160, 0x17D, 0x110]
ok = True
sheets = []
for path in sorted(glob.glob(os.path.join(out_dir, "*-HR.ttf")) + glob.glob(os.path.join(out_dir, "*-HR.otf"))):
    name = os.path.basename(path)
    try:
        f = TTFont(path)
        cmap = f.getBestCmap()
        missing = [chr(c) for c in want if c not in cmap]
        gs = f.getGlyphSet()
        for cp in want:
            if cp in cmap:
                bp = BoundsPen(gs)
                gs[cmap[cp]].draw(bp)
                assert bp.bounds is not None, f"prazan glif {chr(cp)}"
        assert f["maxp"].numGlyphs == len(f.getGlyphOrder())
        fam = f["name"].getDebugName(1)
        buf = io.BytesIO(); f.save(buf)
        # usporedba s pravim glifovima izvornog fonta (ako postoji)
        base = name.replace("-HR", "")
        orig = os.path.join(os.path.dirname(out_dir.rstrip("/\\")), "orig", base)
        if os.path.exists(orig):
            o = TTFont(orig)
            ocm, ogs = o.getBestCmap(), o.getGlyphSet()
            for cp in want:
                if cp in ocm and cp in cmap:
                    b1 = BoundsPen(ogs); ogs[ocm[cp]].draw(b1)
                    b2 = BoundsPen(gs); gs[cmap[cp]].draw(b2)
                    d = [round(b - a) for a, b in zip(b1.bounds, b2.bounds)]
                    print(f"  {chr(cp)} izvornik={tuple(round(v) for v in b1.bounds)} novo={tuple(round(v) for v in b2.bounds)} razlika={d}")
        print(f"{name}: obitelj='{fam}', nedostaje='{''.join(missing)}', glifova={f['maxp'].numGlyphs}")
        if missing:
            ok = False
    except Exception as e:
        print(f"{name}: GREŠKA fontTools: {e}")
        ok = False
        continue
    try:
        import ots
        r = ots.sanitize(path, os.devnull, capture_output=True)
        if r.returncode != 0:
            print(f"{name}: OTS odbio font:\n{r.stdout}\n{r.stderr}")
            ok = False
        else:
            print(f"{name}: OTS u redu")
    except ImportError:
        print("OTS nije dostupan, preskačem")

    # slika uzorka
    try:
        img = Image.new("RGB", (1400, 260), "white")
        d = ImageDraw.Draw(img)
        big = ImageFont.truetype(path, 96)
        small = ImageFont.truetype(path, 34)
        d.text((24, 20), "čćšžđ ČĆŠŽĐ", font=big, fill="black")
        d.text((24, 170), "Gojazni đačić s biciklom drži hmelj. ŠĐČĆŽ šđčćž", font=small, fill="black")
        p = os.path.splitext(path)[0] + ".png"
        img.save(p)
        sheets.append(p)
    except Exception as e:
        print(f"{name}: slika nije uspjela: {e}")

if sheets:
    ims = [Image.open(p) for p in sheets]
    sheet = Image.new("RGB", (1400, 260 * len(ims)), "white")
    for i, im in enumerate(ims):
        sheet.paste(im, (0, 260 * i))
    sheet.save(os.path.join(out_dir, "uzorci.png"))

print("SVE U REDU" if ok else "PROVJERA NIJE PROŠLA")
sys.exit(0 if ok else 1)
