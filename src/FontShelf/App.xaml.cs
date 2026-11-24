using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FontShelf.Services;

namespace FontShelf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Način bez sučelja (koristi ga i automatski test u GitHub Actions):
            // FontShelf.exe --patch-hr ulaz.ttf izlaz.ttf
            if (e.Args.Length >= 3 && e.Args[0] == "--patch-hr")
            {
                int code = 0;
                try
                {
                    var r = FontPatcher.Patch(File.ReadAllBytes(e.Args[1]), new PatchOptions());
                    File.WriteAllBytes(e.Args[2], r.Data);
                    File.WriteAllText(e.Args[2] + ".log",
                        $"family={r.NewFamily}\nadded={string.Join(" ", r.Added)}\nskipped={string.Join(" | ", r.Skipped)}\n");
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(e.Args[2] + ".err", ex.ToString()); } catch { }
                    code = 1;
                }
                Shutdown(code);
                return;
            }

            if (e.Args.Length >= 3 && e.Args[0] == "--ui-test")
            {
                Diag.Dir = e.Args[2];
                Diag.Install();
                DispatcherUnhandledException += (s, a) => { Diag.Crash("Dispatcher", a.Exception); a.Handled = true; Shutdown(3); };
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = UiTestAsync(e.Args[1], e.Args[2]);
                return;
            }

            Diag.Install();
            Diag.Log("pokretanje " + typeof(App).Assembly.GetName().Version);
            DispatcherUnhandledException += OnUnhandled;
            base.OnStartup(e);
            var w = new MainWindow();
            MainWindow = w;
            w.Show();
        }

        /// <summary>
        /// Automatski test cijelog postupka kroz sučelje: učita font iz mape, otvori prozor za
        /// hrvatske znakove, pomiče klizače, spremi i zatim iscrta spremljene fontove u PNG.
        /// </summary>
        private async Task UiTestAsync(string srcDir, string outDir)
        {
            int code = 0;
            try
            {
                Directory.CreateDirectory(outDir);
                var items = FontLoader.LoadFolder(srcDir);
                Diag.Log($"učitano {items.Count} fontova iz {srcDir}");
                foreach (var it in items)
                {
                    var fs = FontAnalyzer.AllFiles(it);
                    Diag.Log($"  {it.Name}: {string.Join(" | ", fs.Select(Path.GetFileName))}");
                    if (fs.Count != it.Styles.Select(x => x.FilePath).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).Count())
                        throw new Exception($"Font {it.Name} ima krivi popis datoteka ({fs.Count}).");
                }
                int fileCount = Directory.GetFiles(srcDir).Count(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
                if (items.Count == 0 || items.Sum(i => FontAnalyzer.AllFiles(i).Count) != fileCount)
                    throw new Exception($"Datoteke nisu ispravno raspoređene po fontovima ({items.Sum(i => FontAnalyzer.AllFiles(i).Count)} / {fileCount}).");
                PatchWindow.OutputRootOverride = Path.Combine(outDir, "hr");

                // dva prolaza: drugi sprema iste fontove ponovno (zamjena postojećih mapa)
                for (int pass = 0; pass < 2; pass++)
                {
                    var w = new PatchWindow(items, 0);
                    w.Show();
                    for (int n = 0; n < items.Count; n++)
                    {
                        for (int i = 0; i < 100 && !w.IsReady; i++) await Task.Delay(100);
                        Diag.Log($"font {w.Index + 1}/{items.Count}: {w.TitleText.Text} — {w.InfoText.Text.Replace("\n", " / ")}");
                        if (!w.IsReady) throw new Exception("Prozor nije spreman: " + w.InfoText.Text);
                        foreach (var (g, x, y, b, own) in new[] { (0.05, 0.08, 0.2, 1.3, true), (-0.05, -0.08, -0.2, 0.7, false), (0.0, pass * 0.03, 0.0, 1.0, pass == 1) })
                        {
                            w.SetForTest(g, x, y, b, own);
                            await Task.Delay(450);
                        }
                        if (n < items.Count - 1) w.GoForTest(1);
                    }
                    w.GoForTest(-1);
                    await Task.Delay(300);
                    if (!await w.SaveAsync()) throw new Exception("Spremanje nije uspjelo: " + w.InfoText.Text);
                    Diag.Log($"prolaz {pass + 1}: spremljeno {w.SavedFonts}, zamijenjeno mapa: {w.ReplacedDirs.Count}");
                    if (w.SavedFonts != items.Count) throw new Exception("Nisu spremljeni svi fontovi: " + string.Join("; ", w.Errors));
                    foreach (var dir in w.ReplacedDirs) Directory.Delete(dir, true);
                    w.Close();

                    // učitaj spremljene fontove kao što to radi aplikacija i iscrtaj ih
                    var saved = FontLoader.LoadFolder(PatchWindow.OutputRootOverride);
                    Diag.Log($"učitano {saved.Count} spremljenih fontova: {string.Join(", ", saved.Select(x => x.Name))}");
                    if (saved.Count != items.Count) throw new Exception("Broj spremljenih fontova ne odgovara.");
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1100, 80 * saved.Count + 20));
                        int k = 0;
                        foreach (var it in saved)
                        {
                            var st = it.Styles[0];
                            var tf = new Typeface(st.Family, st.Style, st.Weight, st.Stretch);
                            var ft = new FormattedText("čćšžđ ČĆŠŽĐ Gojazni đačić", System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, tf, 48, Brushes.Black, 1.0);
                            dc.DrawText(ft, new Point(10, 10 + 80 * k++));
                            if (tf.TryGetGlyphTypeface(out var gt))
                                foreach (var ch in "čćšžđČĆŠŽĐ")
                                    if (gt.CharacterToGlyphMap.TryGetValue(ch, out var gi)) gt.GetGlyphOutline(gi, 48, 48);
                        }
                    }
                    var bmp = new RenderTargetBitmap(1100, 80 * saved.Count + 20, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(dv);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs2 = File.Create(Path.Combine(outDir, $"wpf-prolaz{pass + 1}.png"))) enc.Save(fs2);
                }
                Diag.Log("UI TEST U REDU");
            }
            catch (Exception ex)
            {
                Diag.Crash("ui-test", ex);
                code = 2;
            }
            Shutdown(code);
        }

        private DateTime _lastBox = DateTime.MinValue;
        private int _burst;

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Diag.Crash("Dispatcher", e.Exception);
            e.Handled = true;
            // Greška pri crtanju fonta ponavlja se pri svakom osvježavanju: makni odabir da se petlja prekine
            // i ne zatrpavaj korisnika porukama.
            var now = DateTime.Now;
            _burst = (now - _lastBox).TotalSeconds < 3 ? _burst + 1 : 0;
            if (_burst >= 1)
            {
                try { (MainWindow as MainWindow)?.ClearSelectionAfterError(); } catch { }
                if (_burst > 1) return;
            }
            _lastBox = now;
            MessageBox.Show("Dogodila se greška:\n\n" + e.Exception.Message, "FontShelf",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _lastBox = DateTime.Now;
        }
    }
}
