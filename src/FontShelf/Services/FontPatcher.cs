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
        public double AccentShiftX { get; set; }     // pomak kvačica/akcenta lijevo-desno (jedinice)
        public double BarShiftYUpper { get; set; }   // pomak crtice na Đ gore-dolje (jedinice)
        public bool UseEth { get; set; } = true;     // Đ = postojeće Ð ako ga font ima
        // Ručni odabir znaka za kvačicu / akcent (0 = automatski) te veličina, zakret (stupnjevi) i zrcaljenje
        public int CaronGlyph { get; set; }
        public double CaronScale { get; set; } = 1.0;
        public double CaronRotate { get; set; }
        public bool CaronFlip { get; set; }
        public int AcuteGlyph { get; set; }
        public double AcuteScale { get; set; } = 1.0;
        public double AcuteRotate { get; set; }
        public bool AcuteFlip { get; set; }
        // širina (rastezanje, 1 = bez promjene) i podebljanje (jedinice fonta, + deblje, − tanje)
        public double CaronWidth { get; set; } = 1.0;
        public double CaronThickness { get; set; }
        public double AcuteWidth { get; set; } = 1.0;
        public double AcuteThickness { get; set; }
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
        public bool IsCff;
        public string Extension => IsCff ? ".otf" : ".ttf";
    }

    public class FontPatchException : Exception
    {
        public FontPatchException(string msg) : base(msg) { }
    }

    /// <summary>
    /// Dodaje č ć š ž đ Č Ć Š Ž Đ u TrueType ili CFF (.otf) font slaganjem postojećih dijelova:
    /// slovo + kvačica/akcent, odnosno slovo + crtica (TTF: složeni glifovi, CFF: spojeni obrisi).
    /// </summary>
    public static partial class FontPatcher
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
            if (ver != 0x00010000 && ver != 0x74727565 && ver != 0x4F54544F) throw new FontPatchException("Nepoznat format fonta.");
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
                if (count < 2) { polys.Add(new List<(double, double)>()); startPt = endPt + 1; continue; }
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
                if (first < 0) { polys.Add(new List<(double, double)>()); startPt = endPt + 1; continue; }
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

        // ------------------------------------------------------------------ akcenti iz postojećih slova
        // Slova iz kojih se može "posuditi" akcent kakav je nacrtao dizajner (redoslijed = prednost).
        private static readonly (int cp, char b)[] AcuteLower = { (0x107, 'c'), (0x15B, 's'), (0x144, 'n'), (0xE9, 'e'), (0xE1, 'a'), (0xF3, 'o'), (0xFA, 'u') };
        private static readonly (int cp, char b)[] AcuteUpper = { (0x106, 'C'), (0x15A, 'S'), (0x143, 'N'), (0xC9, 'E'), (0xC1, 'A'), (0xD3, 'O'), (0xDA, 'U') };
        private static readonly (int cp, char b)[] CaronLower = { (0x10D, 'c'), (0x161, 's'), (0x17E, 'z'), (0x11B, 'e'), (0x148, 'n'), (0x159, 'r') };
        private static readonly (int cp, char b)[] CaronUpper = { (0x10C, 'C'), (0x160, 'S'), (0x17D, 'Z'), (0x11A, 'E'), (0x147, 'N'), (0x158, 'R') };

        private class AccentSrc
        {
            public int Gid;
            public double Dx, Dy;      // položaj akcenta u slovu iz kojeg je uzet
            public double BaseCX;      // središte osnovnog slova u tom slovu
            public double Lift;        // dodatno podizanje (malo slovo -> veliko)
        }

        /// <summary>Komponente složenog TrueType glifa (null ako nisu jednostavni pomaci).</summary>
        private static List<(int gid, int dx, int dy)> Components(byte[] g)
        {
            if (g == null || g.Length < 10 || I16(g, 0) >= 0) return null;
            var list = new List<(int, int, int)>();
            int p = 10;
            while (p + 4 <= g.Length)
            {
                int flags = U16(g, p), cg = U16(g, p + 2);
                p += 4;
                if ((flags & 2) == 0) return null; // poravnanje po točkama
                int dx, dy;
                if ((flags & 1) != 0) { dx = I16(g, p); dy = I16(g, p + 2); p += 4; }
                else { dx = (sbyte)g[p]; dy = (sbyte)g[p + 1]; p += 2; }
                if ((flags & (0x8 | 0x40 | 0x80)) != 0) return null; // skalirano
                list.Add((cg, dx, dy));
                if ((flags & 0x20) == 0) break;
            }
            return list;
        }

        /// <summary>Novi jednostavni TrueType glif od odabranih kontura postojećeg (bez instrukcija).</summary>
        private static byte[] SubsetSimple(byte[] g, List<int> keep)
        {
            int nc = I16(g, 0);
            var ends = new int[nc];
            for (int i = 0; i < nc; i++) ends[i] = U16(g, 10 + 2 * i);
            int np = ends[nc - 1] + 1;
            int p = 10 + 2 * nc;
            p += 2 + U16(g, p);
            var flags = new byte[np];
            for (int i = 0; i < np;)
            {
                byte f = g[p++];
                flags[i++] = f;
                if ((f & 8) != 0) { int rep = g[p++]; for (int k = 0; k < rep && i < np; k++) flags[i++] = f; }
            }
            var xs = new int[np]; var ys = new int[np];
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
            var pts = new List<(int x, int y, bool on)>();
            var newEnds = new List<int>();
            keep = keep.OrderBy(k => k).ToList();
            foreach (var c in keep)
            {
                int start = c == 0 ? 0 : ends[c - 1] + 1;
                for (int i = start; i <= ends[c]; i++) pts.Add((xs[i], ys[i], (flags[i] & 1) != 0));
                newEnds.Add(pts.Count - 1);
            }
            var b = new Buf();
            b.U16(newEnds.Count);
            b.U16(pts.Min(q => q.x) & 0xFFFF); b.U16(pts.Min(q => q.y) & 0xFFFF);
            b.U16(pts.Max(q => q.x) & 0xFFFF); b.U16(pts.Max(q => q.y) & 0xFFFF);
            foreach (var e in newEnds) b.U16(e);
            b.U16(0); // bez instrukcija
            foreach (var q in pts) b.U8(q.on ? 1 : 0);
            int lx = 0, ly = 0;
            foreach (var q in pts) { b.U16((q.x - lx) & 0xFFFF); lx = q.x; }
            foreach (var q in pts) { b.U16((q.y - ly) & 0xFFFF); ly = q.y; }
            return b.ToArray();
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
            if (t.ContainsKey("fvar") || t.ContainsKey("CFF2"))
                throw new FontPatchException("Varijabilni fontovi još nisu podržani. Koristi statičnu inačicu fonta (npr. Regular).");
            bool isCff = t.ContainsKey("CFF ");
            foreach (var need in isCff ? new[] { "head", "hhea", "hmtx", "maxp", "cmap" } : new[] { "head", "hhea", "hmtx", "maxp", "cmap", "loca", "glyf" })
                if (!t.ContainsKey(need))
                    throw new FontPatchException(need == "glyf" || need == "loca"
                        ? "Font nema ni TrueType ni CFF obrise."
                        : "Fontu nedostaje tablica " + need + ".");
            var head = (byte[])t["head"].Clone();
            var hhea = (byte[])t["hhea"].Clone();
            var maxp = (byte[])t["maxp"].Clone();
            int upem = U16(head, 18);
            int locFmt = I16(head, 50);
            int numGlyphs = U16(maxp, 4);
            int numHM = U16(hhea, 34);

            // glifovi
            var glyphs = new List<byte[]>(numGlyphs + 16);
            CffFont cff = null;
            if (isCff)
            {
                cff = new CffFont(t["CFF "]);
                if (cff.Count != numGlyphs) throw new FontPatchException("Broj znakova u CFF tablici ne odgovara fontu.");
            }
            var loca = isCff ? null : t["loca"];
            var glyf = isCff ? null : t["glyf"];
            var offs = new long[numGlyphs + 1];
            for (int i = 0; !isCff && i <= numGlyphs; i++)
                offs[i] = locFmt == 0 ? U16(loca, 2 * i) * 2L : U32(loca, 4 * i);
            for (int i = 0; !isCff && i < numGlyphs; i++)
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

            BBox Box(int g) => isCff ? cff.Box(g) : BoxOf(glyphs[g]);
            List<List<(double x, double y)>> Poly(int g) => isCff ? cff.Poly(g) : Outline(glyphs[g]);
            int GlyphCount() => isCff ? cff.Count : glyphs.Count;

            var cmap = ReadCmap(t["cmap"]);
            int Gid(int cp) => cmap.Map.TryGetValue(cp, out var g) && g < GlyphCount() ? g : -1;
            int FirstGid(params int[] cps) { foreach (var cp in cps) { var g = Gid(cp); if (g > 0 && !Box(g).Empty) return g; } return -1; }

            // mjere fonta
            int xHeight = 0, capHeight = 0;
            if (t.TryGetValue("OS/2", out var os2r) && os2r.Length >= 90 && U16(os2r, 0) >= 2)
            {
                xHeight = I16(os2r, 86);
                capHeight = I16(os2r, 88);
            }
            if (xHeight <= 0) { var g = Gid('x'); xHeight = g > 0 ? Box(g).YMax : (int)(upem * 0.5); }
            if (capHeight <= 0) { var g = Gid('H'); capHeight = g > 0 ? Box(g).YMax : (int)(upem * 0.7); }
            double italic = 0;
            if (t.TryGetValue("post", out var postr) && postr.Length >= 8) italic = I32(postr, 4) / 65536.0;
            double slant = Math.Tan(-italic * Math.PI / 180.0);

            int caronG = FirstGid(0x02C7, 0x030C);
            int circG = caronG < 0 ? FirstGid(0x02C6, 0x0302, 0x005E) : -1;
            int acuteG = FirstGid(0x00B4, 0x02CA, 0x0301);
            int hyphenG = FirstGid(0x002D, 0x2010, 0x00AD, 0x2212, 0x00AF);
            int ethG = FirstGid(0x00D0);

            var res = new PatchResult { UnitsPerEm = upem, IsCff = isCff };
            var newCmap = new Dictionary<int, int>(cmap.Map);
            var newNames = new List<string>();
            double minGap = upem * 0.03 + o.AccentGap;

            // dodaj novi (neimenovani u cmap-u) glif i njegove metrike
            int AddGlyphFromContours(int donor, List<int> keep, string nm)
            {
                int gidNew;
                if (isCff) gidNew = cff.AddSubset(donor, keep, adv[donor], nm);
                else { glyphs.Add(SubsetSimple(glyphs[donor], keep)); gidNew = glyphs.Count - 1; }
                adv.Add(adv[donor]);
                var nb = Box(gidNew);
                lsb.Add(nb.Empty ? 0 : nb.XMin);
                newNames.Add(nm);
                return gidNew;
            }

            AccentSrc FromDonors((int cp, char b)[] list, string nm)
            {
                foreach (var (dcp, db) in list)
                {
                    if (!cmap.Map.ContainsKey(dcp)) continue;
                    int dg = Gid(dcp), bg = Gid(db);
                    if (dg <= 0 || bg <= 0) continue;
                    var bBox = Box(bg);
                    if (bBox.Empty) continue;
                    if (!isCff && glyphs[dg].Length >= 10 && I16(glyphs[dg], 0) < 0)
                    {
                        var cs = Components(glyphs[dg]);
                        if (cs == null || cs.Count != 2) continue;
                        int bi = cs.FindIndex(c => c.gid == bg);
                        if (bi < 0) continue;
                        var acc = cs[1 - bi];
                        var accBox = Box(acc.gid);
                        if (accBox.Empty || accBox.YMin + acc.dy < bBox.YMax * 0.6 + cs[bi].dy) continue;
                        return new AccentSrc { Gid = acc.gid, Dx = acc.dx, Dy = acc.dy, BaseCX = bBox.CX + cs[bi].dx };
                    }
                    var dp = Poly(dg);
                    var bp = Poly(bg);
                    if (dp == null || bp == null) continue;
                    int m = dp.Count(x => x.Count > 0) - bp.Count(x => x.Count > 0);
                    if (m < 1 || m > 4) continue;
                    var keep = Enumerable.Range(0, dp.Count).Where(i => dp[i].Count > 0)
                        .OrderByDescending(i => dp[i].Min(q => q.y)).Take(m).ToList();
                    if (keep.Any(i => dp[i].Min(q => q.y) < bBox.YMax * 0.6)) continue;
                    // kontrola: preostale konture moraju odgovarati osnovnom slovu
                    var restTop = Enumerable.Range(0, dp.Count).Where(i => dp[i].Count > 0 && !keep.Contains(i)).Select(i => dp[i].Max(q => q.y)).DefaultIfEmpty(0).Max();
                    if (Math.Abs(restTop - bBox.YMax) > upem * 0.05) continue;
                    int ng2 = AddGlyphFromContours(dg, keep, nm);
                    return new AccentSrc { Gid = ng2, BaseCX = bBox.CX };
                }
                return null;
            }

            var baked = new Dictionary<Kind, (int gid, double xMin, double xMax, double yMin, string err)>();
            (int gid, double xMin, double xMax, double yMin, string err) Bake(Kind kind)
            {
                bool ac = kind == Kind.Acute;
                int manual = ac ? o.AcuteGlyph : o.CaronGlyph;
                double scale = Math.Max(0.05, Math.Min(4, ac ? o.AcuteScale : o.CaronScale));
                double wide = Math.Max(0.1, Math.Min(5, ac ? o.AcuteWidth : o.CaronWidth));
                double rot = (ac ? o.AcuteRotate : o.CaronRotate) * Math.PI / 180.0;
                double thick = ac ? o.AcuteThickness : o.CaronThickness;
                bool flipV = ac ? o.AcuteFlip : o.CaronFlip;
                int accG;
                if (manual > 0)
                {
                    accG = Gid(manual);
                    if (accG <= 0 || Box(accG).Empty) return (-1, 0, 0, 0, "font nema znak " + char.ConvertFromUtf32(manual));
                }
                else if (GetAccent(kind, false) is AccentSrc auto) accG = auto.Gid;
                else if (ac) accG = acuteG;
                else if (caronG > 0) accG = caronG;
                else { accG = circG; flipV = !flipV; }
                if (accG <= 0) return (-1, 0, 0, 0, ac ? "nema akcenta ´" : "nema kvačice ˇ ni ˆ");

                double f = flipV ? -1 : 1;
                double a11 = scale * wide * Math.Cos(rot), a12 = -scale * f * Math.Sin(rot);
                double a21 = scale * wide * Math.Sin(rot), a22 = scale * f * Math.Cos(rot);
                List<List<(double x, double y)>> pts;
                List<List<(int x, int y, bool on)>> tt = null;
                if (isCff) pts = cff.RawPoints(accG);
                else
                {
                    tt = ParsePoints(glyphs[accG]);
                    pts = tt?.Select(c => c.Select(q => ((double)q.x, (double)q.y)).ToList()).ToList();
                }
                if (pts == null || pts.Count == 0) return (-1, 0, 0, 0, "odabrani znak je složen od drugih znakova — odaberi neki drugi");
                pts = pts.Select(c => c.Select(q => (a11 * q.x + a12 * q.y, a21 * q.x + a22 * q.y)).ToList()).ToList();
                if (Math.Abs(thick) > 0.01) pts = Embolden(pts, thick / 2);
                var all = pts.SelectMany(c => c).ToList();
                int gidNew;
                string nm = ac ? "acute.hr" + newNames.Count : "caron.hr" + newNames.Count;
                if (isCff) gidNew = cff.AddFromRaw(accG, pts, adv[accG], nm);
                else
                {
                    var outc = new List<List<(int x, int y, bool on)>>();
                    for (int ci = 0; ci < pts.Count; ci++)
                        outc.Add(pts[ci].Select((q, i) => ((int)Math.Round(q.x), (int)Math.Round(q.y), tt[ci][i].on)).ToList());
                    glyphs.Add(WriteSimple(outc));
                    gidNew = glyphs.Count - 1;
                }
                adv.Add(adv[accG]);
                lsb.Add((int)Math.Floor(all.Min(q => q.x)));
                newNames.Add(nm);
                return (gidNew, all.Min(q => q.x), all.Max(q => q.x), all.Min(q => q.y), null);
            }

            var accentCache = new Dictionary<(Kind, bool), AccentSrc>();
            AccentSrc GetAccent(Kind k, bool up)
            {
                if (accentCache.TryGetValue((k, up), out var a)) return a;
                if (up)
                {
                    a = FromDonors(k == Kind.Acute ? AcuteUpper : CaronUpper, k == Kind.Acute ? "acute.cap.hr" : "caron.cap.hr");
                    if (a == null)
                    {
                        var low = GetAccent(k, false);
                        if (low != null) a = new AccentSrc { Gid = low.Gid, Dx = low.Dx, Dy = low.Dy, BaseCX = low.BaseCX, Lift = capHeight - xHeight };
                    }
                }
                else a = FromDonors(k == Kind.Acute ? AcuteLower : CaronLower, k == Kind.Acute ? "acute.hr" : "caron.hr");
                accentCache[(k, up)] = a;
                return a;
            }

            foreach (var (cp, baseCh, kind, gname) in Targets)
            {
                var label = char.ConvertFromUtf32(cp);
                if (cmap.Map.ContainsKey(cp)) continue;
                int baseG = Gid(baseCh);
                if (baseG <= 0 || Box(baseG).Empty) { res.Skipped.Add(label + " (nema slova " + baseCh + ")"); continue; }
                var bb = Box(baseG);
                bool upper = char.IsUpper(baseCh);
                var comps = new List<(int gid, int dx, int dy, double sx, double sy, bool metrics, double m01, double m10)>
                {
                    (baseG, 0, 0, 1, 1, true, 0, 0)
                };
                List<List<(double x, double y)>> barPolys = null;

                if (kind == Kind.Bar)
                {
                    if (upper && ethG > 0 && o.UseEth)
                    {
                        newCmap[cp] = ethG; // Đ izgleda isto kao Ð
                        res.Added.Add(label);
                        continue;
                    }
                    if (hyphenG <= 0) { res.Skipped.Add(label + " (nema crtice)"); continue; }
                    var hb = Box(hyphenG);
                    if (hb.W <= 0 || hb.H <= 0) { res.Skipped.Add(label + " (neispravna crtica)"); continue; }

                    double cy = upper ? capHeight * 0.5 : xHeight + (bb.YMax - xHeight) * 0.52;
                    cy += upper ? o.BarShiftYUpper : o.BarShiftY;
                    // pronađi stupac slova na visini crtice
                    double stemA = double.NaN, stemB = double.NaN;
                    var poly = Poly(baseG);
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
                    if (isCff)
                    {
                        // Windows u CFF fontovima preklapanje obrisa crta kao rupu, pa se crtica
                        // slaže od dijelova lijevo i desno od slova, bez preklapanja.
                        double bw = hb.W * sx, bh = hb.H * sy;
                        double x0 = cx - bw / 2, x1 = cx + bw / 2, yb = cy - bh / 2, yt = cy + bh / 2;
                        List<(double a, double b)> Free(double y)
                        {
                            var free = new List<(double, double)>();
                            double cur = x0;
                            if (poly != null)
                                foreach (var sp in Spans(poly, y).OrderBy(q => q.a))
                                {
                                    if (sp.b <= cur) continue;
                                    if (sp.a >= x1) break;
                                    if (sp.a > cur) free.Add((cur, Math.Min(sp.a, x1)));
                                    cur = Math.Max(cur, sp.b);
                                }
                            if (cur < x1) free.Add((cur, x1));
                            return free;
                        }
                        var fb = Free(yb); var ft = Free(yt);
                        if (fb.Count != ft.Count) { fb = Free(cy); ft = fb; }
                        barPolys = new List<List<(double x, double y)>>();
                        for (int i = 0; i < fb.Count; i++)
                        {
                            if (fb[i].b - fb[i].a < 2 && ft[i].b - ft[i].a < 2) continue;
                            barPolys.Add(new List<(double, double)> { (fb[i].a, yb), (fb[i].b, yb), (ft[i].b, yt), (ft[i].a, yt) });
                        }
                    }
                    else comps.Add((hyphenG, dx, dy, sx, sy, false, 0, 0));
                }
                else if (Custom(kind, o))
                {
                    // ručno odabran znak ili promijenjen oblik: napravi se poseban glif akcenta i složi iznad slova
                    if (!baked.TryGetValue(kind, out var bk)) baked[kind] = bk = Bake(kind);
                    if (bk.gid < 0) { res.Skipped.Add(label + " (" + bk.err + ")"); continue; }
                    double dy = bb.YMax + minGap - bk.yMin;
                    double dx = bb.CX - (bk.xMin + bk.xMax) / 2 + slant * (minGap + bb.YMax / 2.0) + o.AccentShiftX;
                    comps.Add((bk.gid, (int)Math.Round(dx), (int)Math.Round(dy), 1, 1, false, 0, 0));
                }
                else if (GetAccent(kind, upper) is AccentSrc src)
                {
                    var ab = Box(src.Gid);
                    double bottom = ab.YMin + src.Dy + src.Lift + o.AccentGap;
                    double dy = src.Dy + src.Lift + o.AccentGap;
                    double floor = bb.YMax + upem * 0.01;
                    if (bottom < floor) dy += floor - bottom;
                    double dx = src.Dx + bb.CX - src.BaseCX + slant * (dy - src.Dy) + o.AccentShiftX;
                    comps.Add((src.Gid, (int)Math.Round(dx), (int)Math.Round(dy), 1, 1, false, 0, 0));
                }
                else
                {
                    int accG; bool flip = false;
                    if (kind == Kind.Acute) accG = acuteG;
                    else if (caronG > 0) accG = caronG;
                    else { accG = circG; flip = true; }
                    if (accG <= 0) { res.Skipped.Add(label + (kind == Kind.Acute ? " (nema akcenta ´)" : " (nema kvačice ˇ ni ˆ)")); continue; }
                    var ab = Box(accG);
                    double sy = flip ? -1 : 1;
                    // nakon zrcaljenja zadrži isti okomiti raspon
                    double flipDy = flip ? ab.YMin + ab.YMax : 0;
                    double accBottom = ab.YMin; // nakon zrcaljenja raspon je isti
                    double dy = flipDy + (upper ? capHeight - xHeight : 0);
                    double need = bb.YMax + minGap - (accBottom + (upper ? capHeight - xHeight : 0));
                    if (need > 0) dy += need;
                    else dy += o.AccentGap;
                    double finalBottom = accBottom + dy - flipDy;
                    double dx = bb.CX - ab.CX + slant * (finalBottom - bb.YMax / 2.0) + o.AccentShiftX;
                    comps.Add((accG, (int)Math.Round(dx), (int)Math.Round(dy), 1, sy, false, 0, 0));
                }

                int ng;
                if (isCff)
                {
                    ng = cff.AddComposite(comps, adv[baseG], gname, barPolys);
                    var nb = cff.Box(ng);
                    lsb.Add(nb.Empty ? 0 : nb.XMin);
                }
                else
                {
                    var glyph = BuildComposite(glyphs, comps);
                    glyphs.Add(glyph);
                    ng = glyphs.Count - 1;
                    lsb.Add(BoxOf(glyph).XMin);
                }
                adv.Add(adv[baseG]);
                newCmap[cp] = ng;
                newNames.Add(gname);
                res.Added.Add(label);
            }

            if (res.Added.Count == 0 && !o.Rename)
            {
                res.Data = font;
                return res;
            }

            int newNum = GlyphCount();
            if (newNum > 65535) throw new FontPatchException("Font ima previše znakova.");

            // ---- ime (prije CFF-a, jer i CFF nosi PostScript ime)
            string psName = null;
            if (o.Rename && t.TryGetValue("name", out var name))
            {
                var (tbl, fam, ps) = Rename(name, o.Suffix);
                t["name"] = tbl;
                res.NewFamily = fam;
                psName = ps;
            }

            if (isCff)
                t["CFF "] = cff.Build(psName);
            else
            {
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
            }

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

            // ---- tablice koje ovise o broju glifova ili potpisu
            foreach (var drop in new[] { "hdmx", "LTSH", "VDMX", "DSIG", "vhea", "vmtx" }) t.Remove(drop);

            t["head"] = head;
            t["hhea"] = hhea;
            t["maxp"] = maxp;
            res.Data = WriteFont(t);
            return res;
        }

        private static bool Custom(Kind k, PatchOptions o) => k == Kind.Acute
            ? o.AcuteGlyph > 0 || Math.Abs(o.AcuteScale - 1) > 1e-6 || Math.Abs(o.AcuteRotate) > 1e-6 || o.AcuteFlip
              || Math.Abs(o.AcuteWidth - 1) > 1e-6 || Math.Abs(o.AcuteThickness) > 0.01
            : k == Kind.Caron && (o.CaronGlyph > 0 || Math.Abs(o.CaronScale - 1) > 1e-6 || Math.Abs(o.CaronRotate) > 1e-6 || o.CaronFlip
              || Math.Abs(o.CaronWidth - 1) > 1e-6 || Math.Abs(o.CaronThickness) > 0.01);

        /// <summary>Točke jednostavnog TrueType glifa po konturama (null za složene ili prazne).</summary>
        private static List<List<(int x, int y, bool on)>> ParsePoints(byte[] g)
        {
            if (g == null || g.Length < 10) return null;
            int nc = I16(g, 0);
            if (nc <= 0) return null;
            var ends = new int[nc];
            for (int i = 0; i < nc; i++) ends[i] = U16(g, 10 + 2 * i);
            int np = ends[nc - 1] + 1;
            int p = 10 + 2 * nc;
            p += 2 + U16(g, p);
            var flags = new byte[np];
            for (int i = 0; i < np;)
            {
                byte f = g[p++];
                flags[i++] = f;
                if ((f & 8) != 0) { int rp = g[p++]; for (int k = 0; k < rp && i < np; k++) flags[i++] = f; }
            }
            var xs = new int[np]; var ys = new int[np];
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
            var res = new List<List<(int, int, bool)>>();
            int start = 0;
            for (int c = 0; c < nc; c++)
            {
                var l = new List<(int, int, bool)>();
                for (int i = start; i <= ends[c]; i++) l.Add((xs[i], ys[i], (flags[i] & 1) != 0));
                if (l.Count > 0) res.Add(l);
                start = ends[c] + 1;
            }
            return res.Count == 0 ? null : res;
        }

        /// <summary>Jednostavan TrueType glif iz točaka (bez instrukcija).</summary>
        private static byte[] WriteSimple(List<List<(int x, int y, bool on)>> contours)
        {
            int C(int v) => Math.Max(-32768, Math.Min(32767, v));
            var pts = contours.SelectMany(c => c).Select(q => (x: C(q.x), y: C(q.y), q.on)).ToList();
            var b = new Buf();
            b.U16(contours.Count);
            b.U16(pts.Min(q => q.x) & 0xFFFF); b.U16(pts.Min(q => q.y) & 0xFFFF);
            b.U16(pts.Max(q => q.x) & 0xFFFF); b.U16(pts.Max(q => q.y) & 0xFFFF);
            int e = -1;
            foreach (var c in contours) { e += c.Count; b.U16(e); }
            b.U16(0);
            foreach (var q in pts) b.U8(q.on ? 1 : 0);
            int lx = 0, ly = 0;
            foreach (var q in pts) { b.U16((q.x - lx) & 0xFFFF); lx = q.x; }
            foreach (var q in pts) { b.U16((q.y - ly) & 0xFFFF); ly = q.y; }
            return b.ToArray();
        }

        /// <summary>Podeblja (d &gt; 0) ili stanji (d &lt; 0) obris pomicanjem točaka po okomici za d jedinica.</summary>
        private static List<List<(double x, double y)>> Embolden(List<List<(double x, double y)>> contours, double d)
        {
            double Area(List<(double x, double y)> c)
            {
                double a = 0;
                for (int i = 0; i < c.Count; i++) { var p = c[i]; var q = c[(i + 1) % c.Count]; a += p.x * q.y - q.x * p.y; }
                return a / 2;
            }
            // najveća kontura je vanjska; njezin smjer određuje gdje je "van"
            double big = 0;
            foreach (var c in contours) { var a = Area(c); if (Math.Abs(a) > Math.Abs(big)) big = a; }
            double sign = big >= 0 ? 1 : -1;
            var res = new List<List<(double, double)>>();
            foreach (var c in contours)
            {
                int n = c.Count;
                var o = new List<(double, double)>(n);
                for (int i = 0; i < n; i++)
                {
                    var cur = c[i];
                    (double x, double y) prev = cur, next = cur;
                    for (int k = 1; k < n; k++) { var t = c[(i - k + n) % n]; if (Math.Abs(t.x - cur.x) + Math.Abs(t.y - cur.y) > 0.01) { prev = t; break; } }
                    for (int k = 1; k < n; k++) { var t = c[(i + k) % n]; if (Math.Abs(t.x - cur.x) + Math.Abs(t.y - cur.y) > 0.01) { next = t; break; } }
                    double d1x = cur.x - prev.x, d1y = cur.y - prev.y, d2x = next.x - cur.x, d2y = next.y - cur.y;
                    double l1 = Math.Sqrt(d1x * d1x + d1y * d1y), l2 = Math.Sqrt(d2x * d2x + d2y * d2y);
                    if (l1 < 1e-6 || l2 < 1e-6) { o.Add(cur); continue; }
                    d1x /= l1; d1y /= l1; d2x /= l2; d2y /= l2;
                    // vanjska okomica za konturu suprotnu od kazaljke: (dy, -dx)
                    double nx = (d1y + d2y) * sign, ny = -(d1x + d2x) * sign;
                    double nl = Math.Sqrt(nx * nx + ny * ny);
                    if (nl < 1e-6) { o.Add(cur); continue; }
                    nx /= nl; ny /= nl;
                    double cosHalf = Math.Sqrt(Math.Max(0, (1 + d1x * d2x + d1y * d2y) / 2));
                    double miter = 1 / Math.Max(0.5, cosHalf);
                    o.Add((cur.x + nx * d * miter, cur.y + ny * d * miter));
                }
                res.Add(o);
            }
            return res;
        }

        private static byte[] BuildComposite(List<byte[]> glyphs, List<(int gid, int dx, int dy, double sx, double sy, bool metrics, double m01, double m10)> comps)
        {
            int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
            foreach (var c in comps)
            {
                var b = BoxOf(glyphs[c.gid]);
                if (b.Empty) continue;
                foreach (var (px, py) in new[] { (b.XMin, b.YMin), (b.XMax, b.YMin), (b.XMax, b.YMax), (b.XMin, b.YMax) })
                {
                    double x = px * c.sx + py * c.m10 + c.dx, y = px * c.m01 + py * c.sy + c.dy;
                    xMin = Math.Min(xMin, (int)Math.Floor(x)); xMax = Math.Max(xMax, (int)Math.Ceiling(x));
                    yMin = Math.Min(yMin, (int)Math.Floor(y)); yMax = Math.Max(yMax, (int)Math.Ceiling(y));
                }
            }
            if (xMin == int.MaxValue) { xMin = yMin = xMax = yMax = 0; }

            var b2 = new Buf();
            b2.U16(0xFFFF); // -1 kontura = složeni glif
            b2.U16(xMin & 0xFFFF); b2.U16(yMin & 0xFFFF); b2.U16(xMax & 0xFFFF); b2.U16(yMax & 0xFFFF);
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                bool matrix = Math.Abs(c.m01) > 1e-6 || Math.Abs(c.m10) > 1e-6;
                bool scaled = !matrix && (Math.Abs(c.sx - 1) > 1e-6 || Math.Abs(c.sy - 1) > 1e-6);
                bool uniform = Math.Abs(c.sx - c.sy) < 1e-6;
                int flags = 0x0001 | 0x0002 | 0x0004 | 0x1000; // riječi, xy pomaci, zaokruži na mrežu, pomak se ne skalira
                if (i < comps.Count - 1) flags |= 0x0020;
                if (c.metrics) flags |= 0x0200;
                if (scaled) flags |= uniform ? 0x0008 : 0x0040;
                if (matrix) flags |= 0x0080;
                b2.U16(flags);
                b2.U16(c.gid);
                b2.U16(Math.Max(-32768, Math.Min(32767, c.dx)) & 0xFFFF);
                b2.U16(Math.Max(-32768, Math.Min(32767, c.dy)) & 0xFFFF);
                int F(double v) => (int)Math.Round(Math.Max(-2, Math.Min(1.99993, v)) * 16384) & 0xFFFF;
                if (matrix) { b2.U16(F(c.sx)); b2.U16(F(c.m01)); b2.U16(F(c.m10)); b2.U16(F(c.sy)); }
                if (scaled)
                {
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

        private static (byte[] table, string family, string ps) Rename(byte[] name, string suffix)
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
            var ps = outRecs.Where(r => r.id == 6).Select(r => r.text).FirstOrDefault();
            return (b.ToArray(), newFamily, ps);
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
            dir.U32(t.ContainsKey("CFF ") ? 0x4F54544Fu : 0x00010000u); dir.U16(n); dir.U16(pow * 16); dir.U16(log); dir.U16(n * 16 - pow * 16);
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
