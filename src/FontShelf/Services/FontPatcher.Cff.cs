using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FontShelf.Services
{
    public static partial class FontPatcher
    {
        /// <summary>
        /// Čitanje i ponovno slaganje CFF tablice (OpenType .otf s PostScript obrisima).
        /// Novi glifovi se grade spajanjem obrisa postojećih (CFF nema složene glifove),
        /// bez hintova. Podržani su obični i CID fontovi; CFF2 (varijabilni) nisu.
        /// </summary>
        private class CffFont
        {
            // ------------------------------------------------------------ pomoćno
            private class Entry
            {
                public int Op;
                public byte[] Raw;          // bajtovi operanada kako su bili u izvorniku
                public List<double> Args;
            }

            private class Seg
            {
                public bool Curve;
                public double X1, Y1, X2, Y2, X, Y;
            }

            private class Contour
            {
                public double SX, SY;
                public List<Seg> Segs = new List<Seg>();
            }

            private class Fd
            {
                public List<Entry> FontDict;     // samo za CID
                public List<Entry> Priv = new List<Entry>();
                public List<byte[]> Subrs = new List<byte[]>();
                public byte[] SubrsRaw;
                public double Nominal, Default;
            }

            private readonly byte[] _d;
            private readonly byte[] _header;
            private List<byte[]> _names;
            private List<Entry> _top;
            private List<byte[]> _strings;
            private byte[] _gsubrsRaw;
            private List<byte[]> _gsubrs;
            private List<byte[]> _cs;
            private List<int> _charset;      // gid -> SID ili CID
            private List<int> _fdSelect;     // gid -> fd
            private List<Fd> _fds = new List<Fd>();
            private byte[] _encodingRaw;
            private bool _cid;
            private int _cidCount = 8720;
            private readonly Dictionary<int, List<Contour>> _outlines = new Dictionary<int, List<Contour>>();
            private readonly List<string> _newNames = new List<string>();

            public int Count => _cs.Count;
            public bool IsCid => _cid;

            // ------------------------------------------------------------ čitanje
            public CffFont(byte[] d)
            {
                _d = d;
                if (d.Length < 4 || d[0] != 1) throw new FontPatchException("Nepodržana inačica CFF tablice.");
                int hdr = d[2];
                _header = d.Take(hdr).ToArray();
                int p = hdr;
                _names = ReadIndex(p, out p);
                var tops = ReadIndex(p, out p);
                if (tops.Count != 1) throw new FontPatchException("CFF s više fontova nije podržan.");
                _strings = ReadIndex(p, out p);
                int gs = p;
                _gsubrs = ReadIndex(p, out p);
                _gsubrsRaw = Slice(gs, p - gs);
                _top = ParseDict(tops[0]);

                if (Get(_top, 1206) is List<double> ct && ct.Count > 0 && (int)ct[0] != 2)
                    throw new FontPatchException("Nepodržan tip CFF obrisa.");
                _cid = Get(_top, 1230) != null;
                if (Get(_top, 1234) is List<double> cc && cc.Count > 0) _cidCount = (int)cc[0];

                var csOff = Get(_top, 17) ?? throw new FontPatchException("CFF bez obrisa znakova.");
                _cs = ReadIndex((int)csOff[0], out _);
                int n = _cs.Count;

                // charset
                int chOff = (int)(Get(_top, 15)?.FirstOrDefault() ?? 0);
                _charset = new List<int>(n + 16) { 0 };
                if (chOff == 0)
                {
                    for (int i = 1; i < n; i++) _charset.Add(i);
                }
                else if (chOff <= 2) throw new FontPatchException("Nepodržan CFF charset (Expert).");
                else
                {
                    int fmt = _d[chOff], q = chOff + 1;
                    if (fmt == 0)
                        for (int i = 1; i < n; i++) { _charset.Add(U16(_d, q)); q += 2; }
                    else
                    {
                        while (_charset.Count < n)
                        {
                            int first = U16(_d, q);
                            int left = fmt == 1 ? _d[q + 2] : U16(_d, q + 2);
                            q += fmt == 1 ? 3 : 4;
                            for (int k = 0; k <= left && _charset.Count < n; k++) _charset.Add(first + k);
                        }
                    }
                }

                // kodiranje (samo za ne-CID; unaprijed zadana ostaju kakva jesu)
                var enc = Get(_top, 16);
                if (!_cid && enc != null && enc[0] > 1)
                {
                    int eo = (int)enc[0];
                    int fmt = _d[eo], len;
                    if ((fmt & 0x7F) == 0) len = 2 + _d[eo + 1];
                    else len = 2 + 2 * _d[eo + 1];
                    if ((fmt & 0x80) != 0) len += 1 + 3 * _d[eo + len];
                    _encodingRaw = Slice(eo, len);
                }

                // privatni rječnici
                if (_cid)
                {
                    var fa = Get(_top, 1236) ?? throw new FontPatchException("CID font bez FDArray.");
                    foreach (var fdb in ReadIndex((int)fa[0], out _))
                    {
                        var fd = new Fd { FontDict = ParseDict(fdb) };
                        ReadPrivate(fd, Get(fd.FontDict, 18));
                        _fds.Add(fd);
                    }
                    var fs = Get(_top, 1237) ?? throw new FontPatchException("CID font bez FDSelect.");
                    int so = (int)fs[0];
                    _fdSelect = new List<int>(n + 16);
                    if (_d[so] == 0) for (int i = 0; i < n; i++) _fdSelect.Add(_d[so + 1 + i]);
                    else if (_d[so] == 3)
                    {
                        int nr = U16(_d, so + 1);
                        var arr = new int[n];
                        for (int r = 0; r < nr; r++)
                        {
                            int first = U16(_d, so + 3 + 3 * r), fd = _d[so + 5 + 3 * r];
                            int last = U16(_d, so + 6 + 3 * r);
                            for (int g = first; g < last && g < n; g++) arr[g] = fd;
                        }
                        _fdSelect.AddRange(arr);
                    }
                    else throw new FontPatchException("Nepodržan FDSelect format.");
                }
                else
                {
                    var fd = new Fd();
                    ReadPrivate(fd, Get(_top, 18));
                    _fds.Add(fd);
                    _fdSelect = Enumerable.Repeat(0, n).ToList();
                }
            }

            private void ReadPrivate(Fd fd, List<double> priv)
            {
                if (priv == null || priv.Count < 2) return;
                int size = (int)priv[0], off = (int)priv[1];
                if (size <= 0 || off + size > _d.Length) return;
                fd.Priv = ParseDict(Slice(off, size));
                fd.Default = Get(fd.Priv, 20)?.FirstOrDefault() ?? 0;
                fd.Nominal = Get(fd.Priv, 21)?.FirstOrDefault() ?? 0;
                var s = Get(fd.Priv, 19);
                if (s != null)
                {
                    int so = off + (int)s[0];
                    fd.Subrs = ReadIndex(so, out int end);
                    fd.SubrsRaw = Slice(so, end - so);
                }
            }

            private byte[] Slice(int o, int len)
            {
                if (o < 0 || len < 0 || o + len > _d.Length) throw new FontPatchException("Oštećena CFF tablica.");
                var b = new byte[len];
                Buffer.BlockCopy(_d, o, b, 0, len);
                return b;
            }

            private List<byte[]> ReadIndex(int p, out int end)
            {
                var list = new List<byte[]>();
                if (p + 2 > _d.Length) throw new FontPatchException("Oštećena CFF tablica.");
                int count = U16(_d, p);
                if (count == 0) { end = p + 2; return list; }
                int os = _d[p + 2];
                int OffAt(int i)
                {
                    int v = 0, q = p + 3 + i * os;
                    for (int k = 0; k < os; k++) v = v << 8 | _d[q + k];
                    return v;
                }
                int dataStart = p + 3 + (count + 1) * os - 1;
                for (int i = 0; i < count; i++)
                {
                    int a = OffAt(i), b = OffAt(i + 1);
                    list.Add(Slice(dataStart + a, b - a));
                }
                end = dataStart + OffAt(count);
                return list;
            }

            private static List<double> Get(List<Entry> dict, int op) => dict?.FirstOrDefault(e => e.Op == op)?.Args;

            private static List<Entry> ParseDict(byte[] b)
            {
                var list = new List<Entry>();
                var args = new List<double>();
                int p = 0, argStart = 0;
                while (p < b.Length)
                {
                    int b0 = b[p];
                    if (b0 <= 21)
                    {
                        int op = b0;
                        int opStart = p;
                        if (b0 == 12) { op = 1200 + b[p + 1]; p += 2; } else p++;
                        var raw = new byte[opStart - argStart];
                        Buffer.BlockCopy(b, argStart, raw, 0, raw.Length);
                        list.Add(new Entry { Op = op, Raw = raw, Args = args });
                        args = new List<double>();
                        argStart = p;
                    }
                    else if (b0 == 28) { args.Add((short)(b[p + 1] << 8 | b[p + 2])); p += 3; }
                    else if (b0 == 29) { args.Add(b[p + 1] << 24 | b[p + 2] << 16 | b[p + 3] << 8 | b[p + 4]); p += 5; }
                    else if (b0 == 30)
                    {
                        var sb = new StringBuilder();
                        p++;
                        bool done = false;
                        while (!done && p < b.Length)
                        {
                            int v = b[p++];
                            foreach (int nib in new[] { v >> 4, v & 15 })
                            {
                                if (nib <= 9) sb.Append((char)('0' + nib));
                                else if (nib == 10) sb.Append('.');
                                else if (nib == 11) sb.Append('E');
                                else if (nib == 12) sb.Append("E-");
                                else if (nib == 14) sb.Append('-');
                                else if (nib == 15) { done = true; break; }
                            }
                        }
                        double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var r);
                        args.Add(r);
                    }
                    else if (b0 >= 32 && b0 <= 246) { args.Add(b0 - 139); p++; }
                    else if (b0 >= 247 && b0 <= 250) { args.Add((b0 - 247) * 256 + b[p + 1] + 108); p += 2; }
                    else if (b0 >= 251 && b0 <= 254) { args.Add(-(b0 - 251) * 256 - b[p + 1] - 108); p += 2; }
                    else p++; // rezervirano
                }
                return list;
            }

            // ------------------------------------------------------------ tumač obrisa (Type 2)
            private static int Bias(int n) => n < 1240 ? 107 : n < 33900 ? 1131 : 32768;

            private class Ctx
            {
                public List<double> Stack = new List<double>();
                public double X, Y;
                public int Stems;
                public bool Width = true;   // širina je još moguća
                public bool Done;
                public List<Contour> Contours = new List<Contour>();
                public Contour Cur;
            }

            private List<Contour> Outlines(int gid)
            {
                if (_outlines.TryGetValue(gid, out var c)) return c;
                var ctx = new Ctx();
                try
                {
                    var fd = _fds[Math.Min(_fdSelect[gid], _fds.Count - 1)];
                    Run(_cs[gid], fd.Subrs, ctx, 0);
                }
                catch (FontPatchException) { throw; }
                catch { /* oštećen obris — vrati ono što je pročitano */ }
                c = ctx.Contours.Where(x => x.Segs.Count > 0).ToList();
                _outlines[gid] = c;
                return c;
            }

            private void Run(byte[] cs, List<byte[]> lsubrs, Ctx c, int depth)
            {
                if (depth > 12) return;
                var s = c.Stack;
                int p = 0;

                void MoveTo(double dx, double dy)
                {
                    c.X += dx; c.Y += dy;
                    c.Cur = new Contour { SX = c.X, SY = c.Y };
                    c.Contours.Add(c.Cur);
                }
                void LineTo(double dx, double dy)
                {
                    c.X += dx; c.Y += dy;
                    if (c.Cur == null) { c.Cur = new Contour { SX = 0, SY = 0 }; c.Contours.Add(c.Cur); }
                    c.Cur.Segs.Add(new Seg { X = c.X, Y = c.Y });
                }
                void CurveTo(double a, double b, double cc, double d, double e, double f)
                {
                    double x1 = c.X + a, y1 = c.Y + b, x2 = x1 + cc, y2 = y1 + d;
                    c.X = x2 + e; c.Y = y2 + f;
                    if (c.Cur == null) { c.Cur = new Contour { SX = 0, SY = 0 }; c.Contours.Add(c.Cur); }
                    c.Cur.Segs.Add(new Seg { Curve = true, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, X = c.X, Y = c.Y });
                }
                // ukloni širinu s početka stoga ako je višak argumenata
                void TakeWidth(bool odd)
                {
                    if (c.Width && odd && s.Count > 0) s.RemoveAt(0);
                    c.Width = false;
                }

                while (p < cs.Length && !c.Done)
                {
                    int b0 = cs[p];
                    if (b0 >= 32 || b0 == 28)
                    {
                        if (b0 == 28) { s.Add((short)(cs[p + 1] << 8 | cs[p + 2])); p += 3; }
                        else if (b0 <= 246) { s.Add(b0 - 139); p++; }
                        else if (b0 <= 250) { s.Add((b0 - 247) * 256 + cs[p + 1] + 108); p += 2; }
                        else if (b0 <= 254) { s.Add(-(b0 - 251) * 256 - cs[p + 1] - 108); p += 2; }
                        else { s.Add((cs[p + 1] << 24 | cs[p + 2] << 16 | cs[p + 3] << 8 | cs[p + 4]) / 65536.0); p += 5; }
                        continue;
                    }
                    p++;
                    int n = s.Count;
                    switch (b0)
                    {
                        case 1: case 3: case 18: case 23: // hstem, vstem, hstemhm, vstemhm
                            TakeWidth(n % 2 == 1);
                            c.Stems += s.Count / 2;
                            s.Clear();
                            break;
                        case 19: case 20: // hintmask, cntrmask
                            TakeWidth(n % 2 == 1);
                            c.Stems += s.Count / 2;
                            s.Clear();
                            p += (c.Stems + 7) / 8;
                            break;
                        case 21: // rmoveto
                            TakeWidth(n > 2);
                            MoveTo(s[0], s[1]); s.Clear();
                            break;
                        case 22: // hmoveto
                            TakeWidth(n > 1);
                            MoveTo(s[0], 0); s.Clear();
                            break;
                        case 4: // vmoveto
                            TakeWidth(n > 1);
                            MoveTo(0, s[0]); s.Clear();
                            break;
                        case 5: // rlineto
                            for (int i = 0; i + 1 < n; i += 2) LineTo(s[i], s[i + 1]);
                            s.Clear(); break;
                        case 6: case 7: // hlineto, vlineto
                            {
                                bool h = b0 == 6;
                                for (int i = 0; i < n; i++) { if (h) LineTo(s[i], 0); else LineTo(0, s[i]); h = !h; }
                                s.Clear(); break;
                            }
                        case 8: // rrcurveto
                            for (int i = 0; i + 5 < n; i += 6) CurveTo(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                            s.Clear(); break;
                        case 24: // rcurveline
                            {
                                int i = 0;
                                for (; i + 5 < n - 2; i += 6) CurveTo(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                                if (i + 1 < n) LineTo(s[i], s[i + 1]);
                                s.Clear(); break;
                            }
                        case 25: // rlinecurve
                            {
                                int i = 0;
                                for (; i + 1 < n - 6; i += 2) LineTo(s[i], s[i + 1]);
                                if (i + 5 < n) CurveTo(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                                s.Clear(); break;
                            }
                        case 26: // vvcurveto
                            {
                                int i = 0; double dx1 = 0;
                                if (n % 2 == 1) { dx1 = s[0]; i = 1; }
                                for (; i + 3 < n; i += 4) { CurveTo(dx1, s[i], s[i + 1], s[i + 2], 0, s[i + 3]); dx1 = 0; }
                                s.Clear(); break;
                            }
                        case 27: // hhcurveto
                            {
                                int i = 0; double dy1 = 0;
                                if (n % 2 == 1) { dy1 = s[0]; i = 1; }
                                for (; i + 3 < n; i += 4) { CurveTo(s[i], dy1, s[i + 1], s[i + 2], s[i + 3], 0); dy1 = 0; }
                                s.Clear(); break;
                            }
                        case 30: case 31: // vhcurveto, hvcurveto
                            {
                                bool h = b0 == 31;
                                for (int i = 0; i + 3 < n; i += 4)
                                {
                                    bool last = n - i == 5;
                                    if (h) CurveTo(s[i], 0, s[i + 1], s[i + 2], last ? s[i + 4] : 0, s[i + 3]);
                                    else CurveTo(0, s[i], s[i + 1], s[i + 2], s[i + 3], last ? s[i + 4] : 0);
                                    h = !h;
                                }
                                s.Clear(); break;
                            }
                        case 10: // callsubr
                            {
                                if (n == 0) return;
                                int idx = (int)s[n - 1] + Bias(lsubrs.Count);
                                s.RemoveAt(n - 1);
                                if (idx >= 0 && idx < lsubrs.Count) Run(lsubrs[idx], lsubrs, c, depth + 1);
                                break;
                            }
                        case 29: // callgsubr
                            {
                                if (n == 0) return;
                                int idx = (int)s[n - 1] + Bias(_gsubrs.Count);
                                s.RemoveAt(n - 1);
                                if (idx >= 0 && idx < _gsubrs.Count) Run(_gsubrs[idx], lsubrs, c, depth + 1);
                                break;
                            }
                        case 11: // return
                            return;
                        case 14: // endchar (seac se zanemaruje)
                            TakeWidth(n == 1 || n == 5);
                            s.Clear();
                            c.Done = true;
                            return;
                        case 12:
                            {
                                int b1 = cs[p++];
                                var a = s;
                                switch (b1)
                                {
                                    case 35: // flex
                                        if (n >= 12)
                                        {
                                            CurveTo(a[0], a[1], a[2], a[3], a[4], a[5]);
                                            CurveTo(a[6], a[7], a[8], a[9], a[10], a[11]);
                                        }
                                        break;
                                    case 34: // hflex
                                        if (n >= 7)
                                        {
                                            CurveTo(a[0], 0, a[1], a[2], a[3], 0);
                                            CurveTo(a[4], 0, a[5], -a[2], a[6], 0);
                                        }
                                        break;
                                    case 36: // hflex1
                                        if (n >= 9)
                                        {
                                            CurveTo(a[0], a[1], a[2], a[3], a[4], 0);
                                            CurveTo(a[5], 0, a[6], a[7], a[8], -(a[1] + a[3] + a[7]));
                                        }
                                        break;
                                    case 37: // flex1
                                        if (n >= 11)
                                        {
                                            double dx = a[0] + a[2] + a[4] + a[6] + a[8];
                                            double dy = a[1] + a[3] + a[5] + a[7] + a[9];
                                            CurveTo(a[0], a[1], a[2], a[3], a[4], a[5]);
                                            if (Math.Abs(dx) > Math.Abs(dy)) CurveTo(a[6], a[7], a[8], a[9], a[10], -dy);
                                            else CurveTo(a[6], a[7], a[8], a[9], -dx, a[10]);
                                        }
                                        break;
                                }
                                s.Clear();
                                break;
                            }
                        default:
                            s.Clear();
                            break;
                    }
                }
            }

            // ------------------------------------------------------------ za FontPatcher
            private static List<(double x, double y)> Flatten(Contour c)
            {
                var pts = new List<(double, double)> { (c.SX, c.SY) };
                double px = c.SX, py = c.SY;
                foreach (var s in c.Segs)
                {
                    if (!s.Curve) pts.Add((s.X, s.Y));
                    else
                        for (int k = 1; k <= 8; k++)
                        {
                            double t = k / 8.0, u = 1 - t;
                            pts.Add((u * u * u * px + 3 * u * u * t * s.X1 + 3 * u * t * t * s.X2 + t * t * t * s.X,
                                     u * u * u * py + 3 * u * u * t * s.Y1 + 3 * u * t * t * s.Y2 + t * t * t * s.Y));
                        }
                    px = s.X; py = s.Y;
                }
                return pts;
            }

            public List<List<(double x, double y)>> Poly(int gid)
            {
                if (gid < 0 || gid >= Count) return null;
                var o = Outlines(gid);
                return o.Count == 0 ? null : o.Select(Flatten).ToList();
            }

            public BBox Box(int gid)
            {
                var polys = Poly(gid);
                if (polys == null) return new BBox { Empty = true };
                var all = polys.SelectMany(x => x).ToList();
                return new BBox
                {
                    XMin = (int)Math.Floor(all.Min(q => q.x)),
                    XMax = (int)Math.Ceiling(all.Max(q => q.x)),
                    YMin = (int)Math.Floor(all.Min(q => q.y)),
                    YMax = (int)Math.Ceiling(all.Max(q => q.y))
                };
            }

            /// <summary>Doda glif spojen od transformiranih obrisa drugih glifova. Vraća novi gid.</summary>
            public int AddComposite(List<(int gid, int dx, int dy, double sx, double sy, bool metrics, double m01, double m10)> comps, int advance, string name,
                List<List<(double x, double y)>> extra = null)
            {
                int baseGid = comps[0].gid;
                var list = new List<Contour>();
                foreach (var cp in comps)
                {
                    foreach (var src in Outlines(cp.gid))
                    {
                        (double, double) T(double x, double y) => (x * cp.sx + y * cp.m10 + cp.dx, x * cp.m01 + y * cp.sy + cp.dy);
                        var (sx, sy) = T(src.SX, src.SY);
                        var nc = new Contour { SX = sx, SY = sy };
                        foreach (var s in src.Segs)
                        {
                            var (x, y) = T(s.X, s.Y);
                            var (x1, y1) = T(s.X1, s.Y1);
                            var (x2, y2) = T(s.X2, s.Y2);
                            nc.Segs.Add(new Seg { Curve = s.Curve, X = x, Y = y, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 });
                        }
                        list.Add(nc);
                    }
                }

                if (extra != null)
                    foreach (var poly in extra)
                    {
                        if (poly.Count < 3) continue;
                        var nc = new Contour { SX = poly[0].x, SY = poly[0].y };
                        for (int i = 1; i < poly.Count; i++) nc.Segs.Add(new Seg { X = poly[i].x, Y = poly[i].y });
                        list.Add(nc);
                    }
                return AddContours(list, _fdSelect[baseGid], advance, name);
            }

            /// <summary>Sve točke glifa po konturama: početak, pa za svaki odsječak kraj (ravni) ili c1, c2, kraj (krivulja).</summary>
            public List<List<(double x, double y)>> RawPoints(int gid)
            {
                if (gid < 0 || gid >= Count) return null;
                var o = Outlines(gid);
                if (o.Count == 0) return null;
                return o.Select(c =>
                {
                    var l = new List<(double, double)> { (c.SX, c.SY) };
                    foreach (var s in c.Segs)
                    {
                        if (s.Curve) { l.Add((s.X1, s.Y1)); l.Add((s.X2, s.Y2)); }
                        l.Add((s.X, s.Y));
                    }
                    return l;
                }).ToList();
            }

            /// <summary>Doda glif iste građe kao predložak, ali s novim položajima točaka (iz RawPoints).</summary>
            public int AddFromRaw(int template, List<List<(double x, double y)>> pts, int advance, string name)
            {
                var o = Outlines(template);
                var list = new List<Contour>();
                for (int ci = 0; ci < o.Count && ci < pts.Count; ci++)
                {
                    var p = pts[ci];
                    int k = 0;
                    var nc = new Contour { SX = p[k].x, SY = p[k].y };
                    k++;
                    foreach (var s in o[ci].Segs)
                    {
                        if (s.Curve)
                        {
                            nc.Segs.Add(new Seg { Curve = true, X1 = p[k].x, Y1 = p[k].y, X2 = p[k + 1].x, Y2 = p[k + 1].y, X = p[k + 2].x, Y = p[k + 2].y });
                            k += 3;
                        }
                        else { nc.Segs.Add(new Seg { X = p[k].x, Y = p[k].y }); k++; }
                    }
                    list.Add(nc);
                }
                return AddContours(list, _fdSelect[template], advance, name);
            }

            /// <summary>Doda glif od odabranih kontura postojećeg glifa (npr. samo akcent iz é).</summary>
            public int AddSubset(int gid, List<int> keep, int advance, string name)
            {
                var src = Outlines(gid);
                var list = keep.OrderBy(i => i).Where(i => i >= 0 && i < src.Count).Select(i => src[i]).ToList();
                return AddContours(list, _fdSelect[gid], advance, name);
            }

            private int AddContours(List<Contour> list, int fdIdx, int advance, string name)
            {
                var fd = _fds[Math.Min(fdIdx, _fds.Count - 1)];
                _cs.Add(Encode(list, advance - fd.Nominal));
                _fdSelect.Add(fdIdx);
                int gid = _cs.Count - 1;
                _outlines[gid] = list;
                if (_cid)
                {
                    int cid = _charset.Max() + 1;
                    _charset.Add(cid);
                    if (cid >= _cidCount) _cidCount = cid + 1;
                }
                else
                {
                    _strings.Add(Encoding.ASCII.GetBytes(name));
                    _charset.Add(391 + _strings.Count - 1);
                }
                return gid;
            }

            private static byte[] Encode(List<Contour> contours, double width)
            {
                var b = new List<byte>();
                void Num(double v)
                {
                    int i = (int)Math.Round(v);
                    if (i >= -107 && i <= 107) b.Add((byte)(i + 139));
                    else if (i >= 108 && i <= 1131) { i -= 108; b.Add((byte)((i >> 8) + 247)); b.Add((byte)(i & 0xFF)); }
                    else if (i >= -1131 && i <= -108) { i = -i - 108; b.Add((byte)((i >> 8) + 251)); b.Add((byte)(i & 0xFF)); }
                    else { i = Math.Max(-32768, Math.Min(32767, i)); b.Add(28); b.Add((byte)(i >> 8)); b.Add((byte)i); }
                }
                bool first = true;
                int cx = 0, cy = 0;
                int R(double v) => (int)Math.Round(v);
                foreach (var c in contours)
                {
                    int sx = R(c.SX), sy = R(c.SY);
                    if (first) { Num(width); first = false; }
                    Num(sx - cx); Num(sy - cy); b.Add(21);
                    cx = sx; cy = sy;
                    foreach (var s in c.Segs)
                    {
                        if (!s.Curve)
                        {
                            int x = R(s.X), y = R(s.Y);
                            Num(x - cx); Num(y - cy); b.Add(5);
                            cx = x; cy = y;
                        }
                        else
                        {
                            int x1 = R(s.X1), y1 = R(s.Y1), x2 = R(s.X2), y2 = R(s.Y2), x = R(s.X), y = R(s.Y);
                            Num(x1 - cx); Num(y1 - cy); Num(x2 - x1); Num(y2 - y1); Num(x - x2); Num(y - y2); b.Add(8);
                            cx = x; cy = y;
                        }
                    }
                }
                if (first) Num(width);
                b.Add(14);
                return b.ToArray();
            }

            // ------------------------------------------------------------ pisanje
            private static byte[] WriteIndex(List<byte[]> items)
            {
                var o = new List<byte>();
                o.Add((byte)(items.Count >> 8)); o.Add((byte)items.Count);
                if (items.Count == 0) return o.ToArray();
                long total = 1 + items.Sum(x => (long)x.Length);
                int os = total < 0x100 ? 1 : total < 0x10000 ? 2 : total < 0x1000000 ? 3 : 4;
                o.Add((byte)os);
                long off = 1;
                void W(long v) { for (int k = os - 1; k >= 0; k--) o.Add((byte)(v >> (8 * k))); }
                W(off);
                foreach (var it in items) { off += it.Length; W(off); }
                foreach (var it in items) o.AddRange(it);
                return o.ToArray();
            }

            private static void Int5(List<byte> o, int v)
            {
                o.Add(29); o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
            }

            private static void Op(List<byte> o, int op)
            {
                if (op >= 1200) { o.Add(12); o.Add((byte)(op - 1200)); } else o.Add((byte)op);
            }

            private static byte[] WriteDict(List<Entry> dict, Dictionary<int, int[]> over)
            {
                var o = new List<byte>();
                var done = new HashSet<int>();
                foreach (var e in dict)
                {
                    if (over.TryGetValue(e.Op, out var v))
                    {
                        if (v == null) continue; // izbaci
                        foreach (var x in v) Int5(o, x);
                        done.Add(e.Op);
                    }
                    else o.AddRange(e.Raw);
                    Op(o, e.Op);
                }
                foreach (var kv in over)
                {
                    if (done.Contains(kv.Key) || kv.Value == null) continue;
                    foreach (var x in kv.Value) Int5(o, x);
                    Op(o, kv.Key);
                }
                return o.ToArray();
            }

            private byte[] PrivateBlock(Fd fd, out int dictLen)
            {
                var over = new Dictionary<int, int[]>();
                byte[] dict;
                if (fd.SubrsRaw != null)
                {
                    over[19] = new[] { 0 };
                    int len = WriteDict(fd.Priv, over).Length;
                    over[19] = new[] { len };
                    dict = WriteDict(fd.Priv, over);
                }
                else
                {
                    over[19] = null;
                    dict = WriteDict(fd.Priv, over);
                }
                dictLen = dict.Length;
                return fd.SubrsRaw == null ? dict : dict.Concat(fd.SubrsRaw).ToArray();
            }

            public byte[] Build(string psName)
            {
                int n = _cs.Count;
                var names = _names;
                if (!string.IsNullOrEmpty(psName) && _names.Count == 1)
                    names = new List<byte[]> { Encoding.ASCII.GetBytes(psName) };
                var nameIdx = WriteIndex(names);
                var strIdx = WriteIndex(_strings);

                // charset (format 0)
                var charset = new List<byte> { 0 };
                for (int i = 1; i < n; i++) { charset.Add((byte)(_charset[i] >> 8)); charset.Add((byte)_charset[i]); }

                // FDSelect (format 3)
                byte[] fdsel = null;
                if (_cid)
                {
                    var r = new List<(int first, int fd)>();
                    for (int g = 0; g < n; g++)
                        if (r.Count == 0 || r[r.Count - 1].fd != _fdSelect[g]) r.Add((g, _fdSelect[g]));
                    var o = new List<byte> { 3, (byte)(r.Count >> 8), (byte)r.Count };
                    foreach (var x in r) { o.Add((byte)(x.first >> 8)); o.Add((byte)x.first); o.Add((byte)x.fd); }
                    o.Add((byte)(n >> 8)); o.Add((byte)n);
                    fdsel = o.ToArray();
                }

                var csIdx = WriteIndex(_cs);
                var privs = _fds.Select(fd => (block: PrivateBlock(fd, out int len), len)).ToList();

                Dictionary<int, int[]> TopOver(int chOff, int encOff, int csOff, int privOff, int fdArrOff, int fdSelOff)
                {
                    var ov = new Dictionary<int, int[]> { [15] = new[] { chOff }, [17] = new[] { csOff } };
                    if (_encodingRaw != null) ov[16] = new[] { encOff };
                    if (_cid)
                    {
                        ov[1236] = new[] { fdArrOff };
                        ov[1237] = new[] { fdSelOff };
                        ov[1234] = new[] { _cidCount };
                        ov[18] = null;
                    }
                    else ov[18] = new[] { privs[0].len, privOff };
                    return ov;
                }

                int topLen = WriteIndex(new List<byte[]> { WriteDict(_top, TopOver(0, 0, 0, 0, 0, 0)) }).Length;
                int pos = _header.Length + nameIdx.Length + topLen + strIdx.Length + _gsubrsRaw.Length;
                int chOff2 = pos; pos += charset.Count;
                int encOff2 = 0;
                if (_encodingRaw != null) { encOff2 = pos; pos += _encodingRaw.Length; }
                int fdSelOff2 = 0;
                if (fdsel != null) { fdSelOff2 = pos; pos += fdsel.Length; }
                int csOff2 = pos; pos += csIdx.Length;
                var privOffs = new List<int>();
                foreach (var pr in privs) { privOffs.Add(pos); pos += pr.block.Length; }
                byte[] fdArr = null;
                int fdArrOff2 = 0;
                if (_cid)
                {
                    fdArrOff2 = pos;
                    var dicts = new List<byte[]>();
                    for (int i = 0; i < _fds.Count; i++)
                        dicts.Add(WriteDict(_fds[i].FontDict, new Dictionary<int, int[]> { [18] = new[] { privs[i].len, privOffs[i] } }));
                    fdArr = WriteIndex(dicts);
                }

                var topIdx = WriteIndex(new List<byte[]> { WriteDict(_top, TopOver(chOff2, encOff2, csOff2, privOffs.FirstOrDefault(), fdArrOff2, fdSelOff2)) });
                if (topIdx.Length != topLen) throw new FontPatchException("Interna greška pri slaganju CFF tablice.");

                var outp = new List<byte>(pos + 64);
                outp.AddRange(_header); outp.AddRange(nameIdx); outp.AddRange(topIdx); outp.AddRange(strIdx); outp.AddRange(_gsubrsRaw);
                outp.AddRange(charset);
                if (_encodingRaw != null) outp.AddRange(_encodingRaw);
                if (fdsel != null) outp.AddRange(fdsel);
                outp.AddRange(csIdx);
                foreach (var pr in privs) outp.AddRange(pr.block);
                if (fdArr != null) outp.AddRange(fdArr);
                return outp.ToArray();
            }
        }
    }
}
