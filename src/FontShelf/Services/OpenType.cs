using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FontShelf.Services
{
    public class AxisInfo
    {
        public string Tag { get; set; }
        public string Name { get; set; }
        public double Min { get; set; }
        public double Default { get; set; }
        public double Max { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public ushort NameIdTemp { get; set; }
    }

    public class OpenTypeInfo
    {
        public List<string> Features { get; set; } = new List<string>();
        public List<AxisInfo> Axes { get; set; } = new List<AxisInfo>();
    }

    /// <summary>Osnovni podaci o jednom licu fonta (čitano izravno iz datoteke, bez WPF-a).</summary>
    public class FaceInfo
    {
        public string Family { get; set; }      // nameID 16 ili 1
        public string Legacy { get; set; }      // nameID 1
        public string Face { get; set; }        // nameID 17 ili 2
        public int Weight { get; set; } = 400;
        public int Italic { get; set; }         // 0 normal, 1 italic, 2 oblique
        public int Width { get; set; } = 5;
    }

    /// <summary>Minimalni čitač OpenType tablica (GSUB/GPOS značajke, fvar osi, name).</summary>
    public static class OpenTypeReader
    {
        private class BE
        {
            private readonly BinaryReader _r;
            public BE(Stream s) { _r = new BinaryReader(s); }
            public long Pos { get => _r.BaseStream.Position; set => _r.BaseStream.Position = value; }
            public long Length => _r.BaseStream.Length;
            public ushort U16() { var b = _r.ReadBytes(2); return (ushort)(b[0] << 8 | b[1]); }
            public short I16() => (short)U16();
            public uint U32() { var b = _r.ReadBytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
            public int I32() => (int)U32();
            public string Tag() => Encoding.ASCII.GetString(_r.ReadBytes(4));
            public byte[] Bytes(int n) => _r.ReadBytes(n);
        }

        private static List<long> FaceOffsets(BE r)
        {
            r.Pos = 0;
            var tag = r.Tag();
            if (tag == "ttcf")
            {
                r.U32(); // verzija
                var n = r.U32();
                var list = new List<long>();
                for (int i = 0; i < n && i < 64; i++) list.Add(r.U32());
                return list;
            }
            return new List<long> { 0 };
        }

        private static Dictionary<string, (uint off, uint len)> Tables(BE r, long face)
        {
            var d = new Dictionary<string, (uint, uint)>();
            r.Pos = face + 4;
            var num = r.U16();
            r.Pos = face + 12;
            for (int i = 0; i < num; i++)
            {
                var tag = r.Tag();
                r.U32();
                var off = r.U32();
                var len = r.U32();
                d[tag] = (off, len);
            }
            return d;
        }

        /// <summary>Brzo čita imena i stil svih lica u datoteci (za popis instaliranih fontova).</summary>
        public static List<FaceInfo> ReadFaces(string file)
        {
            var result = new List<FaceInfo>();
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
                var r = new BE(fs);
                foreach (var face in FaceOffsets(r))
                {
                    var t = Tables(r, face);
                    if (!t.TryGetValue("name", out var nt)) continue;
                    var names = ReadNames(r, nt.off, new HashSet<ushort> { 1, 2, 16, 17 });
                    names.TryGetValue(1, out var n1);
                    names.TryGetValue(2, out var n2);
                    names.TryGetValue(16, out var n16);
                    names.TryGetValue(17, out var n17);
                    var fi = new FaceInfo
                    {
                        Family = (string.IsNullOrWhiteSpace(n16) ? n1 : n16)?.Trim(),
                        Legacy = n1?.Trim(),
                        Face = (string.IsNullOrWhiteSpace(n17) ? n2 : n17)?.Trim()
                    };
                    if (string.IsNullOrWhiteSpace(fi.Family)) continue;
                    if (t.TryGetValue("OS/2", out var os2) && os2.len >= 64)
                    {
                        r.Pos = os2.off + 4;
                        int w = r.U16();
                        int wd = r.U16();
                        r.Pos = os2.off + 62;
                        int sel = r.U16();
                        if (w > 0 && w < 10) w *= 100;
                        fi.Weight = Math.Max(1, Math.Min(999, w == 0 ? 400 : w));
                        fi.Width = wd >= 1 && wd <= 9 ? wd : 5;
                        fi.Italic = (sel & 1) != 0 ? 1 : (sel & 0x200) != 0 ? 2 : 0;
                    }
                    if (string.IsNullOrWhiteSpace(fi.Face)) fi.Face = "Regular";
                    if (string.IsNullOrWhiteSpace(n16)) SplitStyleWords(fi);
                    result.Add(fi);
                }
            }
            catch { }
            return result;
        }

        // Riječi koje označavaju stil, a ne obitelj (kad font nema ispravno ime obitelji, nameID 16)
        private static readonly string[] StyleWords =
        {
            "Hairline", "Thin", "ExtraLight", "UltraLight", "Extra Light", "Ultra Light", "Light", "SemiLight", "Semi Light",
            "Book", "Regular", "Normal", "Medium", "SemiBold", "DemiBold", "Semi Bold", "Demi Bold", "Bold",
            "ExtraBold", "UltraBold", "Extra Bold", "Ultra Bold", "Black", "Heavy", "ExtraBlack", "UltraBlack", "Extra Black",
            "Condensed", "SemiCondensed", "ExtraCondensed", "UltraCondensed", "Semi Condensed", "Extra Condensed", "Cond",
            "Narrow", "Compressed", "Expanded", "SemiExpanded", "ExtraExpanded", "Extended", "Wide",
            "Italic", "Oblique", "Slanted"
        };

        /// <summary>"Abhaya Libre ExtraBold" + "Regular" → obitelj "Abhaya Libre", lice "ExtraBold".</summary>
        private static void SplitStyleWords(FaceInfo fi)
        {
            var family = fi.Family;
            var stripped = new List<string>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var w in StyleWords.OrderByDescending(x => x.Length))
                {
                    if (family.Length > w.Length + 1 && family.EndsWith(" " + w, StringComparison.OrdinalIgnoreCase))
                    {
                        stripped.Insert(0, family.Substring(family.Length - w.Length));
                        family = family.Substring(0, family.Length - w.Length - 1).TrimEnd(' ', '-');
                        changed = true;
                        break;
                    }
                }
            }
            if (stripped.Count == 0 || string.IsNullOrWhiteSpace(family)) return;
            fi.Family = family;
            var face = fi.Face;
            var parts = new List<string>(stripped);
            if (!string.Equals(face, "Regular", StringComparison.OrdinalIgnoreCase)
                && !parts.Any(p => string.Equals(p, face, StringComparison.OrdinalIgnoreCase)))
                parts.Add(face);
            fi.Face = string.Join(" ", parts);
        }

        public static bool HasVariations(string file)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var r = new BE(fs);
                return FaceOffsets(r).Any(f => Tables(r, f).ContainsKey("fvar"));
            }
            catch { return false; }
        }

        public static OpenTypeInfo Read(string file)
        {
            var info = new OpenTypeInfo();
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var r = new BE(fs);
                var features = new HashSet<string>();
                foreach (var face in FaceOffsets(r))
                {
                    var t = Tables(r, face);
                    foreach (var name in new[] { "GSUB", "GPOS" })
                        if (t.TryGetValue(name, out var tb)) ReadFeatures(r, tb.off, features);
                    if (info.Axes.Count == 0 && t.TryGetValue("fvar", out var fv))
                        info.Axes = ReadAxes(r, fv.off, t.TryGetValue("name", out var nm) ? nm.off : 0);
                }
                info.Features = features.OrderBy(f => f).ToList();
            }
            catch { }
            return info;
        }

        private static void ReadFeatures(BE r, uint table, HashSet<string> into)
        {
            r.Pos = table + 6; // major, minor, scriptList
            var featureList = r.U16();
            long fl = table + featureList;
            r.Pos = fl;
            var count = r.U16();
            for (int i = 0; i < count && i < 2000; i++)
            {
                r.Pos = fl + 2 + i * 6;
                var tag = r.Tag();
                if (tag.All(c => c >= 32 && c < 127)) into.Add(tag.TrimEnd());
            }
        }

        private static List<AxisInfo> ReadAxes(BE r, uint table, uint nameTable)
        {
            var axes = new List<AxisInfo>();
            r.Pos = table + 4;
            var axesOffset = r.U16();
            r.U16();
            var axisCount = r.U16();
            var axisSize = r.U16();
            for (int i = 0; i < axisCount && i < 64; i++)
            {
                r.Pos = table + axesOffset + i * axisSize;
                var tag = r.Tag();
                double min = r.I32() / 65536.0, def = r.I32() / 65536.0, max = r.I32() / 65536.0;
                r.U16();
                var nameId = r.U16();
                axes.Add(new AxisInfo { Tag = tag, Min = min, Default = def, Max = max, Name = AxisFallbackName(tag), NameIdTemp = nameId });
            }
            if (nameTable != 0)
            {
                var names = ReadNames(r, nameTable, axes.Select(a => a.NameIdTemp).ToHashSet());
                foreach (var a in axes)
                    if (!KnownAxes.ContainsKey(a.Tag) && names.TryGetValue(a.NameIdTemp, out var n) && !string.IsNullOrWhiteSpace(n))
                        a.Name = n;
            }
            return axes;
        }

        private static Dictionary<ushort, string> ReadNames(BE r, uint table, HashSet<ushort> wanted)
        {
            var result = new Dictionary<ushort, string>();
            try
            {
                r.Pos = table;
                r.U16();
                var count = r.U16();
                var strOffset = r.U16();
                var recs = new List<(ushort plat, ushort enc, ushort lang, ushort id, ushort len, ushort off)>();
                for (int i = 0; i < count; i++)
                    recs.Add((r.U16(), r.U16(), r.U16(), r.U16(), r.U16(), r.U16()));
                foreach (var id in wanted)
                {
                    var rec = recs.Where(x => x.id == id && x.plat == 3).OrderBy(x => x.lang == 0x409 ? 0 : 1).FirstOrDefault();
                    if (rec.len > 0)
                    {
                        r.Pos = table + strOffset + rec.off;
                        result[id] = Encoding.BigEndianUnicode.GetString(r.Bytes(rec.len));
                        continue;
                    }
                    var mac = recs.FirstOrDefault(x => x.id == id && x.plat == 1);
                    if (mac.len > 0)
                    {
                        r.Pos = table + strOffset + mac.off;
                        result[id] = Encoding.ASCII.GetString(r.Bytes(mac.len));
                    }
                }
            }
            catch { }
            return result;
        }

        private static readonly Dictionary<string, string> KnownAxes = new Dictionary<string, string>
        {
            ["wght"] = "Debljina",
            ["wdth"] = "Širina",
            ["ital"] = "Kurziv",
            ["slnt"] = "Nagib",
            ["opsz"] = "Optička veličina",
            ["GRAD"] = "Grade",
        };

        private static string AxisFallbackName(string tag) => KnownAxes.TryGetValue(tag, out var n) ? n : tag;

        // Značajke koje imaju smisla za korisnika (ostale su interne, npr. ccmp, mark, locl)
        public static readonly Dictionary<string, string> FeatureNames = new Dictionary<string, string>
        {
            ["kern"] = "Kerning",
            ["liga"] = "Standardne ligature",
            ["dlig"] = "Dodatne ligature",
            ["hlig"] = "Povijesne ligature",
            ["calt"] = "Kontekstualne alternative",
            ["salt"] = "Stilske alternative",
            ["swsh"] = "Ukrasni potezi (swash)",
            ["cswh"] = "Kontekstualni swash",
            ["hist"] = "Povijesni oblici",
            ["smcp"] = "Mala velika slova",
            ["c2sc"] = "Velika → mala velika",
            ["pcap"] = "Petite caps",
            ["c2pc"] = "Velika → petite caps",
            ["unic"] = "Unicase",
            ["titl"] = "Naslovni oblici",
            ["case"] = "Oblici za velika slova",
            ["cpsp"] = "Razmak za velika slova",
            ["onum"] = "Stare (oldstyle) brojke",
            ["lnum"] = "Linijske brojke",
            ["tnum"] = "Tablične brojke",
            ["pnum"] = "Proporcionalne brojke",
            ["zero"] = "Precrtana nula",
            ["frac"] = "Razlomci",
            ["afrc"] = "Okomiti razlomci",
            ["sups"] = "Eksponent",
            ["subs"] = "Indeks",
            ["sinf"] = "Znanstveni indeks",
            ["numr"] = "Brojnik",
            ["dnom"] = "Nazivnik",
            ["ordn"] = "Redni brojevi",
            ["ornm"] = "Ornamenti",
            ["aalt"] = "Sve alternative",
        };

        public static bool IsUserFeature(string tag)
            => FeatureNames.ContainsKey(tag)
               || (tag.Length == 4 && tag.StartsWith("ss") && char.IsDigit(tag[2]) && char.IsDigit(tag[3]))
               || (tag.Length == 4 && tag.StartsWith("cv") && char.IsDigit(tag[2]) && char.IsDigit(tag[3]));

        public static string FeatureLabel(string tag)
        {
            if (FeatureNames.TryGetValue(tag, out var n)) return n;
            if (tag.StartsWith("ss")) return "Stilski set " + int.Parse(tag.Substring(2));
            if (tag.StartsWith("cv")) return "Varijanta znakova " + int.Parse(tag.Substring(2));
            return tag;
        }

        public static bool DefaultOn(string tag) => tag == "kern" || tag == "liga" || tag == "calt";
    }
}
