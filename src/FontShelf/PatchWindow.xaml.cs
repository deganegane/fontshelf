using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FontShelf.Models;
using FontShelf.Services;

namespace FontShelf
{
    public partial class PatchWindow : Window
    {
        public static string OutputRoot => Path.Combine(AppPaths.Local, "Hrvatski fontovi");

        private readonly FontItem _font;
        private List<string> _files = new List<string>();
        private string _previewFile;
        private byte[] _previewBytes;
        private int _upem = 1000;
        private bool _ready;
        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };

        /// <summary>Ime nove obitelji nakon spremanja.</summary>
        public string SavedFamily { get; private set; }

        public PatchWindow(FontItem font)
        {
            _font = font;
            InitializeComponent();
            _debounce.Tick += (s, e) => { _debounce.Stop(); Regenerate(); };
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                int dark = ThemeManager.IsLight ? 0 : 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
            }
            catch { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            TitleText.Text = $"Dodaj hrvatske znakove — {_font.Name}";
            _files = FontAnalyzer.AllFiles(_font).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (_files.Count == 0)
            {
                Fail("Datoteke ovog fonta nije moguće pronaći.");
                return;
            }

            // za pregled uzmi uspravni Regular stil ako postoji
            var regular = _font.Styles.FirstOrDefault(s => s.WeightValue == 400 && !s.IsItalic);
            _previewFile = regular?.FilePath ?? regular?.SourceFile;
            if (_previewFile == null || !File.Exists(_previewFile)) _previewFile = _font.MainFile;
            if (_previewFile == null || !File.Exists(_previewFile)) _previewFile = _files[0];

            try { _previewBytes = File.ReadAllBytes(_previewFile); }
            catch (Exception ex) { Fail("Font nije moguće pročitati: " + ex.Message); return; }

            _ready = true;
            Regenerate();
        }

        private void Fail(string msg)
        {
            InfoText.Text = msg;
            InfoText.Foreground = (Brush)FindResource("WarnBrush");
            SaveButton.IsEnabled = false;
            ControlsPanel.IsEnabled = false;
        }

        private PatchOptions Options() => new PatchOptions
        {
            AccentGap = GapSlider.Value * _upem,
            BarShiftY = BarYSlider.Value * _upem,
            BarShiftX = BarXSlider.Value * _upem,
            BarWidthScale = BarWSlider.Value,
            BarThicknessScale = BarTSlider.Value
        };

        private void UpdateLabels()
        {
            GapVal.Text = $"{GapSlider.Value * 100:+0;-0;0}%";
            BarYVal.Text = $"{BarYSlider.Value * 100:+0;-0;0}%";
            BarXVal.Text = $"{BarXSlider.Value * 100:+0;-0;0}%";
            BarWVal.Text = $"{BarWSlider.Value * 100:0}%";
            BarTVal.Text = $"{BarTSlider.Value * 100:0}%";
        }

        private void Option_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            UpdateLabels();
            if (!_ready) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void Regenerate()
        {
            UpdateLabels();
            try
            {
                var r = FontPatcher.Patch(_previewBytes, Options());
                _upem = r.UnitsPerEm > 0 ? r.UnitsPerEm : _upem;

                var dir = Path.Combine(Path.GetTempPath(), "FontShelf", "patch", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "preview.ttf");
                File.WriteAllBytes(path, r.Data);
                var fam = GoogleFontsService.LoadFamilyFromFile(path);
                if (fam != null)
                {
                    PreviewBig.FontFamily = fam;
                    PreviewSmall.FontFamily = fam;
                    PreviewCaps.FontFamily = fam;
                }

                var added = r.Added.Count > 0 ? "Dodaju se: " + string.Join(" ", r.Added) : "Font već ima sve hrvatske znakove.";
                var skipped = r.Skipped.Count > 0 ? "\nNije moguće dodati: " + string.Join(", ", r.Skipped) : "";
                var styles = _files.Count > 1 ? $"\nSprema se {_files.Count} {Plural.Of(_files.Count, "datoteka", "datoteke", "datoteka")} (svi stilovi), kao „{r.NewFamily}”."
                                              : $"\nSprema se kao „{r.NewFamily}”.";
                InfoText.Text = added + skipped + styles;
                InfoText.Foreground = (Brush)FindResource("SubTextBrush");
                SaveButton.Content = $"Spremi kao „{r.NewFamily}”";
                SaveButton.IsEnabled = r.Added.Count > 0;
            }
            catch (FontPatchException ex) { Fail(ex.Message); }
            catch (Exception ex) { Fail("Font nije moguće obraditi: " + ex.Message); }
        }

        /// <summary>Zakrpa sve stilove jednog fonta zadanim postavkama. Vraća novu obitelj (ili null) i greške.</summary>
        public static (string family, int saved, List<string> errors) PatchFiles(IEnumerable<string> files, PatchOptions opts)
        {
            string fam = null, outDir = null;
            int ok = 0;
            var errs = new List<string>();
            foreach (var f in files)
            {
                try
                {
                    var r = FontPatcher.Patch(File.ReadAllBytes(f), opts);
                    if (r.Added.Count == 0) continue;
                    fam ??= r.NewFamily;
                    if (outDir == null)
                    {
                        var safe = new string((fam ?? "Font HR").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                        outDir = Path.Combine(OutputRoot, safe);
                        Directory.CreateDirectory(outDir);
                    }
                    File.WriteAllBytes(Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "-HR.ttf"), r.Data);
                    ok++;
                }
                catch (Exception ex) { errs.Add(Path.GetFileName(f) + ": " + ex.Message); }
            }
            return (fam, ok, errs);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            _ready = false;
            GapSlider.Value = 0;
            BarYSlider.Value = 0;
            BarXSlider.Value = 0;
            BarWSlider.Value = 1;
            BarTSlider.Value = 1;
            _ready = true;
            Regenerate();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            SaveButton.IsEnabled = false;
            var opts = Options();
            var files = _files.ToList();
            try
            {
                var (family, saved, errors) = await Task.Run(() => PatchFiles(files, opts));

                if (saved == 0)
                {
                    Fail("Spremanje nije uspjelo.\n" + string.Join("\n", errors));
                    return;
                }
                SavedFamily = family;
                if (errors.Count > 0)
                    MessageBox.Show(this, "Neki stilovi nisu spremljeni:\n\n" + string.Join("\n", errors), "FontShelf",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Fail("Spremanje nije uspjelo: " + ex.Message);
            }
        }
    }
}
