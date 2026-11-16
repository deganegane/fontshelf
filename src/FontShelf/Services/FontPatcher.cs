using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FontShelf.Services
{
    /// <summary>Postavke za dodavanje hrvatskih znakova (u jedinicama fonta, osim omjera).</summary>
    public class PatchOptions
    {
        public double BarShiftY { get; set; }        // pomak crtice na đ/Đ gore-dolje (jedinice)
        public double BarShiftX { get; set; }        // pomak crtice lijevo-desno (jedinice)
        public double BarWidthScale { get; set; } = 1.0;
        public double BarThicknessScale { get; set; } = 1.0;
        public double AccentGap { get; set; }        // dodatni razmak kvačica/akcenta od slova (jedinice)
        public string Suffix { get; set; } = "HR";
        public bool Rename { get; set; } = true;
    }

    public class PatchResult
    {
        public byte[] Data;
        public List<string> Added = new List<string>();
        public List<string> Skipped = new List<string>();
        public string NewFamily;
        public int UnitsPerEm;
    }

    public class FontPatchException : Exception
    {
        public FontPatchException(string msg) : base(msg) { }
    }

    /// <summary>
    /// Dodaje č ć š ž đ Č Ć Š Ž Đ u TrueType font slaganjem postojećih dijelova (složeni glifovi):
    /// slovo + kvačica/akcent, odnosno slovo + crtica. Radi samo s TTF (glyf) fontovima.
    /// </summary>
    public static class FontPatcher
    {
        private enum Kind { Caron, Acute, Bar }

        private static readonly (int cp, char baseCh, Kind kind, string name)[] Targets =
        {
            (0x010D, 'c', Kind.Caron, "ccaron"),
            (0x0107, 'c', Kind.Acute, "cacute"),
            (0x0161, 's', Kind.Caron, "scaron"),
            (0x017E, 'z', Kind.Caron, "zcaron"),
            (0x0111, 'd', Kind.Bar, "dcroat"),
            (0x010C, 'C', Kind.Caron, "Ccaron"),
            (0x0106, 'C', Kind.Acute, "Cacute"),
            (0x0160, 'S', Kind.Caron, "Scaron"),
            (0x017D, 'Z', Kind.Caron, "Zcaron"),
            (0x0110, 'D', Kind.Bar, "Dcroat"),
        };

        // ------------------------------------------------------------------ bajtovi
        private static ushort U16(byte[] d, int o) => (ushort)(d[o] << 8 | d[o + 1]);
        private static short I16(byte[] d, int o) => (short)U16(d, o);
        private static uint U32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
        private static int I32(byte[] d, int o) => (int)U32(d, o);
        private static void W16(byte[] d, int o, int v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
        private static void W32(byte[] d, int o, uint v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }

        private class Buf
        {
            private readonly List<byte> _b = new List<byte>();
            public int Length => _b.Count;
            public void U8(int v) => _b.Add((byte)v);
            public void U16(int v) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); }
            public void U32(uint v) { _b.Add((byte)(v >> 24)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 8)); _b.Add((byte)v); }
            public void Bytes(byte[] x) => _b.AddRange(x);
            public void Pad4() { while (_b.Count % 4 != 0) _b.Add(0); }
            public byte[] ToArray() => _b.ToArray();
        }

        private static string Tag(byte[] d, int o) => Encoding.ASCII.GetString(d, o, 4);

        // ------------------------------------------------------------------ čitanje
        private static Dictionary<string, byte[]> ReadTables(byte[] d)
        {
            if (d.Length < 12) throw new FontPatchException("Datoteka nije font.");
            uint ver = U32(d, 0);
            if (ver == 0x74746366) throw new FontPatchException("Kolekcije fontova (.ttc) još nisu podržane.");
            if (ver == 0x4F54544F) throw new FontPatchException("Ovaj font je OpenType s CFF obrisima — zasad su podržani samo TrueType (TTF) fontovi.");
            if (ver != 0x00010000 && ver != 0x74727565) throw new FontPatchException("Nepoznat format fonta.");
            int n = U16(d, 4);
            var t = new Dictionary<string, byte[]>();
            for (int i = 0; i < n; i++)
            {
                int r = 12 + 16 * i;
                var tag = Tag(d, r);
                int off = (int)U32(d, r + 8), len = (int)U32(d, r + 12);
                if (off < 0 || len < 0 || off + len > d.Length) throw new FontPatchException("Oštećena tablica " + tag + ".");
                var b = new byte[len];
                Buffer.BlockCopy(d, off, b, 0, len);
                t[tag] = b;
            }
            return t;
        }

        private class Cmap
        {
            public Dictionary<int, int> Map = new Dictionary<int, int>();
        }

        private static Cmap ReadCmap(byte[] c)
        {
            int n = U16(c, 2);
            int best = -1, bestScore = -1;
            for (int i = 0; i < n; i++)
            {
                int r = 4 + 8 * i;
                int plat = U16(c, r), enc = U16(c, r + 2);
                int off = (int)U32(c, r + 4);
                if (off + 2 > c.Length) continue;
                int fmt = U16(c, off);
                int score = -1;
                if (fmt == 12 && ((plat == 3 && enc == 10) || plat == 0)) score = 4;
                else if (fmt == 4 && plat == 3 && enc == 1) score = 3;
                else if (fmt == 4 && plat == 0) score = 2;
                if (score > bestScore) { bestScore = score; best = off; }
            }
            if (best < 0) throw new FontPatchException("Font nema Unicode tablicu znakova (možda je simbolni font).");
            var m = new Cmap();
            int f = U16(c, best);
            if (f == 4)
            {
                int segX2 = U16(c, best + 6), seg = segX2 / 2;
                int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                for (int s = 0; s < seg; s++)
                {
                    int end = U16(c, ends + 2 * s), start = U16(c, starts + 2 * s);
                    int delta = I16(c, deltas + 2 * s), ro = U16(c, ranges + 2 * s);
                    for (int code = start; code <= end && code != 0xFFFF; code++)
                    {
                        int gid;
                        if (ro == 0) gid = (code + delta) & 0xFFFF;
                        else
                        {
                            int addr = ranges + 2 * s + ro + 2 * (code - start);
                            if (addr + 2 > c.Length) continue;
                            gid = U16(c, addr);
                            if (gid != 0) gid = (gid + delta) & 0xFFFF;
                        }
                        if (gid != 0) m.Map[code] = gid;
                    }
                }
            }
            else
            {
                uint ng = U32(c, best + 12);
                for (uint g = 0; g < ng; g++)
                {
                    int r = best + 16 + (int)g * 12;
                    uint start = U32(c, r), end = U32(c, r + 4), sg = U32(c, r + 8);
                    for (uint code = start; code <= end && code - start < 0x110000; code++)
                        m.Map[(int)code] = (int)(sg + (code - start));
                }
            }
            return m;
        }

        private struct BBox
        {
            public int XMin, YMin, XMax, YMax;
            public bool Empty;
            public double CX => (XMin + XMax) / 2.0;
            public double CY => (YMin + YMax) / 2.0;
            public int W => XMax - XMin;
            public int H => YMax - YMin;
        }

        private static BBox BoxOf(byte[] g)
        {
            if (g == null || g.Length < 10) return new BBox { Empty = true };
            return new BBox { XMin = I16(g, 2), YMin = I16(g, 4), XMax = I16(g, 6), YMax = I16(g, 8) };
        }

        /// <summary>Broj točaka i kontura (rekurzivno) i dubina složenosti glifa — za maxp.</summary>
        private static (int pts, int cont, int depth, int comps) Stats(List<byte[]> glyphs, int gid, int guard = 0)
        {
            if (gid < 0 || gid >= glyphs.Count || guard > 16) return (0, 0, 0, 0);
            var g = glyphs[gid];
            if (g == null || g.Length < 10) return (0, 0, 0, 0);
            int nc = I16(g, 0);
            if (nc >= 0)
            {
                if (nc == 0) return (0, 0, 0, 0);
                return (U16(g, 10 + 2 * (nc - 1)) + 1, nc, 0, 0);
            }
            int p = 10, pts = 0, cont = 0, depth = 0, comps = 0;
            while (true)
            {
                int flags = U16(g, p), cg = U16(g, p + 2);
                p += 4 + ((flags & 1) != 0 ? 4 : 2);
                if ((flags & 0x8) != 0) p += 2; else if ((flags & 0x40) != 0) p += 4; else if ((flags & 0x80) != 0) p += 8;
                var s = Stats(glyphs, cg, guard + 1);
                pts += s.pts; cont += s.cont; depth = Math.Max(depth, s.depth); comps++;
                if ((flags & 0x20) == 0) break;
            }
            return (pts, cont, depth + 1, comps);
        }

        // ---------------------------------------------------- obrisi (za pronalazak stupca slova)
        /// <summary>Vrati poligone (aproksimirane) jednostavnog glifa; null za složene.</summary>
        private static List<List<(double x, double y)>> Outline(byte[] g)
        {
            if (g == null || g.Length < 10) return null;
            int nc = I16(g, 0);
            if (nc <= 0) return null;
            var ends = new int[nc];
            for (int i = 0; i < nc; i++) ends[i] = U16(g, 10 + 2 * i);
            int np = ends[nc - 1] + 1;
            int p = 10 + 2 * nc;
            int insLen = U16(g, p);
            p += 2 + insLen;
            var flags = new byte[np];
            for (int i = 0; i < np;)
            {
                byte f = g[p++];
                flags[i++] = f;
                if ((f & 8) != 0)
                {
                    int rep = g[p++];
                    for (int k = 0; k < rep && i < np; k++) flags[i++] = f;
                }
            }
            var xs = new int[np];
            var ys = new int[np];
            int v = 0;
            for (int i = 0; i < np; i++)
            {
                byte f = flags[i];
                if ((f & 2) != 0) { int dv = g[p++]; v += (f & 16) != 0 ? dv : -dv; }
                else if ((f & 16) == 0) { v += I16(g, p); p += 2; }
                xs[i] = v;
            }
            v = 0;
            for (int i = 0; i < np; i++)
            {
                byte f = flags[i];
                if ((f & 4) != 0) { int dv = g[p++]; v += (f & 32) != 0 ? dv : -dv; }
                else if ((f & 32) == 0) { v += I16(g, p); p += 2; }
                ys[i] = v;
            }

            var polys = new List<List<(double, double)>>();
            int startPt = 0;
            for (int c = 0; c < nc; c++)
            {
                int endPt = ends[c];
                int count = endPt - startPt + 1;
                if (count < 2) { startPt = endPt + 1; continue; }
                var pts = new List<(double x, double y, bool on)>();
                for (int i = startPt; i <= endPt; i++) pts.Add((xs[i], ys[i], (flags[i] & 1) != 0));
                // umetni implicitne točke na krivulji između dviju kontrolnih
                var full = new List<(double x, double y, bool on)>();
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Count];
                    full.Add(a);
                    if (!a.on && !b.on) full.Add(((a.x + b.x) / 2, (a.y + b.y) / 2, true));
                }
                int first = full.FindIndex(q => q.on);
                if (first < 0) { startPt = endPt + 1; continue; }
                var poly = new List<(double, double)>();
                int m = full.Count;
                for (int k = 0; k < m; k++)
                {
                    var cur = full[(first + k) % m];
                    if (cur.on)
                    {
                        poly.Add((cur.x, cur.y));
                        continue;
                    }
                    var prev = full[(first + k - 1 + m) % m];
                    var next = full[(first + k + 1) % m];
                    for (int s = 1; s <= 6; s++)
                    {
                        double t = s / 6.0;
                        double x = (1 - t) * (1 - t) * prev.x + 2 * (1 - t) * t * cur.x + t * t * next.x;
                        double y = (1 - t) * (1 - t) * prev.y + 2 * (1 - t) * t * cur.y + t * t * next.y;
                        if (s < 6) poly.Add((x, y));
                    }
                }
                polys.Add(poly);
                startPt = endPt + 1;
            }
            return polys;
        }

        /// <summary>Ispunjeni rasponi (x1, x2) glifa na visini y (pravilo ne-nula).</summary>
        private static List<(double a, double b)> Spans(List<List<(double x, double y)>> polys, double y)
        {
            var hits = new List<(double x, int dir)>();
            foreach (var poly in polys)
                for (int i = 0; i < poly.Count; i++)
                {
                    var p1 = poly[i];
                    var p2 = poly[(i + 1) % poly.Count];
                    if ((p1.y <= y && p2.y > y) || (p2.y <= y && p1.y > y))
                    {
                        double x = p1.x + (y - p1.y) * (p2.x - p1.x) / (p2.y - p1.y);
                        hits.Add((x, p2.y > p1.y ? 1 : -1));
                    }
                }
            hits.Sort((a, b) => a.x.CompareTo(b.x));
            var spans = new List<(double, double)>();
            int wind = 0;
            double startX = 0;
            foreach (var h in hits)
            {
                int before = wind;
                wind += h.dir;
                if (before == 0 && wind != 0) startX = h.x;
                else if (before != 0 && wind == 0) spans.Add((startX, h.x));
            }
            return spans;
        }

        // ------------------------------------------------------------------ glavni postupak
        /// <summary>Koji hrvatski znakovi nedostaju u fontu (prazno ako je sve tu ili font nije podržan).</summary>
        public static List<int> MissingCodepoints(byte[] font)
        {
            try
            {
                var t = ReadTables(font);
                if (!t.ContainsKey("cmap")) return new List<int>();
                var cm = ReadCmap(t["cmap"]);
                return Targets.Select(x => x.cp).Where(cp => !cm.Map.ContainsKey(cp)).ToList();
            }
            catch { return new List<int>(); }
        }

        public static PatchResult Patch(byte[] font, PatchOptions o)
        {
            o ??= new PatchOptions();
            var t = ReadTables(font);
            foreach (var need in new[] { "head", "hhea", "hmtx", "maxp", "cmap", "loca", "glyf" })
                if (!t.ContainsKey(need))
                    throw new FontPatchException(need == "glyf" || need == "loca"
                        ? "Font nema TrueType obrise (glyf) — zasad su podržani samo TTF fontovi."
                        : "Fontu nedostaje tablica " + need + ".");
            if (t.ContainsKey("fvar"))
                throw new FontPatchException("Varijabilni fontovi još nisu podržani. Koristi statičnu inačicu fonta (npr. Regular).");

            var head = (byte[])t["head"].Clone();
            var hhea = (byte[])t["hhea"].Clone();
            var maxp = (byte[])t["maxp"].Clone();
            int upem = U16(head, 18);
            int locFmt = I16(head, 50);
            int numGlyphs = U16(maxp, 4);
            int numHM = U16(hhea, 34);

            // glifovi
            var loca = t["loca"];
            var glyf = t["glyf"];
            var offs = new long[numGlyphs + 1];
            for (int i = 0; i <= numGlyphs; i++)
                offs[i] = locFmt == 0 ? U16(loca, 2 * i) * 2L : U32(loca, 4 * i);
            var glyphs = new List<byte[]>(numGlyphs + 16);
            for (int i = 0; i < numGlyphs; i++)
            {
                long a = offs[i], b = offs[i + 1];
                if (b <= a || b > glyf.Length) { glyphs.Add(new byte[0]); continue; }
                var g = new byte[b - a];
                Buffer.BlockCopy(glyf, (int)a, g, 0, g.Length);
                glyphs.Add(g);
            }

            // metrike
            var hmtx = t["hmtx"];
            var adv = new List<int>(numGlyphs + 16);
            var lsb = new List<int>(numGlyphs + 16);
            int lastAdv = 0;
            for (int i = 0; i < numGlyphs; i++)
            {
                if (i < numHM) { lastAdv = U16(hmtx, 4 * i); adv.Add(lastAdv); lsb.Add(I16(hmtx, 4 * i + 2)); }
                else { adv.Add(lastAdv); int o2 = 4 * numHM + 2 * (i - numHM); lsb.Add(o2 + 2 <= hmtx.Length ? I16(hmtx, o2) : 0); }
            }

            var cmap = ReadCmap(t["cmap"]);
            int Gid(int cp) => cmap.Map.TryGetValue(cp, out var g) && g < glyphs.Count ? g : -1;
            int FirstGid(params int[] cps) { foreach (var cp in cps) { var g = Gid(cp); if (g > 0 && !BoxOf(glyphs[g]).Empty) return g; } return -1; }

            // mjere fonta
            int xHeight = 0, capHeight = 0;
            if (t.TryGetValue("OS/2", out var os2r) && os2r.Length >= 90 && U16(os2r, 0) >= 2)
            {
                xHeight = I16(os2r, 86);
                capHeight = I16(os2r, 88);
            }
            if (xHeight <= 0) { var g = Gid('x'); xHeight = g > 0 ? BoxOf(glyphs[g]).YMax : (int)(upem * 0.5); }
            if (capHeight <= 0) { var g = Gid('H'); capHeight = g > 0 ? BoxOf(glyphs[g]).YMax : (int)(upem * 0.7); }
            double italic = 0;
            if (t.TryGetValue("post", out var postr) && postr.Length >= 8) italic = I32(postr, 4) / 65536.0;
            double slant = Math.Tan(-italic * Math.PI / 180.0);

            int caronG = FirstGid(0x02C7, 0x030C);
            int circG = caronG < 0 ? FirstGid(0x02C6, 0x0302, 0x005E) : -1;
            int acuteG = FirstGid(0x00B4, 0x02CA, 0x0301);
            int hyphenG = FirstGid(0x002D, 0x2010, 0x00AD, 0x2212, 0x00AF);
            int ethG = FirstGid(0x00D0);

            var res = new PatchResult { UnitsPerEm = upem };
            var newCmap = new Dictionary<int, int>(cmap.Map);
            var newNames = new List<string>();
            double minGap = upem * 0.03 + o.AccentGap;

            foreach (var (cp, baseCh, kind, gname) in Targets)
            {
                var label = char.ConvertFromUtf32(cp);
                if (cmap.Map.ContainsKey(cp)) continue;
                int baseG = Gid(baseCh);
                if (baseG <= 0 || BoxOf(glyphs[baseG]).Empty) { res.Skipped.Add(label + " (nema slova " + baseCh + ")"); continue; }
                var bb = BoxOf(glyphs[baseG]);
                bool upper = char.IsUpper(baseCh);
                var comps = new List<(int gid, int dx, int dy, double sx, double sy, bool metrics)>
                {
                    (baseG, 0, 0, 1, 1, true)
                };

                if (kind == Kind.Bar)
                {
                    if (upper && ethG > 0)
                    {
                        newCmap[cp] = ethG; // Đ izgleda isto kao Ð
                        res.Added.Add(label);
                        continue;
                    }
                    if (hyphenG <= 0) { res.Skipped.Add(label + " (nema crtice)"); continue; }
                    var hb = BoxOf(glyphs[hyphenG]);
                    if (hb.W <= 0 || hb.H <= 0) { res.Skipped.Add(label + " (neispravna crtica)"); continue; }

                    double cy = upper ? capHeight * 0.5 : xHeight + (bb.YMax - xHeight) * 0.52;
                    cy += o.BarShiftY;
                    // pronađi stupac slova na visini crtice
                    double stemA = double.NaN, stemB = double.NaN;
                    var poly = Outline(glyphs[baseG]);
                    if (poly != null)
                    {
                        var spans = Spans(poly, cy);
                        if (spans.Count > 0)
                        {
                            var s = upper ? spans.First() : spans.Last();
                            stemA = s.a; stemB = s.b;
                        }
                    }
                    if (double.IsNaN(stemA))
                    {
                        double sw = Math.Max(hb.H * 1.2, upem * 0.07);
                        if (upper) { stemA = bb.XMin; stemB = bb.XMin + sw; }
                        else { stemA = bb.XMax - sw; stemB = bb.XMax; }
                    }
                    double stemW = Math.Max(stemB - stemA, upem * 0.03);
                    double stemC = (stemA + stemB) / 2;
                    double width = (upper ? Math.Max(stemW * 2.2, upem * 0.16) : Math.Max(stemW * 2.6, upem * 0.2)) * o.BarWidthScale;
                    double cx = upper ? stemC + stemW * 0.35 : stemC - stemW * 0.15;
                    cx += o.BarShiftX;
                    double sx = Math.Min(1.99, Math.Max(0.05, width / hb.W));
                    double sy = Math.Min(1.99, Math.Max(0.2, o.BarThicknessScale));
                    int dx = (int)Math.Round(cx - hb.CX * sx);
                    int dy = (int)Math.Round(cy - hb.CY * sy);
                    comps.Add((hyphenG, dx, dy, sx, sy, false));
                }
                else
                {
                    int accG; bool flip = false;
                    if (kind == Kind.Acute) accG = acuteG;
                    else if (caronG > 0) accG = caronG;
                    else { accG = circG; flip = true; }
                    if (accG <= 0) { res.Skipped.Add(label + (kind == Kind.Acute ? " (nema akcenta ´)" : " (nema kvačice ˇ ni ˆ)")); continue; }
                    var ab = BoxOf(glyphs[accG]);
                    double sy = flip ? -1 : 1;
                    // nakon zrcaljenja zadrži isti okomiti raspon
                    double flipDy = flip ? ab.YMin + ab.YMax : 0;
                    double accBottom = ab.YMin; // nakon zrcaljenja raspon je isti
                    double dy = flipDy + (upper ? capHeight - xHeight : 0);
                    double need = bb.YMax + minGap - (accBottom + (upper ? capHeight - xHeight : 0));
                    if (need > 0) dy += need;
                    else dy += o.AccentGap;
                    double finalBottom = accBottom + dy - flipDy;
                    double dx = bb.CX - ab.CX + slant * (finalBottom - bb.YMax / 2.0);
                    comps.Add((accG, (int)Math.Round(dx), (int)Math.Round(dy), 1, sy, false));
                }

                var glyph = BuildComposite(glyphs, comps);
                glyphs.Add(glyph);
                int ng = glyphs.Count - 1;
                adv.Add(adv[baseG]);
                lsb.Add(BoxOf(glyph).XMin);
                newCmap[cp] = ng;
                newNames.Add(gname);
                res.Added.Add(label);
            }

            if (res.Added.Count == 0 && !o.Rename)
            {
                res.Data = font;
                return res;
            }

            int newNum = glyphs.Count;
            if (newNum > 65535) throw new FontPatchException("Font ima previše znakova.");

            // ---- glyf + loca (dugi format)
            var gb = new Buf();
            var newLoca = new Buf();
            foreach (var g in glyphs)
            {
                newLoca.U32((uint)gb.Length);
                gb.Bytes(g);
                gb.Pad4();
            }
            newLoca.U32((uint)gb.Length);
            t["glyf"] = gb.ToArray();
            t["loca"] = newLoca.ToArray();
            W16(head, 50, 1);

            // ---- hmtx + hhea
            var hb2 = new Buf();
            for (int i = 0; i < newNum; i++) { hb2.U16(adv[i]); hb2.U16(lsb[i] & 0xFFFF); }
            t["hmtx"] = hb2.ToArray();
            W16(hhea, 34, newNum);

            // ---- maxp
            W16(maxp, 4, newNum);
            if (U32(maxp, 0) == 0x00010000 && maxp.Length >= 32)
            {
                int mcp = U16(maxp, 10), mcc = U16(maxp, 12), mce = U16(maxp, 28), mcd = U16(maxp, 30);
                for (int i = numGlyphs; i < newNum; i++)
                {
                    var s = Stats(glyphs, i);
                    mcp = Math.Max(mcp, s.pts); mcc = Math.Max(mcc, s.cont);
                    mce = Math.Max(mce, s.comps); mcd = Math.Max(mcd, s.depth);
                }
                W16(maxp, 10, Math.Min(65535, mcp)); W16(maxp, 12, Math.Min(65535, mcc));
                W16(maxp, 28, mce); W16(maxp, 30, mcd);
            }

            // ---- cmap
            t["cmap"] = BuildCmap(newCmap);

            // ---- post
            if (t.TryGetValue("post", out var post) && post.Length >= 32)
                t["post"] = ExtendPost(post, numGlyphs, newNum, newNames);

            // ---- OS/2: Latin Extended-A i kodna stranica 1250
            if (t.TryGetValue("OS/2", out var os2) && os2.Length >= 68)
            {
                os2 = (byte[])os2.Clone();
                W32(os2, 42, U32(os2, 42) | 4u);
                if (U16(os2, 0) >= 1 && os2.Length >= 82) W32(os2, 78, U32(os2, 78) | 2u);
                int last = U16(os2, 66);
                int maxCp = newCmap.Keys.Where(k => k <= 0xFFFF).DefaultIfEmpty(0).Max();
                if (maxCp > last) W16(os2, 66, maxCp);
                t["OS/2"] = os2;
            }

            // ---- ime
            if (o.Rename && t.TryGetValue("name", out var name))
            {
                var (tbl, fam) = Rename(name, o.Suffix);
                t["name"] = tbl;
                res.NewFamily = fam;
            }

            // ---- tablice koje ovise o broju glifova ili potpisu
            foreach (var drop in new[] { "hdmx", "LTSH", "VDMX", "DSIG", "vhea", "vmtx" }) t.Remove(drop);

            t["head"] = head;
            t["hhea"] = hhea;
            t["maxp"] = maxp;
            res.Data = WriteFont(t);
            return res;
        }

        private static byte[] BuildComposite(List<byte[]> glyphs, List<(int gid, int dx, int dy, double sx, double sy, bool metrics)> comps)
        {
            int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
            foreach (var c in comps)
            {
                var b = BoxOf(glyphs[c.gid]);
                if (b.Empty) continue;
                double x1 = b.XMin * c.sx + c.dx, x2 = b.XMax * c.sx + c.dx;
                double y1 = b.YMin * c.sy + c.dy, y2 = b.YMax * c.sy + c.dy;
                xMin = Math.Min(xMin, (int)Math.Floor(Math.Min(x1, x2)));
                xMax = Math.Max(xMax, (int)Math.Ceiling(Math.Max(x1, x2)));
                yMin = Math.Min(yMin, (int)Math.Floor(Math.Min(y1, y2)));
                yMax = Math.Max(yMax, (int)Math.Ceiling(Math.Max(y1, y2)));
            }
            if (xMin == int.MaxValue) { xMin = yMin = xMax = yMax = 0; }

            var b2 = new Buf();
            b2.U16(0xFFFF); // -1 kontura = složeni glif
            b2.U16(xMin & 0xFFFF); b2.U16(yMin & 0xFFFF); b2.U16(xMax & 0xFFFF); b2.U16(yMax & 0xFFFF);
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                bool scaled = Math.Abs(c.sx - 1) > 1e-6 || Math.Abs(c.sy - 1) > 1e-6;
                bool uniform = Math.Abs(c.sx - c.sy) < 1e-6;
                int flags = 0x0001 | 0x0002 | 0x0004 | 0x1000; // riječi, xy pomaci, zaokruži na mrežu, pomak se ne skalira
                if (i < comps.Count - 1) flags |= 0x0020;
                if (c.metrics) flags |= 0x0200;
                if (scaled) flags |= uniform ? 0x0008 : 0x0040;
                b2.U16(flags);
                b2.U16(c.gid);
                b2.U16(Math.Max(-32768, Math.Min(32767, c.dx)) & 0xFFFF);
                b2.U16(Math.Max(-32768, Math.Min(32767, c.dy)) & 0xFFFF);
                if (scaled)
                {
                    int F(double v) => (int)Math.Round(Math.Max(-2, Math.Min(1.99993, v)) * 16384) & 0xFFFF;
                    if (uniform) b2.U16(F(c.sx));
                    else { b2.U16(F(c.sx)); b2.U16(F(c.sy)); }
                }
            }
            return b2.ToArray();
        }

        private static byte[] Format4(SortedDictionary<int, int> bmp, bool useDelta)
        {
            var codes = bmp.Keys.Where(k => k < 0xFFFF).ToList();
            var segs = new List<(int start, int end, int delta, List<int> gids)>();
            int i = 0;
            while (i < codes.Count)
            {
                int start = codes[i], j = i;
                if (useDelta)
                {
                    int d = bmp[start] - start;
                    while (j + 1 < codes.Count && codes[j + 1] == codes[j] + 1 && bmp[codes[j + 1]] - codes[j + 1] == d) j++;
                    segs.Add((start, codes[j], d, null));
                }
                else
                {
                    while (j + 1 < codes.Count && codes[j + 1] == codes[j] + 1) j++;
                    segs.Add((start, codes[j], 0, Enumerable.Range(i, j - i + 1).Select(k => bmp[codes[k]]).ToList()));
                }
                i = j + 1;
            }
            segs.Add((0xFFFF, 0xFFFF, 1, null));
            int n = segs.Count;
            int glyphArr = segs.Where(s => s.gids != null).Sum(s => s.gids.Count);
            int len = 16 + 8 * n + 2 * glyphArr;
            if (len > 65535) return null;

            var b = new Buf();
            // searchRange = 2 * 2^floor(log2(n))
            int pow = 1, log = 0;
            while (pow * 2 <= n) { pow *= 2; log++; }
            b.U16(4); b.U16(len); b.U16(0);
            b.U16(n * 2); b.U16(pow * 2); b.U16(log); b.U16(n * 2 - pow * 2);
            foreach (var s in segs) b.U16(s.end);
            b.U16(0);
            foreach (var s in segs) b.U16(s.start);
            foreach (var s in segs) b.U16(s.gids != null ? 0 : s.delta & 0xFFFF);
            // idRangeOffset: udaljenost od mjesta u nizu do početka gid-ova tog segmenta
            int arrPos = 0;
            for (int k = 0; k < n; k++)
            {
                var s = segs[k];
                if (s.gids == null) { b.U16(0); continue; }
                int ro = 2 * (n - k) + 2 * arrPos;
                b.U16(ro);
                arrPos += s.gids.Count;
            }
            foreach (var s in segs) if (s.gids != null) foreach (var g in s.gids) b.U16(g);
            return b.ToArray();
        }

        private static byte[] BuildCmap(Dictionary<int, int> map)
        {
            var bmp = new SortedDictionary<int, int>(map.Where(kv => kv.Key <= 0xFFFF && kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value));
            var f4 = Format4(bmp, true) ?? Format4(bmp, false);
            bool needs12 = f4 == null || map.Keys.Any(k => k > 0xFFFF);
            byte[] f12 = null;
            if (needs12)
            {
                var all = map.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).ToList();
                var groups = new List<(int s, int e, int g)>();
                foreach (var kv in all)
                {
                    if (groups.Count > 0)
                    {
                        var last = groups[groups.Count - 1];
                        if (kv.Key == last.e + 1 && kv.Value == last.g + (last.e - last.s) + 1) { groups[groups.Count - 1] = (last.s, kv.Key, last.g); continue; }
                    }
                    groups.Add((kv.Key, kv.Key, kv.Value));
                }
                var b = new Buf();
                b.U16(12); b.U16(0); b.U32((uint)(16 + 12 * groups.Count)); b.U32(0); b.U32((uint)groups.Count);
                foreach (var g in groups) { b.U32((uint)g.s); b.U32((uint)g.e); b.U32((uint)g.g); }
                f12 = b.ToArray();
            }
            if (f4 == null)
            {
                // samo BMP dio koji stane (rijetko): uzmi prvih ~16000 znakova
                var small = new SortedDictionary<int, int>(bmp.Take(16000).ToDictionary(kv => kv.Key, kv => kv.Value));
                f4 = Format4(small, true) ?? Format4(small, false);
            }

            var recs = new List<(int plat, int enc, byte[] data)>
            {
                (0, 3, f4),
                (3, 1, f4)
            };
            if (f12 != null) { recs.Insert(1, (0, 4, f12)); recs.Add((3, 10, f12)); }
            recs = recs.OrderBy(r => r.plat).ThenBy(r => r.enc).ToList();

            var outB = new Buf();
            outB.U16(0); outB.U16(recs.Count);
            int off = 4 + 8 * recs.Count;
            var placed = new Dictionary<byte[], int>();
            var order = new List<byte[]>();
            foreach (var r in recs)
            {
                if (!placed.ContainsKey(r.data)) { placed[r.data] = off; order.Add(r.data); off += r.data.Length; }
            }
            foreach (var r in recs) { outB.U16(r.plat); outB.U16(r.enc); outB.U32((uint)placed[r.data]); }
            foreach (var d in order) outB.Bytes(d);
            return outB.ToArray();
        }

        private static byte[] ExtendPost(byte[] post, int oldNum, int newNum, List<string> names)
        {
            uint fmt = U32(post, 0);
            if (fmt == 0x00030000) return post;
            if (fmt != 0x00020000 || post.Length < 34)
            {
                var p3 = new byte[32];
                Buffer.BlockCopy(post, 0, p3, 0, 32);
                W32(p3, 0, 0x00030000);
                return p3;
            }
            int n = U16(post, 32);
            if (n != oldNum)
            {
                var p3 = new byte[32];
                Buffer.BlockCopy(post, 0, p3, 0, 32);
                W32(p3, 0, 0x00030000);
                return p3;
            }
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = U16(post, 34 + 2 * i);
            int p = 34 + 2 * n;
            var strings = new List<byte[]>();
            while (p < post.Length)
            {
                int l = post[p];
                if (p + 1 + l > post.Length) break;
                var s = new byte[l];
                Buffer.BlockCopy(post, p + 1, s, 0, l);
                strings.Add(s);
                p += 1 + l;
            }
            var b = new Buf();
            var hdr = new byte[32];
            Buffer.BlockCopy(post, 0, hdr, 0, 32);
            b.Bytes(hdr);
            b.U16(newNum);
            foreach (var i in idx) b.U16(i);
            for (int k = 0; k < newNum - oldNum; k++) b.U16(258 + strings.Count + k);
            foreach (var s in strings) { b.U8(s.Length); b.Bytes(s); }
            for (int k = 0; k < newNum - oldNum; k++)
            {
                var nm = Encoding.ASCII.GetBytes(k < names.Count ? names[k] : "glyph" + (oldNum + k));
                b.U8(nm.Length); b.Bytes(nm);
            }
            return b.ToArray();
        }

        private static (byte[] table, string family) Rename(byte[] name, string suffix)
        {
            int count = U16(name, 2), strOff = U16(name, 4);
            var recs = new List<(int plat, int enc, int lang, int id, string text)>();
            for (int i = 0; i < count; i++)
            {
                int r = 6 + 12 * i;
                if (r + 12 > name.Length) break;
                int plat = U16(name, r), enc = U16(name, r + 2), lang = U16(name, r + 4), id = U16(name, r + 6);
                int len = U16(name, r + 8), off = U16(name, r + 10);
                if (lang >= 0x8000) continue; // jezične oznake (format 1) se ne prenose
                int start = strOff + off;
                if (start + len > name.Length) continue;
                string text = plat == 1 ? Encoding.ASCII.GetString(name, start, len)
                    : Encoding.BigEndianUnicode.GetString(name, start, len);
                recs.Add((plat, enc, lang, id, text));
            }

            string Pick(int id) => recs.Where(x => x.id == id && x.plat == 3).Select(x => x.text).FirstOrDefault()
                                   ?? recs.Where(x => x.id == id).Select(x => x.text).FirstOrDefault();
            var fam1 = Pick(1) ?? "Font";
            var fam16 = Pick(16);
            var family = (fam16 ?? fam1).Trim();
            var newFamily = family + " " + suffix;

            var outRecs = new List<(int plat, int enc, int lang, int id, string text)>();
            foreach (var r in recs)
            {
                string tx = r.text;
                switch (r.id)
                {
                    case 1:
                    case 16:
                    case 21:
                        tx = r.text.Trim() + " " + suffix;
                        break;
                    case 4:
                        {
                            var f = (fam16 ?? fam1).Trim();
                            int k = tx.IndexOf(f, StringComparison.Ordinal);
                            tx = k >= 0 ? tx.Substring(0, k + f.Length) + " " + suffix + tx.Substring(k + f.Length) : tx + " " + suffix;
                            break;
                        }
                    case 3:
                        tx = tx + ";FontShelf-" + suffix;
                        break;
                    case 6:
                        {
                            var parts = tx.Split(new[] { '-' }, 2);
                            tx = parts[0] + suffix + (parts.Length > 1 ? "-" + parts[1] : "");
                            tx = new string(tx.Where(ch => ch > 32 && ch < 127 && "[](){}<>/%".IndexOf(ch) < 0).ToArray());
                            if (tx.Length > 63) tx = tx.Substring(0, 63);
                            break;
                        }
                }
                outRecs.Add((r.plat, r.enc, r.lang, r.id, tx));
            }
            outRecs = outRecs.OrderBy(r => r.plat).ThenBy(r => r.enc).ThenBy(r => r.lang).ThenBy(r => r.id).ToList();

            var storage = new Buf();
            var b = new Buf();
            b.U16(0); b.U16(outRecs.Count); b.U16(6 + 12 * outRecs.Count);
            foreach (var r in outRecs)
            {
                byte[] data = r.plat == 1
                    ? Encoding.ASCII.GetBytes(new string(r.text.Select(ch => ch < 128 ? ch : '?').ToArray()))
                    : Encoding.BigEndianUnicode.GetBytes(r.text);
                b.U16(r.plat); b.U16(r.enc); b.U16(r.lang); b.U16(r.id);
                b.U16(data.Length); b.U16(storage.Length);
                storage.Bytes(data);
            }
            b.Bytes(storage.ToArray());
            return (b.ToArray(), newFamily);
        }

        private static uint Checksum(byte[] d)
        {
            uint sum = 0;
            int n = (d.Length + 3) / 4;
            for (int i = 0; i < n; i++)
            {
                uint v = 0;
                for (int k = 0; k < 4; k++)
                {
                    int idx = 4 * i + k;
                    v = (v << 8) | (idx < d.Length ? d[idx] : (byte)0);
                }
                sum = unchecked(sum + v);
            }
            return sum;
        }

        private static byte[] WriteFont(Dictionary<string, byte[]> t)
        {
            var tags = t.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            int n = tags.Count;
            int pow = 1, log = 0;
            while (pow * 2 <= n) { pow *= 2; log++; }

            var head = t["head"];
            W32(head, 8, 0); // checkSumAdjustment

            int offset = 12 + 16 * n;
            var dir = new Buf();
            dir.U32(0x00010000); dir.U16(n); dir.U16(pow * 16); dir.U16(log); dir.U16(n * 16 - pow * 16);
            var body = new Buf();
            int headPos = -1;
            foreach (var tag in tags)
            {
                var data = t[tag];
                dir.Bytes(Encoding.ASCII.GetBytes(tag));
                dir.U32(Checksum(data));
                dir.U32((uint)(offset + body.Length));
                dir.U32((uint)data.Length);
                if (tag == "head") headPos = offset + body.Length;
                body.Bytes(data);
                body.Pad4();
            }
            var all = new byte[dir.Length + body.Length];
            Buffer.BlockCopy(dir.ToArray(), 0, all, 0, dir.Length);
            Buffer.BlockCopy(body.ToArray(), 0, all, dir.Length, body.Length);
            uint adj = unchecked(0xB1B0AFBA - Checksum(all));
            if (headPos >= 0) W32(all, headPos + 8, adj);
            return all;
        }
    }
}
