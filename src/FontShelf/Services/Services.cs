using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using FontShelf.Models;
using Microsoft.Win32;

namespace FontShelf.Services
{
    public static class LibraryStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FontShelf");
        private static readonly string FilePath = Path.Combine(Dir, "library.json");

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
                Directory.CreateDirectory(Dir);
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

        private static string FaceName(Typeface tf)
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

        private static FontItem Create(FontFamily fam, string name, List<string> files, string folder, string key)
        {
            var styles = new List<FontStyleItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var tf in fam.GetTypefaces())
                {
                    var sn = FaceName(tf);
                    if (!seen.Add(sn)) continue;
                    styles.Add(new FontStyleItem
                    {
                        Name = sn,
                        Family = fam,
                        Weight = tf.Weight,
                        Style = tf.Style,
                        Stretch = tf.Stretch
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
                FolderPath = folder
            };
        }

        private static IEnumerable<FontFamily> SafeFamilies(string file)
        {
            try { return Fonts.GetFontFamilies(new Uri(file)).ToList(); }
            catch { return Enumerable.Empty<FontFamily>(); }
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
                            if (item != null) list.Add(item);
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

            IEnumerable<string> files;
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
            var groups = new Dictionary<(string dir, string name), List<string>>();
            var order = new List<(string dir, string name)>();
            foreach (var file in files)
            {
                var dir = Path.GetDirectoryName(file);
                foreach (var fam in SafeFamilies(file))
                {
                    var name = GetName(fam);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var k = (dir, name);
                    if (!groups.TryGetValue(k, out var l))
                    {
                        l = new List<string>();
                        groups[k] = l;
                        order.Add(k);
                    }
                    if (!l.Contains(file)) l.Add(file);
                }
            }

            foreach (var k in order)
            {
                var fileList = groups[k];
                FontItem item = null;
                try
                {
                    var baseUri = new Uri(k.dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
                    var fam = new FontFamily(baseUri, "./#" + k.name);
                    item = Create(fam, k.name, fileList, folder, "file:" + k.dir + "|" + k.name);
                }
                catch { }

                if (item == null)
                {
                    var fam = SafeFamilies(fileList[0]).FirstOrDefault(f => GetName(f) == k.name);
                    if (fam != null) item = Create(fam, k.name, fileList, folder, "file:" + k.dir + "|" + k.name);
                }

                if (item != null) list.Add(item);
            }

            return list;
        }
    }

    public static class FontAnalyzer
    {
        public static GlyphTypeface GetGlyphTypeface(FontItem f)
        {
            try
            {
                var tfs = f.Family.GetTypefaces().ToList();
                var pick = tfs.FirstOrDefault(t => t.Weight == FontWeights.Normal && t.Style == FontStyles.Normal)
                           ?? tfs.FirstOrDefault();
                if (pick != null && pick.TryGetGlyphTypeface(out var g)) return g;
                foreach (var t in tfs)
                    if (t.TryGetGlyphTypeface(out var g2)) return g2;
            }
            catch { }
            return null;
        }

        public static (bool? mono, bool? croatian) Analyze(FontItem f)
        {
            try
            {
                var g = GetGlyphTypeface(f);
                if (g == null) return (null, null);
                var map = g.CharacterToGlyphMap;
                bool mono = false;
                if (map.TryGetValue('i', out var gi) && map.TryGetValue('W', out var gw) && map.TryGetValue('m', out var gm))
                {
                    var a = g.AdvanceWidths;
                    mono = Math.Abs(a[gi] - a[gw]) < 0.001 && Math.Abs(a[gi] - a[gm]) < 0.001;
                }
                bool hr = "čćđšžČĆĐŠŽ".All(c => map.ContainsKey(c));
                return (mono, hr);
            }
            catch { return (null, null); }
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

        public const int MaxGlyphs = 1500;
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
        public static int InstallForUser(FontItem font)
        {
            var dir = FontLoader.UserFontsDir;
            Directory.CreateDirectory(dir);
            int count = 0;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
            foreach (var src in font.FilePaths)
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
}
