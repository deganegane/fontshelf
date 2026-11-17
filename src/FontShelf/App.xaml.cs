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
        private async Task UiTestAsync(string fontFile, string outDir)
        {
            int code = 0;
            try
            {
                Directory.CreateDirectory(outDir);
                var src = Path.Combine(outDir, "src-" + Path.GetFileNameWithoutExtension(fontFile));
                Directory.CreateDirectory(src);
                File.Copy(fontFile, Path.Combine(src, Path.GetFileName(fontFile)), true);
                var items = FontLoader.LoadFolder(src);
                Diag.Log($"učitano {items.Count} fontova iz {src}");
                if (items.Count == 0) throw new Exception("Font se ne može učitati.");
                PatchWindow.OutputRootOverride = Path.Combine(outDir, "hr");
                var w = new PatchWindow(items[0]);
                w.Show();
                for (int i = 0; i < 100 && !w.IsReady; i++) await Task.Delay(100);
                if (!w.IsReady) throw new Exception("Prozor nije spreman: " + w.InfoText.Text);
                foreach (var (g, y, b) in new[] { (0.05, 0.05, 1.3), (-0.05, -0.1, 0.7), (0.0, 0.0, 1.0) })
                {
                    w.SetForTest(g, y, b);
                    await Task.Delay(600);
                }
                Diag.Log("info: " + w.InfoText.Text.Replace("\n", " / "));
                if (!await w.SaveAsync()) throw new Exception("Spremanje nije uspjelo: " + w.InfoText.Text);
                w.Close();

                // učitaj spremljene fontove kao što to radi aplikacija i iscrtaj ih
                var saved = FontLoader.LoadFolder(PatchWindow.OutputRootOverride);
                Diag.Log($"spremljeno, učitano {saved.Count} fontova: {string.Join(", ", saved.Select(x => x.Name))}");
                foreach (var it in saved)
                {
                    var analysis = FontAnalyzer.AllFiles(it);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1000, 220));
                        int k = 0;
                        foreach (var st in it.Styles.Take(3))
                        {
                            var tf = new Typeface(st.Family, st.Style, st.Weight, st.Stretch);
                            var ft = new FormattedText("čćšžđ ČĆŠŽĐ Gojazni đačić", System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, tf, 48, Brushes.Black, 1.0);
                            dc.DrawText(ft, new Point(10, 10 + 68 * k++));
                            if (tf.TryGetGlyphTypeface(out var gt))
                                foreach (var ch in "čćšžđČĆŠŽĐ")
                                    if (gt.CharacterToGlyphMap.TryGetValue(ch, out var gi)) gt.GetGlyphOutline(gi, 48, 48);
                        }
                    }
                    var bmp = new RenderTargetBitmap(1000, 220, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(dv);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    var png = Path.Combine(outDir, "wpf-" + string.Concat(it.Name.Split(Path.GetInvalidFileNameChars())) + ".png");
                    using (var fs = File.Create(png)) enc.Save(fs);
                    Diag.Log("iscrtano " + png);
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

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Diag.Crash("Dispatcher", e.Exception);
            MessageBox.Show("Dogodila se greška:\n\n" + e.Exception.Message, "FontShelf",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
