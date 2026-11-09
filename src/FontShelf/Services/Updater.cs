using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FontShelf.Services
{
    /// <summary>Provjera i instalacija novih verzija s GitHub Releases.</summary>
    public static class Updater
    {
        private const string LatestUrl = "https://api.github.com/repos/deganegane/fontshelf/releases/latest";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "FontShelf-Updater");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            return c;
        }

        public class UpdateInfo
        {
            public int Build { get; set; }
            public string DownloadUrl { get; set; }
            public string Notes { get; set; }
        }

        public static int CurrentBuild
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v?.Build ?? 0;
            }
        }

        private static string ExePath => Environment.ProcessPath;

        /// <summary>Briše ostatke prethodnog ažuriranja.</summary>
        public static void CleanupOld()
        {
            try
            {
                var old = ExePath + ".old";
                if (File.Exists(old)) File.Delete(old);
                var part = ExePath + ".new";
                if (File.Exists(part)) File.Delete(part);
            }
            catch { }
        }

        /// <summary>Vraća informacije o novijoj verziji ili null ako je ova najnovija.</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            var json = await Http.GetStringAsync(LatestUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!int.TryParse(new string(tag.Where(char.IsDigit).ToArray()), out var build)) return null;
            if (build <= CurrentBuild) return null;

            string url = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            if (url == null) return null;

            return new UpdateInfo
            {
                Build = build,
                DownloadUrl = url,
                Notes = root.TryGetProperty("body", out var b) ? b.GetString() : ""
            };
        }

        /// <summary>
        /// Preuzme novu verziju, zamijeni .exe i pokrene ga. Windows dopušta preimenovanje .exe-a koji radi,
        /// pa stari postaje .old (briše se kod sljedećeg pokretanja). Datoteka preuzeta iz aplikacije
        /// nema oznaku "preuzeto s interneta", pa SmartScreen ne smeta.
        /// </summary>
        public static async Task InstallAsync(UpdateInfo info, IProgress<double> progress = null)
        {
            var exe = ExePath;
            var tmp = exe + ".new";
            var old = exe + ".old";

            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                using var src = await resp.Content.ReadAsStreamAsync();
                using var dst = File.Create(tmp);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            var len = new FileInfo(tmp).Length;
            if (len < 1_000_000) throw new InvalidOperationException("Preuzeta datoteka je neispravna.");

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(tmp, exe);
            }
            catch
            {
                File.Move(old, exe); // vrati staru verziju
                throw;
            }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
    }
}
