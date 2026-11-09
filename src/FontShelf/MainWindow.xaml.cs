using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using FontShelf.Models;
using FontShelf.Services;
using FontShelf.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace FontShelf
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new MainViewModel();
        private bool _syncing;

        private readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };

        public MainWindow()
        {
            // Mica pozadina samo na Windows 11 22H2+ (inače puna boja)
            ThemeManager.UseMica = Environment.OSVersion.Version.Build >= 22621;
            _vm.ApplyInitialTheme();
            InitializeComponent();
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 48,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = ThemeManager.UseMica ? new Thickness(-1) : new Thickness(0),
                CornerRadius = new CornerRadius(8),
                UseAeroCaptionButtons = false
            });
            DataContext = _vm;
            _toastTimer.Tick += (s, e) =>
            {
                if (_vm.IsBusy) return;
                _toastTimer.Stop();
                Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
            };
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            _vm.RequestSelectAll += SelectAllEntry;
            _vm.ThemeChanged += () =>
            {
                ApplyTitleBar();
                _ = UpdateTesterAsync();
            };
            _vm.PropertyChanged += Vm_PropertyChanged;
        }

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedFont) || e.PropertyName == nameof(MainViewModel.ShowTester))
                _ = UpdateTesterAsync();
            else if (e.PropertyName == nameof(MainViewModel.StatusText))
                ShowToast();
            else if (e.PropertyName == nameof(MainViewModel.IsBusy))
            {
                if (_vm.IsBusy)
                {
                    SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
                    ShowToast();
                }
                else SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        }

        private void ShowToast()
        {
            if (string.IsNullOrWhiteSpace(_vm.StatusText)) return;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General && _vm.ThemeMode == 0)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_vm.EffectiveLight != ThemeManager.IsLight) _vm.ApplyTheme();
                }));
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await _vm.InitializeAsync();
            if (LibraryList.SelectedItem == null && TagList.SelectedItem == null && FolderList.SelectedItem == null)
                SelectAllEntry();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            _vm.OnClosing();
        }

        #region Prozor i naslovna traka

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int GWL_STYLE = -16;
        private const int WS_SYSMENU = 0x00080000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private IntPtr Handle => new WindowInteropHelper(this).Handle;

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                int round = 2;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch { }

            if (ThemeManager.UseMica)
            {
                try
                {
                    var src = HwndSource.FromHwnd(Handle);
                    if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
                    int mica = 2; // DWMSBT_MAINWINDOW
                    if (DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int)) != 0)
                        DisableMica();
                    else
                    {
                        // Bez sistemskog izbornika Windows ne crta svoje gumbe ispod naših
                        int style = GetWindowLong(Handle, GWL_STYLE);
                        SetWindowLong(Handle, GWL_STYLE, style & ~WS_SYSMENU);
                    }
                }
                catch { DisableMica(); }
            }
            ApplyTitleBar();
        }

        private void DisableMica()
        {
            ThemeManager.UseMica = false;
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null) chrome.GlassFrameThickness = new Thickness(0);
            ThemeManager.Apply(ThemeManager.IsLight);
        }

        private void ApplyTitleBar()
        {
            try
            {
                if (Handle == IntPtr.Zero) return;
                int dark = ThemeManager.IsLight ? 0 : 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            bool max = WindowState == WindowState.Maximized;
            RootGrid.Margin = max ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = max ? "\uE923" : "\uE922";
            MaxButton.ToolTip = max ? "Vrati" : "Povećaj";
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void OpenReleases_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/deganegane/fontshelf/releases/latest") { UseShellExecute = true }); }
            catch { }
        }

        private void Settings_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

        private void More_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.ContextMenu != null)
            {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.Placement = PlacementMode.Bottom;
                b.ContextMenu.DataContext = _vm;
                b.ContextMenu.IsOpen = true;
            }
        }

        #endregion

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
            {
                _vm.SearchText = "";
                e.Handled = true;
            }
        }

        private void SelectAllEntry()
        {
            _syncing = true;
            TagList.SelectedItem = null;
            FolderList.SelectedItem = null;
            _syncing = false;
            LibraryList.SelectedIndex = 0;
        }

        private static T Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

        private void SafeCopy(string text, string status)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetText(text);
                _vm.StatusText = status;
            }
            catch { _vm.StatusText = "Međuspremnik je zauzet, pokušaj ponovno."; }
        }

        private string PickFolder(string title)
        {
            var dlg = new OpenFolderDialog { Title = title };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }

        #region Bočna traka

        private void Sidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing) return;
            var lb = (ListBox)sender;
            if (lb.SelectedItem is SidebarEntry entry)
            {
                _syncing = true;
                foreach (var other in new[] { LibraryList, TagList, FolderList })
                    if (other != lb) other.SelectedItem = null;
                _syncing = false;
                _vm.SelectedEntry = entry;
            }
            else if (LibraryList.SelectedItem == null && TagList.SelectedItem == null && FolderList.SelectedItem == null
                     && LibraryList.Items.Count > 0)
            {
                Dispatcher.BeginInvoke(new Action(SelectAllEntry));
            }
        }

        private void Sidebar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            var ev = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            ((sender as FrameworkElement)?.Parent as UIElement)?.RaiseEvent(ev);
        }

        private void NewTag_Click(object sender, RoutedEventArgs e)
        {
            NewTagBox.Visibility = Visibility.Visible;
            NewTagBox.Focus();
        }

        private void NewTagBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NewTagBox.Text)) NewTagBox.Visibility = Visibility.Collapsed;
        }

        private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                NewTagBox.Text = "";
                NewTagBox.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            var name = NewTagBox.Text;
            if (_vm.CreateTag(name))
            {
                NewTagBox.Text = "";
                NewTagBox.Visibility = Visibility.Collapsed;
            }
            else if (!string.IsNullOrWhiteSpace(name)) _vm.StatusText = "Tag s tim imenom već postoji.";
            e.Handled = true;
        }

        private void DeleteTag_Click(object sender, RoutedEventArgs e)
        {
            var entry = Ctx<SidebarEntry>(sender);
            if (entry == null) return;
            e.Handled = true;
            var r = MessageBox.Show(this, $"Obrisati tag „{entry.Title}”? Fontovi ostaju, samo se makne oznaka.",
                "Obriši tag", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.OK) _vm.DeleteTag(entry.Value);
        }

        private async void ExportTag_Click(object sender, RoutedEventArgs e)
        {
            var entry = Ctx<SidebarEntry>(sender);
            if (entry == null) return;
            e.Handled = true;
            var fonts = _vm.FontsWithTag(entry.Value);
            if (fonts.Count == 0) { _vm.StatusText = "Tag nema nijedan font."; return; }
            var dir = PickFolder($"Izvoz fontova s tagom „{entry.Title}”");
            if (dir != null) await _vm.ExportAsync(fonts, dir);
        }

        private async void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Odaberi mapu s fontovima", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FolderNames) await _vm.AddFolderAsync(f);
        }

        private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            var entry = Ctx<SidebarEntry>(sender);
            if (entry == null) return;
            e.Handled = true;
            var r = MessageBox.Show(this,
                $"Ukloniti mapu „{entry.Title}” iz biblioteke?\n\nDatoteke ostaju na disku, a aktivirani fontovi iz nje se deaktiviraju.",
                "Ukloni mapu", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.OK) await _vm.RemoveFolderAsync(entry.Value);
        }

        private async void ToggleFolderActivation_Click(object sender, RoutedEventArgs e)
        {
            var entry = Ctx<SidebarEntry>(sender);
            if (entry == null) return;
            e.Handled = true;
            await _vm.ActivateFolderAsync(entry.Value, !_vm.IsFolderFullyActive(entry.Value));
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] paths)) return;
            var folders = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) folders.Add(p);
                else if (File.Exists(p) && FontLoader.IsFontFile(p)) folders.Add(Path.GetDirectoryName(p));
            }
            foreach (var f in folders.Distinct(StringComparer.OrdinalIgnoreCase)) await _vm.AddFolderAsync(f);
        }

        #endregion

        #region Glavni prikaz

        private void Sort_Click(object sender, RoutedEventArgs e) => _vm.CycleSort();

        private void ResetFilters_Click(object sender, RoutedEventArgs e) => _vm.ResetFilters();

        private void ResetRange_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string t) _vm.ResetRange(t);
        }

        private void Chip_Click(object sender, RoutedEventArgs e) => _vm.ChipsChanged();

        private void GridHost_SizeChanged(object sender, SizeChangedEventArgs e) => _vm.SetGridWidth(e.NewSize.Width);

        private void GridCell_Click(object sender, RoutedEventArgs e)
        {
            var f = Ctx<FontItem>(sender);
            if (f != null) _vm.SelectedFont = f;
        }

        private async void RowActivate_Click(object sender, RoutedEventArgs e)
        {
            var f = Ctx<FontItem>(sender);
            if (f != null) await _vm.ToggleActivationAsync(f);
        }

        private void CompareToggle_Click(object sender, RoutedEventArgs e)
        {
            var f = Ctx<FontItem>(sender);
            if (f != null) _vm.OnCompareChanged(f);
        }

        private void FavoriteToggle_Click(object sender, RoutedEventArgs e)
        {
            var f = Ctx<FontItem>(sender);
            if (f != null) _vm.OnFavoriteChanged(f);
        }

        private void RemoveCompare_Click(object sender, RoutedEventArgs e)
        {
            var f = Ctx<FontItem>(sender);
            if (f != null) _vm.RemoveFromCompare(f);
        }

        private void ClearCompare_Click(object sender, RoutedEventArgs e) => _vm.ClearCompare();

        private void SwapCompare_Click(object sender, RoutedEventArgs e) => _vm.SwapCompare();

        #endregion

        #region Google Fonts

        private async void RefreshGoogle_Click(object sender, RoutedEventArgs e) => await _vm.EnsureGoogleAsync(true);

        private void GoogleCategory_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe) _vm.GoogleCategory = fe.Tag as string ?? "";
        }

        private async void DownloadGoogle_Click(object sender, RoutedEventArgs e)
        {
            var g = Ctx<GoogleFontEntry>(sender);
            if (g != null) await _vm.ToggleGoogleAsync(g);
        }

        private void GoogleRow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is GoogleFontEntry g) _vm.EnsureGooglePreview(g);
        }

        private void GoogleRow_Loaded(object sender, RoutedEventArgs e)
        {
            var g = Ctx<GoogleFontEntry>(sender);
            if (g != null) _vm.EnsureGooglePreview(g);
        }

        private void ShowGoogleFont_Click(object sender, RoutedEventArgs e)
        {
            var g = Ctx<GoogleFontEntry>(sender);
            if (g == null) return;
            var f = _vm.FindDownloadedGoogleFont(g);
            if (f == null) { _vm.StatusText = "Font još nije učitan u biblioteku."; return; }
            _vm.SearchText = "";
            SelectAllEntry();
            _vm.IsListMode = true;
            _vm.Refresh();
            _vm.SelectedFont = f;
            Dispatcher.BeginInvoke(new Action(() => FontList.ScrollIntoView(f)));
        }

        #endregion

        #region Detalji

        private void DetailFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedFont != null) _vm.OnFavoriteChanged(_vm.SelectedFont);
        }

        private void DetailCompare_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedFont != null) _vm.OnCompareChanged(_vm.SelectedFont);
        }

        private async void Activate_Click(object sender, RoutedEventArgs e)
            => await _vm.ToggleActivationAsync(_vm.SelectedFont);

        private async void ToggleStyle_Click(object sender, RoutedEventArgs e)
        {
            var s = Ctx<FontStyleItem>(sender);
            if (s != null) await _vm.ToggleStyleActivationAsync(_vm.SelectedFont, s);
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            var f = _vm.SelectedFont;
            if (f == null) return;
            var r = MessageBox.Show(this,
                $"Trajno instalirati „{f.Name}” za tvoj korisnički račun?\n\nFont će biti dostupan i nakon ponovnog pokretanja računala.",
                "Instaliraj font", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.OK) await _vm.InstallAsync(f);
        }

        private async void ExportFont_Click(object sender, RoutedEventArgs e)
        {
            var f = _vm.SelectedFont;
            if (f == null) return;
            var dir = PickFolder($"Izvoz fonta „{f.Name}”");
            if (dir != null) await _vm.ExportAsync(new[] { f }, dir);
        }

        private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            var f = _vm.SelectedFont;
            if (f == null) return;
            var file = f.FirstFile ?? f.MainFile ?? FontAnalyzer.AllFiles(f).FirstOrDefault();
            if (file == null || !File.Exists(file)) { _vm.StatusText = "Datoteku fonta nije moguće pronaći."; return; }
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
            catch { }
        }

        private void CopyName_Click(object sender, RoutedEventArgs e) => SafeCopy(_vm.SelectedFont?.Name, "Naziv fonta kopiran.");

        private void AddTagBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            _vm.AddTagToFont(_vm.SelectedFont, AddTagBox.Text);
            AddTagBox.Text = "";
            e.Handled = true;
        }

        private void RemoveTagFromFont_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string tag) _vm.RemoveTagFromFont(_vm.SelectedFont, tag);
        }

        private void AddSuggestedTag_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string tag) _vm.AddTagToFont(_vm.SelectedFont, tag);
        }

        #endregion

        #region Glifovi

        private void Glyph_Click(object sender, RoutedEventArgs e)
        {
            var g = Ctx<GlyphItem>(sender);
            if (g != null) _vm.SelectedGlyph = g;
        }

        private void CopyGlyph_Click(object sender, RoutedEventArgs e) => SafeCopy(_vm.SelectedGlyph?.Text, "Znak kopiran.");

        private void CopyGlyphCode_Click(object sender, RoutedEventArgs e) => SafeCopy(_vm.SelectedGlyph?.Code, "Kod znaka kopiran.");

        private void CopyAllGlyphs_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.SelectedFont == null) return;
            var all = FontAnalyzer.AllCharacters(_vm.SelectedFont);
            SafeCopy(all, $"Kopirano {all.Length} znakova.");
        }

        private string AskSavePath(string ext, string filter)
        {
            var f = _vm.SelectedFont;
            var g = _vm.SelectedGlyph;
            if (f == null || g == null) return null;
            var safe = new string(f.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            var dlg = new SaveFileDialog
            {
                FileName = $"{safe}-U+{g.CodePoint:X4}.{ext}",
                Filter = filter,
                DefaultExt = ext
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }

        private void SaveGlyphSvg_Click(object sender, RoutedEventArgs e)
        {
            var path = AskSavePath("svg", "SVG slika|*.svg");
            if (path == null) return;
            try
            {
                GlyphExporter.SaveSvg(_vm.SelectedFont, _vm.SelectedGlyph.CodePoint, path);
                _vm.StatusText = "Znak spremljen kao SVG.";
            }
            catch (Exception ex) { _vm.StatusText = "Spremanje nije uspjelo: " + ex.Message; }
        }

        private void SaveGlyphPng_Click(object sender, RoutedEventArgs e)
        {
            var path = AskSavePath("png", "PNG slika|*.png");
            if (path == null) return;
            try
            {
                GlyphExporter.SavePng(_vm.SelectedFont, _vm.SelectedGlyph.CodePoint, path);
                _vm.StatusText = "Znak spremljen kao PNG.";
            }
            catch (Exception ex) { _vm.StatusText = "Spremanje nije uspjelo: " + ex.Message; }
        }

        #endregion

        #region Tester (WebView2)

        private CoreWebView2Environment _env;
        private Task _initTask;
        private TesterPage.Prepared _tester;
        private int _nav;

        private async Task InitWebViewAsync()
        {
            Directory.CreateDirectory(AppPaths.Local);
            _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Local, "WebView2"));
            await TesterView.EnsureCoreWebView2Async(_env);
            TesterView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            var core = TesterView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.AddWebResourceRequestedFilter("https://fontshelf.local/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnTesterRequest;
        }

        private Dictionary<string, string> TesterColors() => new Dictionary<string, string>
        {
            ["bg"] = ThemeManager.Css("PanelBrush"),
            ["card"] = ThemeManager.Css("CardBrush"),
            ["text"] = ThemeManager.Css("TextBrush"),
            ["sub"] = ThemeManager.Css("SubTextBrush"),
            ["line"] = ThemeManager.Css("LineBrush"),
            ["accent"] = ThemeManager.Css("AccentBrush"),
            ["input"] = ThemeManager.Css("InputBrush")
        };

        private async Task UpdateTesterAsync()
        {
            if (!_vm.ShowTester || _vm.SelectedFont == null) return;
            try
            {
                _initTask ??= InitWebViewAsync();
                await _initTask;

                var font = _vm.SelectedFont;
                var text = _vm.EffectivePreviewText;
                var size = Math.Min(Math.Max(_vm.PreviewSize, 24), 96);
                var colors = TesterColors();
                var prepared = await Task.Run(() => TesterPage.Build(font, text, size, colors));
                if (font != _vm.SelectedFont) return;

                _tester = prepared;
                TesterMessage.Visibility = Visibility.Collapsed;
                TesterView.Visibility = Visibility.Visible;
                TesterView.CoreWebView2.Navigate($"https://fontshelf.local/tester.html?n={++_nav}");
            }
            catch (Exception ex)
            {
                _initTask = null;
                TesterView.Visibility = Visibility.Collapsed;
                TesterMessage.Visibility = Visibility.Visible;
                TesterMessage.Text = "Tester treba Microsoft Edge WebView2 Runtime (ugrađen je u Windows 10/11, a može se " +
                                     "preuzeti s Microsoftove stranice).\n\n" + ex.Message;
            }
        }

        private void OnTesterRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var t = _tester;
            if (t == null || _env == null) return;
            try
            {
                var path = new Uri(e.Request.Uri).AbsolutePath;
                if (path == "/tester.html")
                {
                    e.Response = _env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(t.Html)), 200, "OK",
                        "Content-Type: text/html; charset=utf-8");
                }
                else if (path.StartsWith("/font/") && int.TryParse(path.Substring(6), out var i) && i >= 0 && i < t.Files.Count)
                {
                    var bytes = File.ReadAllBytes(t.Files[i]);
                    var type = t.Files[i].EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? "font/otf" : "font/ttf";
                    e.Response = _env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                        $"Content-Type: {type}\r\nAccess-Control-Allow-Origin: *");
                }
                else
                {
                    e.Response = _env.CreateWebResourceResponse(null, 404, "Not Found", "");
                }
            }
            catch
            {
                e.Response = _env.CreateWebResourceResponse(null, 404, "Not Found", "");
            }
        }

        #endregion
    }
}
