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

        public static List<FontItem> LoadSystem()
        {
            var list = new List<FontItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var fam in Fonts.SystemFontFamilies)
            {
                var name = GetName(fam);
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                var item = Create(fam, name, null, null, "sys:" + name);
                if (item != null) list.Add(item);
            }

            // Fontovi instalirani samo za korisnika (Windows 10 1809+)
            try
            {
                if (Directory.Exists(UserFontsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(UserFontsDir).Where(IsFontFile))
                    {
                        foreach (var fam in SafeFamilies(file))
                        {
                            var name = GetName(fam);
                            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                            var item = Create(fam, name, null, null, "sys:" + name);
                            if (item != null) { item.MainFile = file; list.Add(item); }
                        }
                    }
                }
            }
            catch { }

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
                res.MainFile = f.MainFile ?? FileOf(g);
                if (res.MainFile != null) res.IsVariable = OpenTypeReader.HasVariations(res.MainFile);
            }
            catch { }
            return res;
        }

        /// <summary>Sve datoteke fonta (i za instalirane fontove).</summary>
        public static List<string> AllFiles(FontItem f)
        {
            if (f.FilePaths != null) return f.FilePaths.ToList();
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
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

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

        private static readonly Regex FaceRx = new Regex(@"@font-face\s*\{(?<body>[^}]*)\}", RegexOptions.Compiled);
        private static readonly Regex StyleRx = new Regex(@"font-style:\s*(?<v>\w+)", RegexOptions.Compiled);
        private static readonly Regex WeightRx = new Regex(@"font-weight:\s*(?<v>\d+)", RegexOptions.Compiled);
        private static readonly Regex UrlRx = new Regex(@"url\((?<u>[^)]+)\)", RegexOptions.Compiled);

        /// <summary>Preuzima sve stilove obitelji kao TTF i vraća mapu u koju su spremljeni.</summary>
        public static async Task<int> DownloadAsync(GoogleFontEntry e)
        {
            var variants = e.Variants.Select(v => v.EndsWith("i") ? v.TrimEnd('i') + "italic" : v);
            var url = "https://fonts.googleapis.com/css?family=" + Uri.EscapeDataString(e.Family).Replace("%20", "+")
                      + ":" + string.Join(",", variants);
            var css = await Http.GetStringAsync(url);

            var dir = FamilyDir(e.Family);
            Directory.CreateDirectory(dir);
            int n = 0;
            var done = new HashSet<string>();
            foreach (Match m in FaceRx.Matches(css))
            {
                var body = m.Groups["body"].Value;
                var u = UrlRx.Match(body);
                if (!u.Success) continue;
                var fileUrl = u.Groups["u"].Value.Trim('\'', '"', ' ');
                if (!done.Add(fileUrl)) continue;
                var ext = Path.GetExtension(new Uri(fileUrl).AbsolutePath).ToLowerInvariant();
                if (ext != ".ttf" && ext != ".otf") continue;
                var weight = WeightRx.Match(body).Groups["v"].Value;
                var italic = StyleRx.Match(body).Groups["v"].Value == "italic";
                var name = $"{e.Family.Replace(" ", "")}-{weight}{(italic ? "Italic" : "")}{ext}";
                var bytes = await Http.GetByteArrayAsync(fileUrl);
                await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes);
                n++;
            }
            if (n == 0) throw new InvalidOperationException("Google Fonts nije vratio TTF datoteke.");
            return n;
        }
    }

    public static class ThemeManager
    {
        public static bool IsLight { get; private set; }

        private static void S(ResourceDictionary r, string key, string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            r[key] = b;
        }

        public static void Apply(bool light)
        {
            IsLight = light;
            var r = Application.Current.Resources;
            if (!light)
            {
                S(r, "BgBrush", "#1B1C1F");
                S(r, "PanelBrush", "#212226");
                S(r, "CardBrush", "#28292E");
                S(r, "CardHoverBrush", "#2F3036");
                S(r, "LineBrush", "#35373D");
                S(r, "InputBrush", "#1E1F23");
                S(r, "TextBrush", "#ECECEF");
                S(r, "SubTextBrush", "#9497A0");
                S(r, "BodyTextBrush", "#C9CAD0");
                S(r, "AccentBrush", "#8B9BFF");
                S(r, "AccentSoftBrush", "#2E8B9BFF");
                S(r, "OnAccentBrush", "#15161A");
                S(r, "StarBrush", "#FFC857");
                S(r, "WarnBrush", "#FF9F6B");
                S(r, "GoodBrush", "#6BD69B");
                S(r, "ControlBrush", "#33353C");
                S(r, "SegmentOnBrush", "#3A3C44");
                S(r, "HoverBrush", "#FFFFFF");
                S(r, "ThumbBrush", "#40FFFFFF");
                S(r, "OverlayABrush", "#FF6B8B");
                S(r, "OverlayBBrush", "#4FD1C5");
            }
            else
            {
                S(r, "BgBrush", "#F3F3F6");
                S(r, "PanelBrush", "#FBFBFC");
                S(r, "CardBrush", "#FFFFFF");
                S(r, "CardHoverBrush", "#F6F6F9");
                S(r, "LineBrush", "#E1E2E8");
                S(r, "InputBrush", "#FFFFFF");
                S(r, "TextBrush", "#1B1C20");
                S(r, "SubTextBrush", "#6B6E78");
                S(r, "BodyTextBrush", "#3A3C44");
                S(r, "AccentBrush", "#4F5FE0");
                S(r, "AccentSoftBrush", "#1F4F5FE0");
                S(r, "OnAccentBrush", "#FFFFFF");
                S(r, "StarBrush", "#E3A008");
                S(r, "WarnBrush", "#D2591F");
                S(r, "GoodBrush", "#16924F");
                S(r, "ControlBrush", "#ECEDF1");
                S(r, "SegmentOnBrush", "#FFFFFF");
                S(r, "HoverBrush", "#000000");
                S(r, "ThumbBrush", "#38000000");
                S(r, "OverlayABrush", "#E0245E");
                S(r, "OverlayBBrush", "#0F9D8F");
            }
        }

        public static string Css(string key)
        {
            if (Application.Current.Resources[key] is SolidColorBrush b)
            {
                var c = b.Color;
                return $"rgba({c.R},{c.G},{c.B},{(c.A / 255.0).ToString("0.###", CultureInfo.InvariantCulture)})";
            }
            return "#888";
        }
    }
}
