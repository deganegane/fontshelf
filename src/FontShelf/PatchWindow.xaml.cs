using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FontShelf.Models;
using FontShelf.Services;

namespace FontShelf
{
    public partial class PatchWindow : Window
    {
        public static string OutputRootOverride;
        public static string OutputRoot => OutputRootOverride ?? Path.Combine(AppPaths.Local, "Hrvatski fontovi");

        /// <summary>Postavke klizača za jedan font (u dijelovima em-a, osim omjera).</summary>
        private class Settings
        {
            public double Gap, AccX, BarY, BarYU, BarX;
            public double BarW = 1, BarT = 1;
            public bool OwnD;
            public bool Include = true;
            public bool Failed;      // font se ne može obraditi (varijabilni, .ttc…)
            public bool Nothing;     // font već ima sve znakove
        }

        private readonly List<FontItem> _fonts;
        private readonly Dictionary<FontItem, Settings> _settings = new Dictionary<FontItem, Settings>();
        private int _index;
        private FontItem Font => _fonts[_index];
        private Settings Cur => _settings[Font];

        private List<string> _files = new List<string>();
        private string _previewFile;
        private byte[] _previewBytes;
        private int _upem = 1000;
        private bool _ready;
        private bool _loading;
        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };

        /// <summary>Ime nove obitelji (zadnje spremljene) i broj spremljenih fontova.</summary>
        public string SavedFamily { get; private set; }
        public int SavedFonts { get; private set; }
        public List<string> Errors { get; private set; } = new List<string>();
        /// <summary>Stare mape koje su zamijenjene novom inačicom istog fonta.</summary>
        public List<string> ReplacedDirs { get; } = new List<string>();

        public PatchWindow(FontItem font) : this(new List<FontItem> { font }, 0) { }

        public PatchWindow(List<FontItem> fonts, int index)
        {
            _fonts = fonts;
            _index = Math.Max(0, Math.Min(index, fonts.Count - 1));
            foreach (var f in fonts) _settings[f] = new Settings();
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
            if (_fonts.Count > 1)
            {
                NavPanel.Visibility = Visibility.Visible;
                IncludeCheck.Visibility = Visibility.Visible;
                HeaderPanel.Margin = new Thickness(0, 0, 200, 0);
            }
            LoadFont();
        }

        // ------------------------------------------------------------ listanje fontova
        private void LoadFont()
        {
            _ready = false;
            _loading = true;
            var s = Cur;
            GapSlider.Value = s.Gap; AccXSlider.Value = s.AccX;
            BarYSlider.Value = s.BarY; BarYUSlider.Value = s.BarYU; BarXSlider.Value = s.BarX;
            BarWSlider.Value = s.BarW; BarTSlider.Value = s.BarT;
            OwnDCheck.IsChecked = s.OwnD;
            IncludeCheck.IsChecked = s.Include;
            _loading = false;

            TitleText.Text = Font.Name;
            NavText.Text = $"{_index + 1} / {_fonts.Count}";
            PrevButton.IsEnabled = _index > 0;
            NextButton.IsEnabled = _index < _fonts.Count - 1;
            ControlsPanel.IsEnabled = true;
            s.Failed = false;
            InfoText.Foreground = (Brush)FindResource("SubTextBrush");
            PreviewBig.FontFamily = PreviewSmall.FontFamily = PreviewCaps.FontFamily = Font.Family;

            _files = FontAnalyzer.AllFiles(Font).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Diag.Log($"prozor: {Font.Name} [{Font.Key}] datoteke: {string.Join(" | ", _files)}");
            if (_files.Count == 0) { Fail("Datoteke ovog fonta nije moguće pronaći."); return; }

            // za pregled uzmi uspravni Regular stil ako postoji
            var regular = Font.Styles.FirstOrDefault(x => x.WeightValue == 400 && !x.IsItalic);
            _previewFile = regular?.FilePath ?? regular?.SourceFile;
            if (_previewFile == null || !_files.Contains(_previewFile, StringComparer.OrdinalIgnoreCase)) _previewFile = Font.MainFile;
            if (_previewFile == null || !_files.Contains(_previewFile, StringComparer.OrdinalIgnoreCase)) _previewFile = _files[0];

            try { _previewBytes = File.ReadAllBytes(_previewFile); }
            catch (Exception ex) { Fail("Font nije moguće pročitati: " + ex.Message); return; }

            _ready = true;
            Regenerate();
        }

        private void StoreCurrent()
        {
            var s = Cur;
            s.Gap = GapSlider.Value; s.AccX = AccXSlider.Value;
            s.BarY = BarYSlider.Value; s.BarYU = BarYUSlider.Value; s.BarX = BarXSlider.Value;
            s.BarW = BarWSlider.Value; s.BarT = BarTSlider.Value;
            s.OwnD = OwnDCheck.IsChecked == true;
            s.Include = IncludeCheck.IsChecked == true;
        }

        private void Go(int delta)
        {
            int n = _index + delta;
            if (n < 0 || n >= _fonts.Count) return;
            _debounce.Stop();
            StoreCurrent();
            _index = n;
            LoadFont();
        }

        private void Prev_Click(object sender, RoutedEventArgs e) => Go(-1);
        private void Next_Click(object sender, RoutedEventArgs e) => Go(1);

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_fonts.Count < 2) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            if (key == Key.PageUp || (alt && key == Key.Left)) { Go(-1); e.Handled = true; }
            else if (key == Key.PageDown || (alt && key == Key.Right)) { Go(1); e.Handled = true; }
        }

        // ------------------------------------------------------------ pregled
        private void Fail(string msg)
        {
            Cur.Failed = true;
            InfoText.Text = msg;
            InfoText.Foreground = (Brush)FindResource("WarnBrush");
            ControlsPanel.IsEnabled = false;
            UpdateSaveButton();
        }

        private static PatchOptions ToOptions(Settings s, int upem) => new PatchOptions
        {
            AccentGap = s.Gap * upem,
            AccentShiftX = s.AccX * upem,
            BarShiftY = s.BarY * upem,
            BarShiftYUpper = s.BarYU * upem,
            BarShiftX = s.BarX * upem,
            BarWidthScale = s.BarW,
            BarThicknessScale = s.BarT,
            UseEth = !s.OwnD
        };

        private void UpdateLabels()
        {
            GapVal.Text = $"{GapSlider.Value * 100:+0;-0;0}%";
            AccXVal.Text = $"{AccXSlider.Value * 100:+0;-0;0}%";
            BarYVal.Text = $"{BarYSlider.Value * 100:+0;-0;0}%";
            BarYUVal.Text = $"{BarYUSlider.Value * 100:+0;-0;0}%";
            BarXVal.Text = $"{BarXSlider.Value * 100:+0;-0;0}%";
            BarWVal.Text = $"{BarWSlider.Value * 100:0}%";
            BarTVal.Text = $"{BarTSlider.Value * 100:0}%";
        }

        private void Option_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            UpdateLabels();
            if (!_ready || _loading) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void Check_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || !_ready || _loading) return;
            Regenerate();
        }

        private void Include_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _loading) return;
            Cur.Include = IncludeCheck.IsChecked == true;
            UpdateSaveButton();
        }

        private void Regenerate()
        {
            UpdateLabels();
            StoreCurrent();
            try
            {
                Diag.Log("pregled: " + _previewFile);
                // prvi prolaz samo da se dozna veličina em-a
                var probe = FontPatcher.Patch(_previewBytes, new PatchOptions());
                _upem = probe.UnitsPerEm > 0 ? probe.UnitsPerEm : 1000;
                var r = FontPatcher.Patch(_previewBytes, ToOptions(Cur, _upem));

                var dir = Path.Combine(Path.GetTempPath(), "FontShelf", "patch", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "preview" + r.Extension);
                File.WriteAllBytes(path, r.Data);
                var fam = GoogleFontsService.LoadFamilyFromFile(path);
                if (fam != null)
                {
                    PreviewBig.FontFamily = fam;
                    PreviewSmall.FontFamily = fam;
                    PreviewCaps.FontFamily = fam;
                }

                Cur.Nothing = r.Added.Count == 0;
                var added = r.Added.Count > 0 ? "Dodaju se: " + string.Join(" ", r.Added) : "Font već ima sve hrvatske znakove.";
                var skipped = r.Skipped.Count > 0 ? "\nNije moguće dodati: " + string.Join(", ", r.Skipped) : "";
                var styles = r.Added.Count == 0 ? ""
                    : _files.Count > 1 ? $"\nSprema se {_files.Count} {Plural.Of(_files.Count, "stil", "stila", "stilova")}, kao „{r.NewFamily}”."
                    : $"\nSprema se kao „{r.NewFamily}”.";
                InfoText.Text = added + skipped + styles;
                InfoText.Foreground = (Brush)FindResource("SubTextBrush");
            }
            catch (FontPatchException ex) { Diag.Log("  " + ex.Message); Fail(ex.Message); }
            catch (Exception ex) { Diag.Log("  " + ex); Fail("Font nije moguće obraditi: " + ex.Message); }
            UpdateSaveButton();
        }

        private void UpdateSaveButton()
        {
            if (_fonts.Count == 1)
            {
                SaveButton.Content = "Spremi kopiju s hrvatskim znakovima";
                SaveButton.IsEnabled = !Cur.Failed && !Cur.Nothing;
                return;
            }
            int n = _settings.Values.Count(s => s.Include && !s.Failed && !s.Nothing);
            SaveButton.Content = $"Spremi sve uključene ({n})";
            SaveButton.IsEnabled = n > 0;
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (Cur.Failed) return;
            _ready = false;
            GapSlider.Value = 0; AccXSlider.Value = 0;
            BarYSlider.Value = 0; BarYUSlider.Value = 0; BarXSlider.Value = 0;
            BarWSlider.Value = 1; BarTSlider.Value = 1;
            OwnDCheck.IsChecked = false;
            _ready = true;
            Regenerate();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ------------------------------------------------------------ spremanje
        /// <summary>
        /// Zakrpa sve datoteke jednog fonta. Postavke su u dijelovima em-a pa se preračunavaju za svaku datoteku.
        /// Ako mapa obitelji već postoji, sprema se u novu (stara se vraća u replaced) — WPF ne podnosi
        /// prepisivanje datoteka fontova koje je već učitao.
        /// </summary>
        private static (string family, int saved, List<string> errors) PatchFiles(IEnumerable<string> files, Settings s,
            string preferFile, List<string> replaced)
        {
            string fam = null;
            int ok = 0;
            var errs = new List<string>();
            var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                try
                {
                    Diag.Log("patch " + f);
                    var bytes = File.ReadAllBytes(f);
                    int upem = FontPatcher.Patch(bytes, new PatchOptions()).UnitsPerEm;
                    var r = FontPatcher.Patch(bytes, ToOptions(s, upem > 0 ? upem : 1000));
                    if (r.Added.Count == 0) { Diag.Log("  nema promjena"); continue; }
                    var famName = r.NewFamily ?? "Font HR";
                    if (fam == null || string.Equals(f, preferFile, StringComparison.OrdinalIgnoreCase)) fam = famName;
                    var safe = new string(famName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
                    if (!dirs.TryGetValue(safe, out var outDir))
                    {
                        outDir = Path.Combine(OutputRoot, safe);
                        for (int v = 2; Directory.Exists(outDir); v++)
                        {
                            if (!replaced.Contains(outDir, StringComparer.OrdinalIgnoreCase)) replaced.Add(outDir);
                            outDir = Path.Combine(OutputRoot, $"{safe} ({v})");
                        }
                        Directory.CreateDirectory(outDir);
                        dirs[safe] = outDir;
                    }
                    var dest = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "-HR" + r.Extension);
                    File.WriteAllBytes(dest, r.Data);
                    Diag.Log("  spremljeno " + dest + " (" + string.Join("", r.Added) + ")");
                    ok++;
                }
                catch (Exception ex)
                {
                    Diag.Log("  greška: " + ex.Message);
                    errs.Add(Path.GetFileName(f) + ": " + ex.Message);
                }
            }
            return (fam, ok, errs);
        }

        /// <summary>Spremi sve uključene fontove; vraća true ako je bar jedan spremljen.</summary>
        public async Task<bool> SaveAsync()
        {
            StoreCurrent();
            SaveButton.IsEnabled = false;
            PrevButton.IsEnabled = NextButton.IsEnabled = false;
            var jobs = _fonts.Where(f => _settings[f].Include && !_settings[f].Failed && !_settings[f].Nothing).ToList();
            var current = Font;
            var errors = new List<string>();
            string lastFamily = null, currentFamily = null;
            int done = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var f = jobs[i];
                if (jobs.Count > 1) SaveButton.Content = $"Spremam… {i + 1}/{jobs.Count}";
                var s = _settings[f];
                var files = FontAnalyzer.AllFiles(f).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var prefer = f == current ? _previewFile : f.MainFile;
                Diag.Log($"spremanje: {f.Name}, {files.Count} datoteka");
                var (family, saved, errs) = await Task.Run(() => PatchFiles(files, s, prefer, ReplacedDirs));
                if (saved > 0)
                {
                    done++;
                    lastFamily = family;
                    if (f == current) currentFamily = family;
                }
                else if (jobs.Count > 1 && errs.Count > 0) errors.Add(f.Name + ": " + errs[0]);
                if (jobs.Count == 1) errors.AddRange(errs);
            }
            Errors = errors;
            SavedFonts = done;
            if (done == 0)
            {
                InfoText.Text = "Spremanje nije uspjelo.\n" + string.Join("\n", errors.Take(6));
                InfoText.Foreground = (Brush)FindResource("WarnBrush");
                PrevButton.IsEnabled = _index > 0;
                NextButton.IsEnabled = _index < _fonts.Count - 1;
                return false;
            }
            SavedFamily = currentFamily ?? lastFamily;
            return true;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!await SaveAsync()) return;
                if (Errors.Count > 0)
                    MessageBox.Show(this, "Nije sve spremljeno:\n\n" + string.Join("\n", Errors.Take(12)), "FontShelf",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Diag.Log("Save_Click: " + ex);
                InfoText.Text = "Spremanje nije uspjelo: " + ex.Message;
            }
        }

        // ---- za automatski test
        public bool IsReady => _ready;
        public int Index => _index;
        public void SetForTest(double gap, double accX, double barY, double barW, bool ownD)
        {
            GapSlider.Value = gap; AccXSlider.Value = accX; BarYSlider.Value = barY; BarYUSlider.Value = barY;
            BarWSlider.Value = barW; OwnDCheck.IsChecked = ownD;
        }
        public void GoForTest(int delta) => Go(delta);
    }
}
