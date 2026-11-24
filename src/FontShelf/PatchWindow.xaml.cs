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
            public string CaronCh = "", AcuteCh = "";
            public double CaronS = 1, AcuteS = 1, CaronR, AcuteR, CaronW = 1, AcuteW = 1, CaronT, AcuteT;
            public bool CaronFlip, AcuteFlip;
            public bool Saved;
            public Dictionary<int, (double x, double y)> Per = new Dictionary<int, (double x, double y)>();
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

        private readonly bool _batch;

        public PatchWindow(List<FontItem> fonts, int index, bool batch = false)
        {
            _batch = batch;
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
                if (_batch)
                {
                    IncludeCheck.Visibility = Visibility.Visible;
                    SaveAllButton.Visibility = Visibility.Visible;
                }
            }
            ZoomSlider.Value = _zoom;
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
            LoadFont();
        }

        private string _caronCh = "", _acuteCh = "";
        private int _perCp = 0x10D;
        private bool _perLoading;

        private void LoadPerLetter()
        {
            if (PerXSlider == null || PerYSlider == null) return;
            _perLoading = true;
            Cur.Per.TryGetValue(_perCp, out var v);
            PerXSlider.Value = v.x; PerYSlider.Value = v.y;
            _perLoading = false;
            PerXVal.Text = $"{v.x * 100:+0;-0;0}%"; PerYVal.Text = $"{v.y * 100:+0;-0;0}%";
        }

        private void PerLetter_Checked(object sender, RoutedEventArgs e)
        {
            var t = (string)((FrameworkElement)sender).Tag;
            _perCp = char.ConvertToUtf32(t, 0);
            if (IsLoaded) LoadPerLetter();
        }

        private void PerLetter_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _perLoading) return;
            double x = PerXSlider.Value, y = PerYSlider.Value;
            PerXVal.Text = $"{x * 100:+0;-0;0}%"; PerYVal.Text = $"{y * 100:+0;-0;0}%";
            if (Math.Abs(x) < 1e-6 && Math.Abs(y) < 1e-6) Cur.Per.Remove(_perCp); else Cur.Per[_perCp] = (x, y);
            if (!_ready || _loading) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void ShowGlyphBoxes()
        {
            CaronGlyphText.Text = _caronCh; AcuteGlyphText.Text = _acuteCh;
            CaronGlyphText.FontFamily = AcuteGlyphText.FontFamily = Font.Family;
            CaronGlyphLabel.Text = _caronCh == "" ? "Automatski" : "Ručno: " + _caronCh;
            AcuteGlyphLabel.Text = _acuteCh == "" ? "Automatski" : "Ručno: " + _acuteCh;
        }

        private static double _zoom = 1;   // pamti se dok je aplikacija otvorena

        private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PreviewBig == null || PreviewCaps == null || ZoomVal == null) return;
            _zoom = ZoomSlider.Value;
            PreviewBig.FontSize = 84 * _zoom; PreviewWords.FontSize = 40 * _zoom;
            PreviewSmall.FontSize = 26 * _zoom; PreviewCaps.FontSize = 26 * _zoom;
            ZoomVal.Text = $"{_zoom * 100:0}%";
        }

        private void SetFace(GlyphTypeface face)
        {
            PreviewBig.Face = face; PreviewWords.Face = face; PreviewSmall.Face = face; PreviewCaps.Face = face;
        }

        private void ClearGlyph_Click(object sender, RoutedEventArgs e)
        {
            if ((string)((FrameworkElement)sender).Tag == "Caron") _caronCh = ""; else _acuteCh = "";
            ShowGlyphBoxes();
            if (_ready && !_loading) Regenerate();
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
            _caronCh = s.CaronCh ?? ""; _acuteCh = s.AcuteCh ?? ""; ShowGlyphBoxes();
            CaronSSlider.Value = s.CaronS; AcuteSSlider.Value = s.AcuteS;
            CaronRSlider.Value = s.CaronR; AcuteRSlider.Value = s.AcuteR;
            CaronWSlider.Value = s.CaronW; AcuteWSlider.Value = s.AcuteW;
            CaronTSlider.Value = s.CaronT; AcuteTSlider.Value = s.AcuteT;
            CaronFlipCheck.IsChecked = s.CaronFlip; AcuteFlipCheck.IsChecked = s.AcuteFlip;
            LoadPerLetter();
            _loading = false;

            TitleText.Text = Font.Name;
            NavText.Text = $"{_index + 1} / {_fonts.Count}";
            PrevButton.IsEnabled = _index > 0;
            NextButton.IsEnabled = _index < _fonts.Count - 1;
            ControlsPanel.IsEnabled = true;
            s.Failed = false;
            InfoText.Foreground = (Brush)FindResource("SubTextBrush");
            SetFace(null);

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
            s.CaronCh = _caronCh; s.AcuteCh = _acuteCh;
            s.CaronS = CaronSSlider.Value; s.AcuteS = AcuteSSlider.Value;
            s.CaronR = CaronRSlider.Value; s.AcuteR = AcuteRSlider.Value;
            s.CaronW = CaronWSlider.Value; s.AcuteW = AcuteWSlider.Value;
            s.CaronT = CaronTSlider.Value; s.AcuteT = AcuteTSlider.Value;
            s.CaronFlip = CaronFlipCheck.IsChecked == true; s.AcuteFlip = AcuteFlipCheck.IsChecked == true;
        }

        private static int FirstCp(string t) => string.IsNullOrEmpty(t) ? 0 : char.ConvertToUtf32(t, 0);

        private void PickGlyph_Click(object sender, RoutedEventArgs e)
        {
            bool caron = (string)((FrameworkElement)sender).Tag == "Caron";
            var pick = new GlyphPickerWindow(Font, caron ? "Odaberi znak za kvačicu" : "Odaberi znak za akcent") { Owner = this };
            if (pick.ShowDialog() != true || string.IsNullOrEmpty(pick.Picked)) return;
            if (caron) _caronCh = pick.Picked; else _acuteCh = pick.Picked;
            ShowGlyphBoxes();
            if (_ready && !_loading) Regenerate();
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
            UseEth = !s.OwnD,
            CaronGlyph = SafeCp(s.CaronCh), CaronScale = s.CaronS, CaronRotate = s.CaronR, CaronFlip = s.CaronFlip,
            CaronWidth = s.CaronW, CaronThickness = s.CaronT * upem,
            AcuteGlyph = SafeCp(s.AcuteCh), AcuteScale = s.AcuteS, AcuteRotate = s.AcuteR, AcuteFlip = s.AcuteFlip,
            AcuteWidth = s.AcuteW, AcuteThickness = s.AcuteT * upem,
            PerGlyph = s.Per.ToDictionary(k => k.Key, k => (k.Value.x * upem, k.Value.y * upem))
        };

        private static int SafeCp(string t)
        {
            try { return FirstCp(t); } catch { return 0; }
        }

        private void UpdateLabels()
        {
            GapVal.Text = $"{GapSlider.Value * 100:+0;-0;0}%";
            AccXVal.Text = $"{AccXSlider.Value * 100:+0;-0;0}%";
            BarYVal.Text = $"{BarYSlider.Value * 100:+0;-0;0}%";
            BarYUVal.Text = $"{BarYUSlider.Value * 100:+0;-0;0}%";
            BarXVal.Text = $"{BarXSlider.Value * 100:+0;-0;0}%";
            BarWVal.Text = $"{BarWSlider.Value * 100:0}%";
            BarTVal.Text = $"{BarTSlider.Value * 100:0}%";
            CaronSVal.Text = $"{CaronSSlider.Value * 100:0}%";
            AcuteSVal.Text = $"{AcuteSSlider.Value * 100:0}%";
            CaronWVal.Text = $"{CaronWSlider.Value * 100:0}%";
            AcuteWVal.Text = $"{AcuteWSlider.Value * 100:0}%";
            CaronTVal.Text = $"{CaronTSlider.Value * 1000:+0;-0;0}";
            AcuteTVal.Text = $"{AcuteTSlider.Value * 1000:+0;-0;0}";
            CaronRVal.Text = $"{CaronRSlider.Value:+0;-0;0}°";
            AcuteRVal.Text = $"{AcuteRSlider.Value:+0;-0;0}°";
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
                SetFace(new GlyphTypeface(new Uri(path)));

                Cur.Nothing = r.Added.Count == 0;
                var added = r.Added.Count > 0 ? "Dodaju se: " + string.Join(" ", r.Added)
                    : r.Skipped.Count > 0 ? "Zasad se ništa ne dodaje." : "Font već ima sve hrvatske znakove.";
                var skipped = "";
                if (r.Skipped.Any(x => x.Contains("nema kvačice"))) skipped += "\nFont nema kvačicu — pod „Kvačica” odaberi zamjenski znak (npr. v).";
                if (r.Skipped.Any(x => x.Contains("nema akcenta"))) skipped += "\nFont nema akcent — pod „Akcent” odaberi zamjenski znak (npr. I, l ili /) i zakreni ga.";
                var other = r.Skipped.Where(x => !x.Contains("nema kvačice") && !x.Contains("nema akcenta")).ToList();
                if (other.Count > 0) skipped += "\nNije moguće dodati: " + string.Join(", ", other);
                if (Cur.Saved) added = "✓ Spremljeno. " + added;
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
            SaveButton.Content = Cur.Saved ? "Spremi ponovno" : "Spremi ovaj font";
            SaveButton.IsEnabled = !Cur.Failed && !Cur.Nothing;
            if (!_batch) return;
            int n = _settings.Values.Count(s => s.Include && !s.Failed && !s.Nothing && !s.Saved);
            SaveAllButton.Content = $"Spremi sve uključene ({n})";
            SaveAllButton.IsEnabled = n > 0;
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (Cur.Failed) return;
            _ready = false;
            GapSlider.Value = 0; AccXSlider.Value = 0;
            BarYSlider.Value = 0; BarYUSlider.Value = 0; BarXSlider.Value = 0;
            BarWSlider.Value = 1; BarTSlider.Value = 1;
            OwnDCheck.IsChecked = false;
            _caronCh = ""; _acuteCh = ""; ShowGlyphBoxes();
            CaronSSlider.Value = 1; AcuteSSlider.Value = 1; CaronRSlider.Value = 0; AcuteRSlider.Value = 0;
            CaronWSlider.Value = 1; AcuteWSlider.Value = 1; CaronTSlider.Value = 0; AcuteTSlider.Value = 0;
            CaronFlipCheck.IsChecked = false; AcuteFlipCheck.IsChecked = false;
            Cur.Per.Clear(); LoadPerLetter();
            _ready = true;
            Regenerate();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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

        /// <summary>Spremi trenutni font ili sve uključene; vraća true ako je bar jedan spremljen.</summary>
        public async Task<bool> SaveAsync(bool all)
        {
            StoreCurrent();
            SaveButton.IsEnabled = SaveAllButton.IsEnabled = false;
            PrevButton.IsEnabled = NextButton.IsEnabled = false;
            var current = Font;
            var jobs = all
                ? _fonts.Where(f => _settings[f].Include && !_settings[f].Failed && !_settings[f].Nothing && !_settings[f].Saved).ToList()
                : new List<FontItem> { current };
            var errors = new List<string>();
            int done = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var f = jobs[i];
                if (jobs.Count > 1) SaveAllButton.Content = $"Spremam… {i + 1}/{jobs.Count}";
                var s = _settings[f];
                var files = FontAnalyzer.AllFiles(f).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var prefer = f == current ? _previewFile : f.MainFile;
                Diag.Log($"spremanje: {f.Name}, {files.Count} datoteka");
                var (family, saved, errs) = await Task.Run(() => PatchFiles(files, s, prefer, ReplacedDirs));
                if (saved > 0)
                {
                    done++;
                    s.Saved = true;
                    SavedFamily = family;
                }
                else if (jobs.Count > 1 && errs.Count > 0) errors.Add(f.Name + ": " + errs[0]);
                if (jobs.Count == 1) errors.AddRange(errs);
            }
            Errors = errors;
            SavedFonts += done;
            PrevButton.IsEnabled = _index > 0;
            NextButton.IsEnabled = _index < _fonts.Count - 1;
            UpdateSaveButton();
            if (done == 0)
            {
                InfoText.Text = "Spremanje nije uspjelo.\n" + string.Join("\n", errors.Take(6));
                InfoText.Foreground = (Brush)FindResource("WarnBrush");
                return false;
            }
            return true;
        }

        private async Task SaveAndReport(bool all)
        {
            try
            {
                if (!await SaveAsync(all)) return;
                if (Errors.Count > 0)
                    MessageBox.Show(this, "Nije sve spremljeno:\n\n" + string.Join("\n", Errors.Take(12)), "FontShelf",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                if (all || _fonts.Count == 1) { Close(); return; }
                // nakon spremanja prijeđi na sljedeći font
                if (_index < _fonts.Count - 1) Go(1);
                else Regenerate();
            }
            catch (Exception ex)
            {
                Diag.Log("spremanje: " + ex);
                InfoText.Text = "Spremanje nije uspjelo: " + ex.Message;
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAndReport(false);
        private async void SaveAll_Click(object sender, RoutedEventArgs e) => await SaveAndReport(true);

        // ---- za automatski test
        public bool IsReady => _ready;
        public int Index => _index;
        public void SetForTest(double gap, double accX, double barY, double barW, bool ownD)
        {
            GapSlider.Value = gap; AccXSlider.Value = accX; BarYSlider.Value = barY; BarYUSlider.Value = barY;
            BarWSlider.Value = barW; OwnDCheck.IsChecked = ownD;
        }
        public void GoForTest(int delta) => Go(delta);
        public void SetManualForTest(string caron, double cs, double cr, string acute, double asc, double ar)
        {
            CaronWSlider.Value = 1.4; CaronTSlider.Value = 0.03; AcuteWSlider.Value = 0.8; AcuteTSlider.Value = 0.02;
            _caronCh = caron; CaronSSlider.Value = cs; CaronRSlider.Value = cr;
            _acuteCh = acute; AcuteSSlider.Value = asc; AcuteRSlider.Value = ar;
            ShowGlyphBoxes();
            PerXSlider.Value = 0.05; PerYSlider.Value = 0.04;
        }
    }
}
