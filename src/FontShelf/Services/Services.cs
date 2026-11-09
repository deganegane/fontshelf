using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FontShelf.Models;
using Microsoft.Win32;

namespace FontShelf.Services
{
    public static class AppPaths
    {
        public static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FontShelf");
        public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FontShelf");
    }

    public static class LibraryStore
    {
        private static string FilePath => Path.Combine(AppPaths.Roaming, "library.json");

        public static LibraryData Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(FilePath));
                    if (data != null)
                    {
                        data.Favorites ??= new List<string>();
                        data.Tags ??= new Dictionary<string, List<string>>();
                        data.Folders ??= new List<string>();
                        data.ActivatedFiles ??= new List<string>();
                        if (data.PreviewSize < 8 || data.PreviewSize > 200) data.PreviewSize = 40;
                        return data;
                    }
                }
            }
            catch { /* oštećena datoteka – krećemo ispočetka */ }
            return new LibraryData();
        }

        public static void Save(LibraryData data)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Roaming);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, true);
            }
            catch { }
        }
    }

    public static class FontLoader
    {
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".ttf", ".otf", ".ttc", ".otc" };

        private static readonly XmlLanguage EnUs = XmlLanguage.GetLanguage("en-us");

        public static bool IsFontFile(string path) => Extensions.Contains(Path.GetExtension(path) ?? "");

        public static string UserFontsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

        public static string GetName(FontFamily fam)
        {
            try
            {
                if (fam.FamilyNames.TryGetValue(EnUs, out var n) && !string.IsNullOrWhiteSpace(n)) return n;
                var first = fam.FamilyNames.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (first != null) return first;
            }
            catch { }
            var src = fam.Source ?? "";
            var hash = src.LastIndexOf('#');
            return hash >= 0 ? src.Substring(hash + 1) : src;
        }

        public static string FaceName(Typeface tf)
        {
            try
            {
                if (tf.FaceNames.TryGetValue(EnUs, out var n) && !string.IsNullOrWhiteSpace(n)) return n;
                var first = tf.FaceNames.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (first != null) return first;
            }
            catch { }
            return $"{tf.Weight} {tf.Style}";
        }

        private static FontItem Create(FontFamily fam, string name, List<string> files, string folder, string key,
            Dictionary<string, string> styleFiles = null)
        {
            var styles = new List<FontStyleItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var tf in fam.GetTypefaces())
                {
                    var sn = FaceName(tf);
                    if (!seen.Add(sn)) continue;
                    string file = null;
                    if (files != null)
                    {
                        if (styleFiles != null && styleFiles.TryGetValue(sn, out var sf)) file = sf;
                        else if (files.Count == 1) file = files[0];
                    }
                    styles.Add(new FontStyleItem
                    {
                        Name = sn,
                        Family = fam,
                        Weight = tf.Weight,
                        Style = tf.Style,
                        Stretch = tf.Stretch,
                        FilePath = file
                    });
                }
            }
            catch { }

            if (styles.Count == 0) return null;

            styles = styles
                .OrderBy(s => s.Stretch.ToOpenTypeStretch() == 5 ? 0 : 1)
                .ThenBy(s => s.Stretch.ToOpenTypeStretch())
                .ThenBy(s => s.Weight.ToOpenTypeWeight())
                .ThenBy(s => s.Style == FontStyles.Normal ? 0 : 1)
                .ToList();

            return new FontItem
            {
                Key = key,
                Name = name,
                Family = fam,
                Styles = styles,
                FilePaths = files,
                FolderPath = folder,
                MainFile = files?.FirstOrDefault()
            };
        }

        private static List<FontFamily> SafeFamilies(string file)
        {
            try { return Fonts.GetFontFamilies(new Uri(file)).ToList(); }
            catch { return new List<FontFamily>(); }
        }

        // ---- Keš stilova instaliranih fontova (čitanje stilova je najsporiji dio pokretanja) ----
        public class StyleInfo
        {
            public string N { get; set; }
            public int W { get; set; }
            public int I { get; set; }
            public int S { get; set; }
        }

        public class SystemCache
        {
            public Dictionary<string, List<StyleInfo>> Families { get; set; } = new Dictionary<string, List<StyleInfo>>();
            /// <summary>Datoteka korisničkog fonta (putanja|vrijeme) → imena obitelji u njoj.</summary>
            public Dictionary<string, List<string>> UserFiles { get; set; } = new Dictionary<string, List<string>>();
        }

        private static string SystemCachePath => Path.Combine(AppPaths.Local, "system-fonts-cache-v1.json");

        private static SystemCache LoadSystemCache()
        {
            try
            {
                if (File.Exists(SystemCachePath))
                    return JsonSerializer.Deserialize<SystemCache>(File.ReadAllText(SystemCachePath)) ?? new SystemCache();
            }
            catch { }
            return new SystemCache();
        }

        private static void SaveSystemCache(SystemCache c)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Local);
                var tmp = SystemCachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(c));
                File.Move(tmp, SystemCachePath, true);
            }
            catch { }
        }

        private static FontItem FromCache(FontFamily fam, string name, List<StyleInfo> info)
        {
            var styles = info.Select(i => new FontStyleItem
            {
                Name = i.N,
                Family = fam,
                Weight = FontWeight.FromOpenTypeWeight(Math.Max(1, Math.Min(999, i.W))),
                Style = i.I == 1 ? FontStyles.Italic : i.I == 2 ? FontStyles.Oblique : FontStyles.Normal,
                Stretch = FontStretch.FromOpenTypeStretch(Math.Max(1, Math.Min(9, i.S)))
            }).ToList();
            if (styles.Count == 0) return null;
            return new FontItem { Key = "sys:" + name, Name = name, Family = fam, Styles = styles };
        }

        private static List<StyleInfo> ToCache(FontItem item) => item.Styles.Select(s => new StyleInfo
        {
            N = s.Name,
            W = s.Weight.ToOpenTypeWeight(),
            I = s.Style == FontStyles.Italic ? 1 : s.Style == FontStyles.Oblique ? 2 : 0,
            S = s.Stretch.ToOpenTypeStretch()
        }).ToList();

        // ---------- Brzo učitavanje instaliranih fontova (registar + vlastiti čitač, keš po datoteci) ----------
        public class FileFaces
        {
            public string Stamp { get; set; }
            public List<FaceInfo> Faces { get; set; }
        }

        private static string FastCachePath => Path.Combine(AppPaths.Local, "installed-fonts-cache-v1.json");

        private static IEnumerable<string> RegisteredFontFiles()
        {
            var winFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                RegistryKey key = null;
                try { key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"); } catch { }
                if (key == null) continue;
                using (key)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        if (!(key.GetValue(name) is string v) || string.IsNullOrWhiteSpace(v)) continue;
                        var path = Path.IsPathRooted(v) ? v : Path.Combine(winFonts, v);
                        if (IsFontFile(path) && seen.Add(path)) yield return path;
                    }
                }
            }
        }

        private static FontFamily FileFamily(string file, IEnumerable<string> names)
        {
            var uri = new Uri(file).AbsoluteUri;
            var src = string.Join(", ", names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()
                .Select(n => uri + "#" + n.Replace(",", "")));
            return new FontFamily(src);
        }

        /// <summary>Brzi put: bez Fonts.SystemFontFamilies (koji kod tisuća fontova traje desetke sekundi).</summary>
        public static List<FontItem> LoadSystemFast()
        {
            Dictionary<string, FileFaces> cache;
            try
            {
                cache = File.Exists(FastCachePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, FileFaces>>(File.ReadAllText(FastCachePath))
                    : null;
            }
            catch { cache = null; }
            cache ??= new Dictionary<string, FileFaces>(StringComparer.OrdinalIgnoreCase);
            var fresh = new Dictionary<string, FileFaces>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;

            // obitelj → (datoteka, lice)
            var groups = new Dictionary<string, List<(string file, FaceInfo face)>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var file in RegisteredFontFiles())
            {
                string stamp;
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists) continue;
                    stamp = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                }
                catch { continue; }

                List<FaceInfo> faces;
                if (cache.TryGetValue(file, out var c) && c.Stamp == stamp && c.Faces != null) faces = c.Faces;
                else
                {
                    faces = OpenTypeReader.ReadFaces(file);
                    changed = true;
                }
                fresh[file] = new FileFaces { Stamp = stamp, Faces = faces };

                foreach (var f in faces)
                {
                    if (!groups.TryGetValue(f.Family, out var l))
                    {
                        groups[f.Family] = l = new List<(string, FaceInfo)>();
                        order.Add(f.Family);
                    }
                    l.Add((file, f));
                }
            }
            if (cache.Count != fresh.Count) changed = true;
            if (changed)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.Local);
                    File.WriteAllText(FastCachePath, JsonSerializer.Serialize(fresh));
                }
                catch { }
            }

            var list = new List<FontItem>();
            foreach (var name in order)
            {
                var faces = groups[name];
                var regular = faces.OrderBy(x => x.face.Italic != 0 ? 1 : 0)
                                   .ThenBy(x => Math.Abs(x.face.Width - 5))
                                   .ThenBy(x => Math.Abs(x.face.Weight - 400)).First();
                var family = FileFamily(regular.file, new[] { name, regular.face.Legacy });
                var styles = new List<FontStyleItem>();
                var seenFaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (file, face) in faces)
                {
                    if (!seenFaces.Add(face.Face)) continue;
                    styles.Add(new FontStyleItem
                    {
                        Name = face.Face,
                        Family = file == regular.file ? family : FileFamily(file, new[] { name, face.Legacy }),
                        Weight = FontWeight.FromOpenTypeWeight(face.Weight),
                        Style = face.Italic == 1 ? FontStyles.Italic : face.Italic == 2 ? FontStyles.Oblique : FontStyles.Normal,
                        Stretch = FontStretch.FromOpenTypeStretch(face.Width),
                        SourceFile = file
                    });
                }
                styles = styles
                    .OrderBy(s => s.Stretch.ToOpenTypeStretch() == 5 ? 0 : 1)
                    .ThenBy(s => s.Stretch.ToOpenTypeStretch())
                    .ThenBy(s => s.Weight.ToOpenTypeWeight())
                    .ThenBy(s => s.Style == FontStyles.Normal ? 0 : 1)
                    .ToList();
                list.Add(new FontItem
                {
                    Key = "sys:" + name,
                    Name = name,
                    Family = family,
                    Styles = styles,
                    MainFile = regular.file,
                    SystemFiles = faces.Select(x => x.file).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                });
            }
            return list;
        }

        public static List<FontItem> LoadSystem()
        {
            try
            {
                var fast = LoadSystemFast();
                if (fast.Count >= 20) return fast;
            }
            catch { }
            return LoadSystemSlow();
        }

        public static List<FontItem> LoadSystemSlow()
        {
            var list = new List<FontItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cache = LoadSystemCache();
            var fresh = new SystemCache();
            bool changed = false;

            foreach (var fam in Fonts.SystemFontFamilies)
            {
                var name = GetName(fam);
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                FontItem item;
                if (cache.Families.TryGetValue(name, out var info) && info.Count > 0)
                    item = FromCache(fam, name, info);
                else
                {
                    item = Create(fam, name, null, null, "sys:" + name);
                    changed = true;
                }
                if (item == null) continue;
                fresh.Families[name] = cache.Families.TryGetValue(name, out var keep) && keep.Count > 0 ? keep : ToCache(item);
                list.Add(item);
            }
            if (cache.Families.Keys.Any(k => !fresh.Families.ContainsKey(k))) changed = true;

            // Fontovi instalirani samo za korisnika (Windows 10 1809+)
            try
            {
                if (Directory.Exists(UserFontsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(UserFontsDir).Where(IsFontFile))
                    {
                        string stamp;
                        try { stamp = file + "|" + File.GetLastWriteTimeUtc(file).Ticks; } catch { continue; }

                        // Ako znamo imena u datoteci i sva su već učitana, ne otvaramo je
                        if (cache.UserFiles.TryGetValue(stamp, out var names) && names.All(seen.Contains))
                        {
                            fresh.UserFiles[stamp] = names;
                            continue;
                        }

                        var found = new List<string>();
                        foreach (var fam in SafeFamilies(file))
                        {
                            var name = GetName(fam);
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            found.Add(name);
                            if (!seen.Add(name)) continue;
                            var item = Create(fam, name, null, null, "sys:" + name);
                            if (item != null) { item.MainFile = file; list.Add(item); }
                        }
                        fresh.UserFiles[stamp] = found;
                        changed = true;
                    }
                }
            }
            catch { }

            if (changed) SaveSystemCache(fresh);
            return list;
        }

        public static List<FontItem> LoadFolder(string folder)
        {
            var list = new List<FontItem>();
            if (!Directory.Exists(folder)) return list;

            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                }).Where(IsFontFile).ToList();
            }
            catch { return list; }

            // Grupiraj datoteke po (podmapa, obitelj) da npr. Roboto-Regular i Roboto-Bold budu jedan font
            var groups = new Dictionary<(string dir, string name), (List<string> files, Dictionary<string, string> styles)>();
            var order = new List<(string dir, string name)>();
            foreach (var file in files)
            {
                var dir = Path.GetDirectoryName(file);
                foreach (var fam in SafeFamilies(file))
                {
                    var name = GetName(fam);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var k = (dir, name);
                    if (!groups.TryGetValue(k, out var g))
                    {
                        g = (new List<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                        groups[k] = g;
                        order.Add(k);
                    }
                    if (!g.files.Contains(file)) g.files.Add(file);
                    try
                    {
                        foreach (var tf in fam.GetTypefaces())
                        {
                            var sn = FaceName(tf);
                            if (!g.styles.ContainsKey(sn)) g.styles[sn] = file;
                        }
                    }
                    catch { }
                }
            }

            foreach (var k in order)
            {
                var g = groups[k];
                var key = "file:" + k.dir + "|" + k.name;
                FontItem item = null;
                try
                {
                    var baseUri = new Uri(k.dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
                    var fam = new FontFamily(baseUri, "./#" + k.name);
                    item = Create(fam, k.name, g.files, folder, key, g.styles);
                }
                catch { }

                if (item == null)
                {
                    var fam = SafeFamilies(g.files[0]).FirstOrDefault(f => GetName(f) == k.name);
                    if (fam != null) item = Create(fam, k.name, g.files, folder, key, g.styles);
                }

                if (item != null) list.Add(item);
            }

            return list;
        }
    }

    public class AnalysisResult
    {
        public bool? Mono;
        public bool? Croatian;
        public string Version = "";
        public string Designer = "";
        public string Manufacturer = "";
        public string MainFile;
        public bool IsVariable;
        public double Contrast = double.NaN;
        public double Weight = double.NaN;
        public double Width = double.NaN;
        public double XHeight = double.NaN;
        public int Serif = -1;
    }

    /// <summary>Spremljeni rezultati analize da se fontovi ne analiziraju pri svakom pokretanju.</summary>
    public static class AnalysisCache
    {
        public class Entry
        {
            public string Stamp { get; set; }
            public AnalysisResult Result { get; set; }
        }

        private const int Version = 2;
        private static string FilePath => Path.Combine(AppPaths.Local, $"analysis-cache-v{Version}.json");

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            IncludeFields = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static Dictionary<string, Entry> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath), Options)
                           ?? new Dictionary<string, Entry>();
            }
            catch { }
            return new Dictionary<string, Entry>();
        }

        public static void Save(Dictionary<string, Entry> data)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Local);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
                File.Move(tmp, FilePath, true);
            }
            catch { }
        }

        /// <summary>Oznaka verzije fonta – ako se promijeni, font se ponovno analizira.</summary>
        public static string StampOf(FontItem f)
        {
            try
            {
                var file = f.FirstFile ?? f.MainFile;
                if (file != null && File.Exists(file))
                {
                    var fi = new FileInfo(file);
                    return $"{f.StyleCount}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                }
            }
            catch { }
            return $"{f.StyleCount}";
        }
    }

    public static class FontAnalyzer
    {
        public const int MaxGlyphs = 1500;

        public static GlyphTypeface GetGlyphTypeface(FontItem f, FontStyleItem style = null)
        {
            try
            {
                var tfs = f.Family.GetTypefaces().ToList();
                Typeface pick = null;
                if (style != null)
                    pick = tfs.FirstOrDefault(t => t.Weight == style.Weight && t.Style == style.Style && t.Stretch == style.Stretch);
                pick ??= tfs.FirstOrDefault(t => t.Weight == FontWeights.Normal && t.Style == FontStyles.Normal
                                                 && t.Stretch == FontStretches.Normal)
                         ?? tfs.FirstOrDefault(t => t.Weight == FontWeights.Normal && t.Style == FontStyles.Normal)
                         ?? tfs.FirstOrDefault();
                if (pick != null && pick.TryGetGlyphTypeface(out var g)) return g;
                foreach (var t in tfs)
                    if (t.TryGetGlyphTypeface(out var g2)) return g2;
            }
            catch { }
            return null;
        }

        private static string Pick(IDictionary<CultureInfo, string> d)
        {
            try
            {
                if (d == null || d.Count == 0) return "";
                var en = d.FirstOrDefault(kv => kv.Key.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Value;
                var v = en ?? d.Values.FirstOrDefault() ?? "";
                return v.Replace("\r", " ").Replace("\n", " ").Trim();
            }
            catch { return ""; }
        }

        public static string FileOf(GlyphTypeface g)
        {
            try { return g.FontUri != null && g.FontUri.IsFile ? g.FontUri.LocalPath : null; }
            catch { return null; }
        }

        /// <summary>Vraća putanju s pravim velikim/malim slovima (WPF je vraća velikim slovima).</summary>
        public static string RealCase(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return path;
                var parts = new Stack<string>();
                var dir = fi.Directory;
                parts.Push(dir.GetFiles(fi.Name).FirstOrDefault()?.Name ?? fi.Name);
                while (dir.Parent != null)
                {
                    parts.Push(dir.Parent.GetDirectories(dir.Name).FirstOrDefault()?.Name ?? dir.Name);
                    dir = dir.Parent;
                }
                parts.Push(dir.Name.ToUpperInvariant());
                return Path.Combine(parts.ToArray());
            }
            catch { return path; }
        }

        public static AnalysisResult Analyze(FontItem f)
        {
            var res = new AnalysisResult();
            try
            {
                var g = GetGlyphTypeface(f);
                if (g == null) return res;
                var map = g.CharacterToGlyphMap;
                bool mono = false;
                if (map.TryGetValue('i', out var gi) && map.TryGetValue('W', out var gw) && map.TryGetValue('m', out var gm))
                {
                    var a = g.AdvanceWidths;
                    mono = Math.Abs(a[gi] - a[gw]) < 0.001 && Math.Abs(a[gi] - a[gm]) < 0.001;
                }
                res.Mono = mono;
                res.Croatian = "čćđšžČĆĐŠŽ".All(c => map.ContainsKey(c));
                res.Version = Pick(g.VersionStrings).Replace("Version ", "");
                res.Designer = Pick(g.DesignerNames);
                res.Manufacturer = Pick(g.ManufacturerNames);
                res.MainFile = f.MainFile ?? RealCase(FileOf(g));
                if (res.MainFile != null) res.IsVariable = OpenTypeReader.HasVariations(res.MainFile);
                try { Measure(g, res); } catch { }
            }
            catch { }
            return res;
        }

        /// <summary>Duljina prvog "punog" dijela na liniji od a do b (uzorkovanjem oblika).</summary>
        private static double FirstRun(Geometry geo, Point a, Point b, int n = 140)
        {
            var d = b - a;
            double len = d.Length;
            bool inside = false;
            double start = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / (n - 1);
                bool c = geo.FillContains(a + d * t);
                if (c && !inside) { inside = true; start = t; }
                else if (!c && inside) return (t - start) * len;
            }
            return inside ? (1 - start) * len : 0;
        }

        /// <summary>Mjeri kontrast, debljinu, širinu, x-visinu i serife (za napredne filtere).</summary>
        private static void Measure(GlyphTypeface g, AnalysisResult res)
        {
            var map = g.CharacterToGlyphMap;
            var adv = g.AdvanceWidths;

            if (map.TryGetValue('o', out var go))
            {
                var geo = g.GetGlyphOutline(go, 100, 100);
                var b = geo.Bounds;
                if (!b.IsEmpty && b.Width > 1 && b.Height > 1)
                {
                    double ym = b.Top + b.Height / 2, xm = b.Left + b.Width / 2;
                    double side = FirstRun(geo, new Point(b.Left - 1, ym), new Point(xm, ym));
                    double top = FirstRun(geo, new Point(xm, b.Top - 1), new Point(xm, ym));
                    if (side > 0 && top > 0)
                    {
                        double thick = Math.Max(side, top), thin = Math.Min(side, top);
                        res.Contrast = 1 - thin / thick;
                        res.Weight = thick / b.Height;
                    }
                }
            }

            if (g.XHeight > 0 && map.TryGetValue('n', out var gn) && map.TryGetValue('o', out var go2))
                res.Width = (adv[gn] + adv[go2]) / 2 / g.XHeight;

            if (g.CapsHeight > 0 && g.XHeight > 0)
                res.XHeight = g.XHeight / g.CapsHeight;

            if (map.TryGetValue('I', out var gI))
            {
                var geo = g.GetGlyphOutline(gI, 100, 100);
                var b = geo.Bounds;
                if (!b.IsEmpty && b.Height > 1)
                {
                    double Span(double y) => FirstRun(geo, new Point(b.Left - 1, y), new Point(b.Right + 1, y), 120);
                    double mid = Span(b.Top + b.Height * 0.5);
                    double bottom = Span(b.Bottom - b.Height * 0.04);
                    double topW = Span(b.Top + b.Height * 0.04);
                    if (mid > 0)
                    {
                        bool serif = bottom / mid > 1.7 && topW / mid > 1.7;
                        res.Serif = !serif ? 0 : (!double.IsNaN(res.Contrast) && res.Contrast < 0.35 ? 2 : 1);
                    }
                }
            }
        }

        /// <summary>Sve datoteke fonta (i za instalirane fontove).</summary>
        public static List<string> AllFiles(FontItem f)
        {
            if (f.FilePaths != null) return f.FilePaths.ToList();
            if (f.SystemFiles != null && f.SystemFiles.Count > 0) return f.SystemFiles.ToList();
            var files = new List<string>();
            try
            {
                foreach (var t in f.Family.GetTypefaces())
                    if (t.TryGetGlyphTypeface(out var g))
                    {
                        var p = FileOf(g);
                        if (p != null && !files.Contains(p, StringComparer.OrdinalIgnoreCase)) files.Add(p);
                    }
            }
            catch { }
            if (files.Count == 0 && f.MainFile != null) files.Add(f.MainFile);
            return files;
        }

        public static List<GlyphItem> GetGlyphs(FontItem f, out int total)
        {
            total = 0;
            var g = GetGlyphTypeface(f);
            if (g == null) return null;
            var codes = g.CharacterToGlyphMap.Keys
                .Where(c => c > 32 && !(c >= 0x7F && c < 0xA0) && !(c >= 0xD800 && c <= 0xDFFF) && c <= 0x10FFFF)
                .OrderBy(c => c)
                .ToList();
            total = codes.Count;
            return codes.Take(MaxGlyphs).Select(c => new GlyphItem { CodePoint = c, Text = char.ConvertFromUtf32(c) }).ToList();
        }

        public static string AllCharacters(FontItem f)
        {
            var g = GetGlyphTypeface(f);
            if (g == null) return "";
            var sb = new StringBuilder();
            foreach (var c in g.CharacterToGlyphMap.Keys.Where(c => c > 32 && !(c >= 0x7F && c < 0xA0) && !(c >= 0xD800 && c <= 0xDFFF)).OrderBy(c => c))
                sb.Append(char.ConvertFromUtf32(c));
            return sb.ToString();
        }
    }

    public static class GlyphExporter
    {
        private const double Em = 1000;

        private static (Geometry geo, double adv, double baseline, double height) Outline(FontItem f, int codePoint)
        {
            var g = FontAnalyzer.GetGlyphTypeface(f) ?? throw new InvalidOperationException("Font nije moguće pročitati.");
            if (!g.CharacterToGlyphMap.TryGetValue(codePoint, out var gi)) throw new InvalidOperationException("Znak ne postoji u fontu.");
            var geo = g.GetGlyphOutline(gi, Em, Em);
            return (geo, g.AdvanceWidths[gi] * Em, g.Baseline * Em, g.Height * Em);
        }

        public static void SaveSvg(FontItem f, int codePoint, string path)
        {
            var (geo, adv, baseline, height) = Outline(f, codePoint);
            var pg = PathGeometry.CreateFromGeometry(geo);
            var d = pg.ToString(CultureInfo.InvariantCulture);
            var fillRule = "nonzero";
            if (d.StartsWith("F0")) { fillRule = "evenodd"; d = d.Substring(2); }
            else if (d.StartsWith("F1")) d = d.Substring(2);
            var b = geo.Bounds;
            double minX = Math.Min(0, b.IsEmpty ? 0 : b.Left);
            double width = Math.Max(adv, b.IsEmpty ? adv : b.Right) - minX;
            var inv = CultureInfo.InvariantCulture;
            var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{minX.ToString("0.##", inv)} {(-baseline).ToString("0.##", inv)} {width.ToString("0.##", inv)} {height.ToString("0.##", inv)}\">\n" +
                      $"  <path fill-rule=\"{fillRule}\" fill=\"#000\" d=\"{d}\"/>\n</svg>\n";
            File.WriteAllText(path, svg);
        }

        public static void SavePng(FontItem f, int codePoint, string path, int sizePx = 512)
        {
            var (geo, adv, baseline, height) = Outline(f, codePoint);
            double scale = sizePx / height;
            var b = geo.Bounds;
            double minX = Math.Min(0, b.IsEmpty ? 0 : b.Left);
            double width = Math.Max(adv, b.IsEmpty ? adv : b.Right) - minX;
            int w = Math.Max(1, (int)Math.Ceiling(width * scale));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushTransform(new TranslateTransform(-minX, baseline));
                dc.DrawGeometry(Brushes.Black, null, geo);
                dc.Pop();
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(w, sizePx, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }

    public static class FontExporter
    {
        public static int CopyFiles(IEnumerable<string> files, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            int n = 0;
            foreach (var src in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(src)) continue;
                    var dest = Path.Combine(targetDir, Path.GetFileName(src));
                    if (File.Exists(dest)) continue;
                    File.Copy(src, dest);
                    n++;
                }
                catch { }
            }
            return n;
        }
    }

    public static class FontActivator
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AddFontResourceEx(string name, uint fl, IntPtr res);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveFontResourceEx(string name, uint fl, IntPtr res);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        private const uint WM_FONTCHANGE = 0x001D;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        public static bool Activate(string path) => AddFontResourceEx(path, 0, IntPtr.Zero) > 0;

        public static bool Deactivate(string path)
        {
            // Font je možda dodan više puta – ukloni sve reference
            bool any = false;
            for (int i = 0; i < 10 && RemoveFontResourceEx(path, 0, IntPtr.Zero); i++) any = true;
            return any;
        }

        public static void Broadcast()
        {
            try { SendMessageTimeout(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _); }
            catch { }
        }

        /// <summary>Trajna instalacija za trenutnog korisnika (bez administratorskih prava).</summary>
        public static int InstallForUser(IEnumerable<string> files)
        {
            var dir = FontLoader.UserFontsDir;
            Directory.CreateDirectory(dir);
            int count = 0;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
            foreach (var src in files)
            {
                var dest = Path.Combine(dir, Path.GetFileName(src));
                if (!File.Exists(dest)) File.Copy(src, dest);
                var ext = Path.GetExtension(src).ToLowerInvariant();
                var kind = ext == ".otf" || ext == ".otc" ? "OpenType" : "TrueType";
                var valueName = $"{Path.GetFileNameWithoutExtension(src)} ({kind})";
                key?.SetValue(valueName, dest);
                AddFontResourceEx(dest, 0, IntPtr.Zero);
                count++;
            }
            Broadcast();
            return count;
        }
    }

    public static class GoogleFontsService
    {
        private static readonly HttpClient Http = CreateClient();

        // Google Fonts vraća TTF samo "starim" preglednicima; bez ovoga dobijemo WOFF/WOFF2 koje Windows ne može učitati
        private const string TtfUserAgent =
            "Mozilla/5.0 (Macintosh; U; Intel Mac OS X 10_6_8; de-at) AppleWebKit/533.21.1 (KHTML, like Gecko) Version/5.0.5 Safari/533.21.1";

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", TtfUserAgent);
            return c;
        }

        /// <summary>Je li sadržaj stvarno TTF/OTF (a ne WOFF/WOFF2/HTML).</summary>
        private static bool IsSfnt(byte[] b)
        {
            if (b == null || b.Length < 12) return false;
            uint tag = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            return tag == 0x00010000 || tag == 0x4F54544F /*OTTO*/ || tag == 0x74727565 /*true*/ || tag == 0x74746366 /*ttcf*/;
        }

        private static readonly Regex SrcRx = new Regex(@"url\((?<u>[^)]+)\)\s*format\(['""]?(?<f>[a-z0-9]+)['""]?\)", RegexOptions.Compiled);

        private static string FindTtfUrl(string block)
        {
            foreach (Match m in SrcRx.Matches(block))
            {
                var f = m.Groups["f"].Value.ToLowerInvariant();
                if (f == "truetype" || f == "opentype") return m.Groups["u"].Value.Trim('\'', '"', ' ');
            }
            // bez format() oznake: prihvati ako adresa završava s .ttf/.otf
            foreach (Match m in UrlRx.Matches(block))
            {
                var u = m.Groups["u"].Value.Trim('\'', '"', ' ');
                var path = u.Split('?')[0].ToLowerInvariant();
                if (path.EndsWith(".ttf") || path.EndsWith(".otf")) return u;
            }
            return null;
        }

        public static string Dir => Path.Combine(AppPaths.Local, "Google Fonts");
        private static string CachePath => Path.Combine(AppPaths.Local, "google-fonts-catalog.json");

        public static string FamilyDir(string family)
        {
            var safe = new string(family.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return Path.Combine(Dir, safe);
        }

        public static bool IsDownloaded(string family)
        {
            try
            {
                var d = FamilyDir(family);
                return Directory.Exists(d) && Directory.EnumerateFiles(d).Any(FontLoader.IsFontFile);
            }
            catch { return false; }
        }

        public static async Task<List<GoogleFontEntry>> LoadCatalogAsync(bool forceRefresh = false)
        {
            Directory.CreateDirectory(AppPaths.Local);
            string json = null;
            if (!forceRefresh && File.Exists(CachePath) && File.GetLastWriteTimeUtc(CachePath) > DateTime.UtcNow.AddDays(-7))
                json = await File.ReadAllTextAsync(CachePath);

            if (json == null)
            {
                try
                {
                    json = await Http.GetStringAsync("https://fonts.google.com/metadata/fonts");
                    await File.WriteAllTextAsync(CachePath, json);
                }
                catch
                {
                    if (File.Exists(CachePath)) json = await File.ReadAllTextAsync(CachePath);
                    else throw;
                }
            }

            return await Task.Run(() => Parse(json));
        }

        private static List<GoogleFontEntry> Parse(string json)
        {
            int i = json.IndexOf('{');
            if (i > 0) json = json.Substring(i);
            var list = new List<GoogleFontEntry>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("familyMetadataList", out var arr)) return list;
            foreach (var el in arr.EnumerateArray())
            {
                try
                {
                    var e = new GoogleFontEntry
                    {
                        Family = el.GetProperty("family").GetString(),
                        Category = el.TryGetProperty("category", out var c) ? c.GetString() : "",
                        Popularity = el.TryGetProperty("popularity", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 99999,
                        IsVariableFont = el.TryGetProperty("axes", out var ax) && ax.ValueKind == JsonValueKind.Array && ax.GetArrayLength() > 0
                    };
                    if (el.TryGetProperty("fonts", out var fonts) && fonts.ValueKind == JsonValueKind.Object)
                        e.Variants = fonts.EnumerateObject().Select(x => x.Name).ToList();
                    if (e.Variants.Count == 0) e.Variants.Add("400");
                    if (!string.IsNullOrEmpty(e.Family)) list.Add(e);
                }
                catch { }
            }
            return list.OrderBy(e => e.Popularity).ToList();
        }

        public static string PreviewDir => Path.Combine(AppPaths.Local, "GooglePreviews");

        private const string PreviewChars =
            "ABCČĆDĐEFGHIJKLMNOPQRSŠTUVWXYZŽabcčćdđefghijklmnopqrsštuvwxyzž0123456789 .,:;!?'\"-–—()&@€$%/+*#„“”’";

        /// <summary>Učita font iz datoteke tako da ga WPF sigurno pronađe (svaki font u svojoj mapi).</summary>
        public static FontFamily LoadFamilyFromFile(string path)
        {
            try
            {
                var gt = new GlyphTypeface(new Uri(path));
                var name = gt.FamilyNames.Values.FirstOrDefault() ?? gt.Win32FamilyNames.Values.FirstOrDefault();
                var dir = Path.GetDirectoryName(path).TrimEnd('\\') + "\\";
                if (!string.IsNullOrEmpty(name))
                {
                    var fam = new FontFamily(new Uri(dir), "./#" + name);
                    if (fam.GetTypefaces().Any(t => t.TryGetGlyphTypeface(out _))) return fam;
                }
                var any = Fonts.GetFontFamilies(new Uri(path)).FirstOrDefault();
                if (any != null && any.GetTypefaces().Any(t => t.TryGetGlyphTypeface(out _))) return any;
            }
            catch { }
            return null;
        }

        /// <summary>Preuzme mali podskup fonta (samo potrebni znakovi) za pregled prije preuzimanja.</summary>
        public static async Task<FontFamily> GetPreviewFamilyAsync(GoogleFontEntry e, string extraText)
        {
            var safe = new string(e.Family.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var dir = Path.Combine(PreviewDir, safe);
            var path = Path.Combine(dir, "preview.ttf");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                var variant = e.Variants.Contains("400") ? "400"
                    : e.Variants.FirstOrDefault(v => !v.EndsWith("i")) ?? e.Variants.FirstOrDefault() ?? "400";
                if (variant.EndsWith("i")) variant = variant.TrimEnd('i') + "italic";
                var text = new string((PreviewChars + e.Family + (extraText ?? "")).Where(ch => !char.IsControl(ch)).Distinct().ToArray());
                var url = "https://fonts.googleapis.com/css?family=" + Uri.EscapeDataString(e.Family).Replace("%20", "+")
                          + ":" + variant + "&text=" + Uri.EscapeDataString(text);
                var css = await Http.GetStringAsync(url);
                var fileUrl = FindTtfUrl(css);
                if (fileUrl == null) return null;
                var bytes = await Http.GetByteArrayAsync(fileUrl);
                if (!IsSfnt(bytes)) return null;
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, path, true);
            }
            return await Task.Run(() => LoadFamilyFromFile(path));
        }

        private static readonly Regex FaceRx = new Regex(@"@font-face\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);
        private static readonly Regex StyleRx = new Regex(@"font-style:\s*(?<v>\w+)", RegexOptions.Compiled);
        private static readonly Regex WeightRx = new Regex(@"font-weight:\s*(?<v>\d+)", RegexOptions.Compiled);
        private static readonly Regex UrlRx = new Regex(@"url\((?<u>[^)]+)\)", RegexOptions.Compiled);

        /// <summary>Preuzima sve stilove obitelji kao TTF i vraća mapu u koju su spremljeni.</summary>
        /// <summary>Mapa s punim fontom za pregled (izvan biblioteke).</summary>
        public static string FullPreviewDir(string family)
        {
            var safe = new string(family.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return Path.Combine(PreviewDir, safe, "full");
        }

        public static async Task<int> DownloadAsync(GoogleFontEntry e, string targetDir = null)
        {
            var dir = targetDir ?? FamilyDir(e.Family);

            // Ako je font već preuzet za pregled, samo ga kopiraj
            var pre = FullPreviewDir(e.Family);
            if (targetDir == null && Directory.Exists(pre))
            {
                var files = Directory.EnumerateFiles(pre).Where(FontLoader.IsFontFile).ToList();
                if (files.Count > 0)
                {
                    Directory.CreateDirectory(dir);
                    foreach (var f in files)
                    {
                        var dest = Path.Combine(dir, Path.GetFileName(f));
                        if (!File.Exists(dest)) File.Copy(f, dest);
                    }
                    return files.Count;
                }
            }

            var variants = e.Variants.Select(v => v.EndsWith("i") ? v.TrimEnd('i') + "italic" : v);
            var url = "https://fonts.googleapis.com/css?family=" + Uri.EscapeDataString(e.Family).Replace("%20", "+")
                      + ":" + string.Join(",", variants);
            var css = await Http.GetStringAsync(url);

            Directory.CreateDirectory(dir);
            int n = 0;
            var done = new HashSet<string>();
            foreach (Match m in FaceRx.Matches(css))
            {
                var body = m.Groups["body"].Value;
                var fileUrl = FindTtfUrl(body);
                if (fileUrl == null || !done.Add(fileUrl)) continue;
                var ext = body.Contains("opentype") ? ".otf" : ".ttf";
                var weight = WeightRx.Match(body).Groups["v"].Value;
                var italic = StyleRx.Match(body).Groups["v"].Value == "italic";
                var name = $"{e.Family.Replace(" ", "")}-{weight}{(italic ? "Italic" : "")}{ext}";
                var bytes = await Http.GetByteArrayAsync(fileUrl);
                if (!IsSfnt(bytes)) continue;
                await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes);
                n++;
            }
            if (n == 0) throw new InvalidOperationException("Google Fonts nije vratio TTF datoteke.");
            return n;
        }
    }
}
