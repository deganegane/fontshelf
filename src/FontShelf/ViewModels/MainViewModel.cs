using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using FontShelf.Models;
using FontShelf.Services;

namespace FontShelf.ViewModels
{
    public class FontComparer : IComparer
    {
        private readonly SortMode _mode;
        public FontComparer(SortMode mode) { _mode = mode; }

        public int Compare(object x, object y)
        {
            var a = (FontItem)x;
            var b = (FontItem)y;
            int byName = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            switch (_mode)
            {
                case SortMode.NameDesc: return -byName;
                case SortMode.Styles:
                    {
                        int c = b.StyleCount.CompareTo(a.StyleCount);
                        return c != 0 ? c : byName;
                    }
                case SortMode.Recent:
                    {
                        int c = b.AddedIndex.CompareTo(a.AddedIndex);
                        return c != 0 ? c : byName;
                    }
                case SortMode.FavoritesFirst:
                    {
                        int c = b.IsFavorite.CompareTo(a.IsFavorite);
                        return c != 0 ? c : byName;
                    }
                default: return byName;
            }
        }
    }

    public class MainViewModel : ObservableObject
    {
        public const string DefaultPangram = "Gojazni đačić s biciklom drži hmelj i finu vatu u džepu nošnje.";
        public const string UpperSample = "ABCČĆDĐEFGHIJKLMNOPQRSŠTUVWXYZŽ";
        public const string LowerSample = "abcčćdđefghijklmnopqrsštuvwxyzž";
        public const string DigitSample = "0123456789  !?&@#%*()[]{}€$£.,;:–—«»„“";
        public const string ParagraphSample =
            "Tipografija je umijeće slaganja slova tako da tekst bude čitljiv, jasan i lijep. " +
            "Dobar font prenosi ton poruke i prije nego što pročitamo ijednu riječ — zato ga vrijedi " +
            "isprobati na pravom tekstu, u pravoj veličini.";

        private readonly List<FontItem> _all = new List<FontItem>();
        private LibraryData _lib = new LibraryData();
        private readonly DispatcherTimer _searchTimer;
        private readonly DispatcherTimer _saveTimer;
        private int _addedCounter;

        public ObservableCollection<SidebarEntry> LibraryEntries { get; } = new ObservableCollection<SidebarEntry>();
        public ObservableCollection<SidebarEntry> TagEntries { get; } = new ObservableCollection<SidebarEntry>();
        public ObservableCollection<SidebarEntry> FolderEntries { get; } = new ObservableCollection<SidebarEntry>();
        public ObservableCollection<FontItem> CompareFonts { get; } = new ObservableCollection<FontItem>();
        public ObservableCollection<string> SuggestedTags { get; } = new ObservableCollection<string>();

        public string Upper => UpperSample;
        public string Lower => LowerSample;
        public string Digits => DigitSample;
        public string Paragraph => ParagraphSample;

        public event Action RequestSelectAll;
        public event Action ThemeChanged;

        public MainViewModel()
        {
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); Refresh(); RefreshGoogle(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); LibraryStore.Save(_lib); };

            CompareFonts.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasCompare));
                OnPropertyChanged(nameof(HasTwoCompare));
                OnPropertyChanged(nameof(CompareTabText));
                OnPropertyChanged(nameof(CompareA));
                OnPropertyChanged(nameof(CompareB));
            };
            FolderEntries.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasFolders));
            TagEntries.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasTags));
        }

        #region Osnovna svojstva

        private ICollectionView _fontsView;
        public ICollectionView FontsView { get => _fontsView; private set => Set(ref _fontsView, value); }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (Set(ref _searchText, value ?? ""))
                {
                    _searchTimer.Stop();
                    _searchTimer.Start();
                }
            }
        }

        private string _previewText = "";
        public string PreviewText
        {
            get => _previewText;
            set
            {
                if (Set(ref _previewText, value ?? ""))
                {
                    OnPropertyChanged(nameof(EffectivePreviewText));
                    OnPropertyChanged(nameof(CardText));
                    _lib.PreviewText = _previewText;
                    ScheduleSave();
                }
            }
        }

        public string EffectivePreviewText => string.IsNullOrWhiteSpace(PreviewText) ? DefaultPangram : PreviewText;

        private double _previewSize = 40;
        public double PreviewSize
        {
            get => _previewSize;
            set
            {
                var v = Math.Round(value);
                if (Set(ref _previewSize, v))
                {
                    OnPropertyChanged(nameof(PreviewSizeText));
                    OnPropertyChanged(nameof(CardFontSize));
                    OnPropertyChanged(nameof(OverlaySize));
                    _lib.PreviewSize = v;
                    ScheduleSave();
                }
            }
        }
        public string PreviewSizeText => $"{PreviewSize:0} pt";
        public double OverlaySize => Math.Max(60, PreviewSize * 2.4);

        private FontItem _selectedFont;
        public FontItem SelectedFont
        {
            get => _selectedFont;
            set
            {
                var old = _selectedFont;
                if (Set(ref _selectedFont, value))
                {
                    if (old != null) old.IsSelected = false;
                    if (value != null) value.IsSelected = true;
                    UpdateSuggestedTags();
                    if (ShowGlyphs) LoadGlyphs();
                    else { Glyphs = null; GlyphInfo = ""; SelectedGlyph = null; }
                }
            }
        }

        private SidebarEntry _selectedEntry;
        public SidebarEntry SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (Set(ref _selectedEntry, value))
                {
                    OnPropertyChanged(nameof(HeaderTitle));
                    if (View == ViewMode.Compare) View = ViewMode.List;
                    RaiseViewProps();
                    Refresh();
                    if (IsGoogleMode) _ = EnsureGoogleAsync();
                }
            }
        }

        public string HeaderTitle => SelectedEntry?.Title ?? "Svi fontovi";

        private int _visibleCount;
        public int VisibleCount
        {
            get => _visibleCount;
            private set { if (Set(ref _visibleCount, value)) OnPropertyChanged(nameof(CountText)); }
        }
        public string CountText => IsGoogleMode ? Plural.Fonts(GoogleVisibleCount) : Plural.Fonts(VisibleCount);

        public bool HasCompare => CompareFonts.Count > 0;
        public bool HasTwoCompare => CompareFonts.Count > 1;
        public string CompareTabText => CompareFonts.Count > 0 ? $"Usporedba ({CompareFonts.Count})" : "Usporedba";
        public FontItem CompareA => CompareFonts.Count > 0 ? CompareFonts[0] : null;
        public FontItem CompareB => CompareFonts.Count > 1 ? CompareFonts[1] : null;
        public bool HasFolders => FolderEntries.Count > 0;
        public bool HasTags => TagEntries.Count > 0;

        private string _statusText = "";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

        #endregion

        #region Prikazi

        public ViewMode View
        {
            get => _lib.View;
            set
            {
                if (_lib.View == value) return;
                _lib.View = value;
                RaiseViewProps();
                if (value == ViewMode.Grid) RebuildGrid();
                ScheduleSave();
            }
        }

        private void RaiseViewProps()
        {
            foreach (var p in new[] { nameof(View), nameof(IsListMode), nameof(IsGridMode), nameof(IsTableMode), nameof(IsCompareMode),
                         nameof(ShowList), nameof(ShowGrid), nameof(ShowTable), nameof(ShowCompare), nameof(IsGoogleMode),
                         nameof(IsLibraryMode), nameof(CountText) })
                OnPropertyChanged(p);
        }

        public bool IsListMode { get => View == ViewMode.List; set { if (value) View = ViewMode.List; } }
        public bool IsGridMode { get => View == ViewMode.Grid; set { if (value) View = ViewMode.Grid; } }
        public bool IsTableMode { get => View == ViewMode.Table; set { if (value) View = ViewMode.Table; } }
        public bool IsCompareMode { get => View == ViewMode.Compare; set { if (value) View = ViewMode.Compare; } }

        public bool IsGoogleMode => SelectedEntry?.Kind == SidebarKind.Google;
        public bool IsLibraryMode => !IsGoogleMode;
        public bool ShowList => !IsGoogleMode && View == ViewMode.List;
        public bool ShowGrid => !IsGoogleMode && View == ViewMode.Grid;
        public bool ShowTable => !IsGoogleMode && View == ViewMode.Table;
        public bool ShowCompare => !IsGoogleMode && View == ViewMode.Compare;

        // Vrsta pregleda na karticama
        public PreviewMode Preview
        {
            get => _lib.Preview;
            set
            {
                if (_lib.Preview == value) return;
                _lib.Preview = value;
                foreach (var p in new[] { nameof(Preview), nameof(IsLinePreview), nameof(IsParagraphPreview), nameof(IsAlphabetPreview),
                             nameof(CardText), nameof(CardFontSize), nameof(CardWrap) })
                    OnPropertyChanged(p);
                ScheduleSave();
            }
        }
        public bool IsLinePreview { get => Preview == PreviewMode.Line; set { if (value) Preview = PreviewMode.Line; } }
        public bool IsParagraphPreview { get => Preview == PreviewMode.Paragraph; set { if (value) Preview = PreviewMode.Paragraph; } }
        public bool IsAlphabetPreview { get => Preview == PreviewMode.Alphabet; set { if (value) Preview = PreviewMode.Alphabet; } }

        public string CardText => Preview switch
        {
            PreviewMode.Paragraph => string.IsNullOrWhiteSpace(PreviewText) ? ParagraphSample : PreviewText,
            PreviewMode.Alphabet => UpperSample + "\n" + LowerSample + "\n" + "0123456789 !?&@€",
            _ => EffectivePreviewText
        };
        public double CardFontSize => Preview switch
        {
            PreviewMode.Paragraph => Math.Max(11, Math.Round(PreviewSize * 0.4)),
            PreviewMode.Alphabet => Math.Max(12, Math.Round(PreviewSize * 0.6)),
            _ => PreviewSize
        };
        public TextWrapping CardWrap => Preview == PreviewMode.Line ? TextWrapping.NoWrap : TextWrapping.Wrap;

        // Sortiranje
        public SortMode Sort
        {
            get => _lib.Sort;
            set
            {
                _lib.Sort = value;
                OnPropertyChanged(nameof(Sort));
                OnPropertyChanged(nameof(SortText));
                ApplySort();
                ScheduleSave();
            }
        }

        public string SortText => Sort switch
        {
            SortMode.NameDesc => "Ime Ž–A",
            SortMode.Styles => "Najviše stilova",
            SortMode.Recent => "Nedavno dodani",
            SortMode.FavoritesFirst => "Favoriti prvi",
            _ => "Ime A–Ž"
        };

        public void CycleSort() => Sort = (SortMode)(((int)Sort + 1) % 5);

        private void ApplySort()
        {
            if (FontsView is ListCollectionView lcv)
            {
                lcv.SortDescriptions.Clear();
                lcv.CustomSort = new FontComparer(Sort);
                VisibleCount = lcv.Count;
                RebuildGrid();
            }
        }

        // Usporedba
        public CompareMode CompareView
        {
            get => _compareView;
            set
            {
                if (Set(ref _compareView, value))
                {
                    OnPropertyChanged(nameof(IsColumnsCompare));
                    OnPropertyChanged(nameof(IsOverlayCompare));
                    OnPropertyChanged(nameof(IsPairingCompare));
                }
            }
        }
        private CompareMode _compareView = CompareMode.Columns;
        public bool IsColumnsCompare { get => CompareView == CompareMode.Columns; set { if (value) CompareView = CompareMode.Columns; } }
        public bool IsOverlayCompare { get => CompareView == CompareMode.Overlay; set { if (value) CompareView = CompareMode.Overlay; } }
        public bool IsPairingCompare { get => CompareView == CompareMode.Pairing; set { if (value) CompareView = CompareMode.Pairing; } }

        public void SwapCompare()
        {
            if (CompareFonts.Count >= 2) CompareFonts.Move(1, 0);
        }

        // Kartice detalja: 0 = detalji, 1 = glifovi, 2 = tester
        private int _detailTab;
        public int DetailTab
        {
            get => _detailTab;
            set
            {
                if (Set(ref _detailTab, value))
                {
                    OnPropertyChanged(nameof(ShowDetails));
                    OnPropertyChanged(nameof(ShowGlyphs));
                    OnPropertyChanged(nameof(ShowTester));
                    if (value == 1) LoadGlyphs();
                }
            }
        }
        public bool ShowDetails { get => DetailTab == 0; set { if (value) DetailTab = 0; } }
        public bool ShowGlyphs { get => DetailTab == 1; set { if (value) DetailTab = 1; } }
        public bool ShowTester { get => DetailTab == 2; set { if (value) DetailTab = 2; } }

        // Mreža
        private List<List<FontItem>> _gridRows = new List<List<FontItem>>();
        public List<List<FontItem>> GridRows { get => _gridRows; private set => Set(ref _gridRows, value); }
        private int _gridColumns = 4;

        public void SetGridWidth(double width)
        {
            int cols = Math.Max(1, (int)((width - 14) / 212));
            if (cols != _gridColumns)
            {
                _gridColumns = cols;
                RebuildGrid();
            }
        }

        private void RebuildGrid()
        {
            if (View != ViewMode.Grid || FontsView == null) return;
            var items = FontsView.Cast<FontItem>().ToList();
            var rows = new List<List<FontItem>>();
            for (int i = 0; i < items.Count; i += _gridColumns)
                rows.Add(items.Skip(i).Take(_gridColumns).ToList());
            GridRows = rows;
        }

        #endregion

        #region Postavke

        public bool HideSystemFonts
        {
            get => _lib.HideSystemFonts;
            set
            {
                if (_lib.HideSystemFonts == value) return;
                _lib.HideSystemFonts = value;
                OnPropertyChanged();
                Refresh();
                ScheduleSave();
            }
        }

        public bool TemporaryActivation
        {
            get => _lib.TemporaryActivation;
            set
            {
                if (_lib.TemporaryActivation == value) return;
                _lib.TemporaryActivation = value;
                OnPropertyChanged();
                ScheduleSave();
            }
        }

        public int ThemeMode
        {
            get => _lib.ThemeMode;
            set
            {
                if (_lib.ThemeMode == value) return;
                _lib.ThemeMode = value;
                OnPropertyChanged(nameof(ThemeMode));
                OnPropertyChanged(nameof(IsThemeSystem));
                OnPropertyChanged(nameof(IsThemeLight));
                OnPropertyChanged(nameof(IsThemeDark));
                ApplyTheme();
                ScheduleSave();
            }
        }
        public bool IsThemeSystem { get => ThemeMode == 0; set { if (value) ThemeMode = 0; } }
        public bool IsThemeLight { get => ThemeMode == 1; set { if (value) ThemeMode = 1; } }
        public bool IsThemeDark { get => ThemeMode == 2; set { if (value) ThemeMode = 2; } }

        public bool EffectiveLight => ThemeMode == 1 || (ThemeMode == 0 && ThemeManager.SystemPrefersLight());

        public void ApplyTheme()
        {
            ThemeManager.Apply(EffectiveLight);
            ThemeChanged?.Invoke();
        }

        #endregion

        #region Glifovi

        private List<GlyphItem> _glyphs;
        public List<GlyphItem> Glyphs { get => _glyphs; private set => Set(ref _glyphs, value); }

        private GlyphItem _selectedGlyph;
        public GlyphItem SelectedGlyph { get => _selectedGlyph; set => Set(ref _selectedGlyph, value); }

        private string _glyphInfo = "";
        public string GlyphInfo { get => _glyphInfo; private set => Set(ref _glyphInfo, value); }

        private void LoadGlyphs()
        {
            SelectedGlyph = null;
            if (SelectedFont == null) { Glyphs = null; GlyphInfo = ""; return; }
            var list = FontAnalyzer.GetGlyphs(SelectedFont, out var total);
            if (list == null)
            {
                Glyphs = null;
                GlyphInfo = "Znakove ovog fonta nije moguće pročitati.";
                return;
            }
            Glyphs = list;
            var word = Plural.Of(total, "znak", "znaka", "znakova");
            GlyphInfo = total > list.Count
                ? $"{total} {word} · prikazano prvih {list.Count}"
                : $"{total} {word}";
            SelectedGlyph = list.FirstOrDefault(g => g.CodePoint == 'A') ?? list.FirstOrDefault();
        }

        #endregion

        #region Učitavanje

        public void ApplyInitialTheme()
        {
            _lib = LibraryStore.Load();
            ThemeManager.Apply(EffectiveLight);
        }

        public async Task InitializeAsync()
        {
            _previewText = _lib.PreviewText ?? "";
            _previewSize = _lib.PreviewSize;
            foreach (var p in new[] { nameof(PreviewText), nameof(EffectivePreviewText), nameof(PreviewSize), nameof(PreviewSizeText),
                         nameof(CardText), nameof(CardFontSize), nameof(CardWrap), nameof(OverlaySize), nameof(HideSystemFonts),
                         nameof(TemporaryActivation), nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark), nameof(SortText), nameof(IsLinePreview),
                         nameof(IsParagraphPreview), nameof(IsAlphabetPreview) })
                OnPropertyChanged(p);
            if (View == ViewMode.Compare) _lib.View = ViewMode.List;
            RaiseViewProps();

            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.All, Title = "Svi fontovi", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Favorites, Title = "Favoriti", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Activated, Title = "Aktivirani", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Mono, Title = "Monospace", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Variable, Title = "Varijabilni", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Croatian, Title = "Hrvatski znakovi", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Duplicates, Title = "Duplikati", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Google, Title = "Google Fonts", Icon = "" });

            IsBusy = true;
            StatusText = "Učitavam fontove…";

            var folders = _lib.Folders.Where(Directory.Exists).ToList();
            _lib.Folders = folders;

            var loaded = await Task.Run(() =>
            {
                var list = new List<FontItem>(FontLoader.LoadSystem());
                foreach (var f in folders) list.AddRange(FontLoader.LoadFolder(f));
                return list;
            });

            AddUnique(loaded);
            _lib.ActivatedFiles = _lib.ActivatedFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ApplyLibraryState(_all);
            UpdateActivationState();

            SyncTagEntries();
            SyncFolderEntries();
            RebuildView();

            // Ponovno aktiviraj fontove koji su bili aktivni
            var toActivate = _lib.ActivatedFiles.ToList();
            if (toActivate.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var p in toActivate) FontActivator.Activate(p);
                    FontActivator.Broadcast();
                });
            }

            StatusText = "Analiziram fontove…";
            await AnalyzeAsync(_all.ToList());
            IsBusy = false;
            StatusText = $"{Plural.Fonts(_all.Count)} u biblioteci";
            ScheduleSave();
        }

        private void AddUnique(IEnumerable<FontItem> items)
        {
            var keys = new HashSet<string>(_all.Select(f => f.Key));
            foreach (var f in items)
                if (keys.Add(f.Key))
                {
                    f.AddedIndex = ++_addedCounter;
                    _all.Add(f);
                }
        }

        private void ApplyLibraryState(IEnumerable<FontItem> items)
        {
            var fav = new HashSet<string>(_lib.Favorites);
            var tagsByKey = new Dictionary<string, List<string>>();
            foreach (var kv in _lib.Tags)
                foreach (var key in kv.Value)
                {
                    if (!tagsByKey.TryGetValue(key, out var l)) tagsByKey[key] = l = new List<string>();
                    l.Add(kv.Key);
                }

            foreach (var f in items)
            {
                f.IsFavorite = fav.Contains(f.Key);
                if (tagsByKey.TryGetValue(f.Key, out var tags))
                    foreach (var t in tags.OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase))
                        if (!f.Tags.Contains(t)) f.Tags.Add(t);
            }
        }

        private void UpdateActivationState()
        {
            var active = new HashSet<string>(_lib.ActivatedFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var f in _all.Where(f => f.FilePaths != null))
            {
                int on = f.FilePaths.Count(active.Contains);
                f.IsActivated = f.FilePaths.Count > 0 && on == f.FilePaths.Count;
                f.IsPartiallyActivated = on > 0 && on < f.FilePaths.Count;
                foreach (var s in f.Styles)
                    s.IsActivated = s.FilePath != null && active.Contains(s.FilePath);
            }
        }

        private void RecomputeDuplicates()
        {
            var counts = _all.GroupBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.CurrentCultureIgnoreCase);
            foreach (var f in _all) f.IsDuplicate = counts[f.Name] > 1;
        }

        private async Task AnalyzeAsync(List<FontItem> items)
        {
            var results = await Task.Run(() => items.Select(f => (f, r: FontAnalyzer.Analyze(f))).ToList());
            foreach (var (f, r) in results)
            {
                f.IsMono = r.Mono;
                f.HasCroatian = r.Croatian;
                f.Version = r.Version;
                f.Designer = r.Designer;
                f.Manufacturer = r.Manufacturer;
                f.IsVariable = r.IsVariable;
                if (f.MainFile == null) f.MainFile = r.MainFile;
            }
            UpdateCounts();
            if (SelectedEntry != null && (SelectedEntry.Kind == SidebarKind.Mono || SelectedEntry.Kind == SidebarKind.Croatian
                                          || SelectedEntry.Kind == SidebarKind.Variable))
                Refresh();
        }

        private void RebuildView()
        {
            var prev = SelectedFont;
            RecomputeDuplicates();
            var view = new ListCollectionView(_all.ToList()) { Filter = FilterFont, CustomSort = new FontComparer(Sort) };
            FontsView = view;
            VisibleCount = view.Count;
            UpdateCounts();
            RebuildGrid();
            if (prev != null && view.Contains(prev)) SelectedFont = prev;
        }

        public void Refresh()
        {
            if (FontsView == null) return;
            var prev = SelectedFont;
            FontsView.Refresh();
            VisibleCount = ((ListCollectionView)FontsView).Count;
            RebuildGrid();
            if (prev != null && SelectedFont != prev && FontsView.Contains(prev)) SelectedFont = prev;
        }

        private bool FilterFont(object o)
        {
            var f = (FontItem)o;
            var e = SelectedEntry;

            if (HideSystemFonts && f.IsSystem && (e == null || e.Kind != SidebarKind.Tag && e.Kind != SidebarKind.Favorites))
                return false;

            var q = SearchText?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                bool match = f.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                             || f.Tags.Any(t => t.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                             || (f.Designer ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
                if (!match) return false;
            }

            if (e == null) return true;
            switch (e.Kind)
            {
                case SidebarKind.Favorites: return f.IsFavorite;
                case SidebarKind.Activated: return f.IsActivated || f.IsPartiallyActivated;
                case SidebarKind.Mono: return f.IsMono == true;
                case SidebarKind.Croatian: return f.HasCroatian == true;
                case SidebarKind.Variable: return f.IsVariable;
                case SidebarKind.Duplicates: return f.IsDuplicate;
                case SidebarKind.Tag: return f.Tags.Contains(e.Value);
                case SidebarKind.Folder: return string.Equals(f.FolderPath, e.Value, StringComparison.OrdinalIgnoreCase);
                default: return true;
            }
        }

        private void UpdateCounts()
        {
            foreach (var e in LibraryEntries)
            {
                switch (e.Kind)
                {
                    case SidebarKind.All: e.Count = _all.Count; break;
                    case SidebarKind.Favorites: e.Count = _all.Count(f => f.IsFavorite); break;
                    case SidebarKind.Activated: e.Count = _all.Count(f => f.IsActivated || f.IsPartiallyActivated); break;
                    case SidebarKind.Mono: e.Count = _all.Count(f => f.IsMono == true); break;
                    case SidebarKind.Croatian: e.Count = _all.Count(f => f.HasCroatian == true); break;
                    case SidebarKind.Variable: e.Count = _all.Count(f => f.IsVariable); break;
                    case SidebarKind.Duplicates: e.Count = _all.Count(f => f.IsDuplicate); break;
                    case SidebarKind.Google: e.Count = _google?.Count ?? 0; break;
                }
            }
            foreach (var e in TagEntries) e.Count = _all.Count(f => f.Tags.Contains(e.Value));
            foreach (var e in FolderEntries)
                e.Count = _all.Count(f => string.Equals(f.FolderPath, e.Value, StringComparison.OrdinalIgnoreCase));
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void OnClosing()
        {
            if (_lib.TemporaryActivation && _lib.ActivatedFiles.Count > 0)
            {
                foreach (var p in _lib.ActivatedFiles) FontActivator.Deactivate(p);
                FontActivator.Broadcast();
                _lib.ActivatedFiles.Clear();
            }
            _saveTimer.Stop();
            LibraryStore.Save(_lib);
        }

        #endregion

        #region Favoriti i usporedba

        public void OnFavoriteChanged(FontItem f)
        {
            _lib.Favorites.Remove(f.Key);
            if (f.IsFavorite) _lib.Favorites.Add(f.Key);
            UpdateCounts();
            if (SelectedEntry?.Kind == SidebarKind.Favorites || Sort == SortMode.FavoritesFirst) Refresh();
            ScheduleSave();
        }

        public void ToggleFavorite(FontItem f)
        {
            if (f == null) return;
            f.IsFavorite = !f.IsFavorite;
            OnFavoriteChanged(f);
        }

        public void OnCompareChanged(FontItem f)
        {
            if (f.IsComparing)
            {
                if (!CompareFonts.Contains(f)) CompareFonts.Add(f);
            }
            else CompareFonts.Remove(f);
        }

        public void RemoveFromCompare(FontItem f)
        {
            f.IsComparing = false;
            CompareFonts.Remove(f);
        }

        public void ClearCompare()
        {
            foreach (var f in CompareFonts.ToList()) f.IsComparing = false;
            CompareFonts.Clear();
        }

        #endregion

        #region Tagovi

        public IEnumerable<string> AllTagNames => _lib.Tags.Keys.OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase);

        public bool CreateTag(string name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return false;
            if (_lib.Tags.Keys.Any(k => string.Equals(k, name, StringComparison.CurrentCultureIgnoreCase))) return false;
            _lib.Tags[name] = new List<string>();
            SyncTagEntries();
            UpdateSuggestedTags();
            ScheduleSave();
            return true;
        }

        public void DeleteTag(string name)
        {
            if (!_lib.Tags.ContainsKey(name)) return;
            _lib.Tags.Remove(name);
            foreach (var f in _all) f.Tags.Remove(name);
            bool wasSelected = SelectedEntry?.Kind == SidebarKind.Tag && SelectedEntry.Value == name;
            SyncTagEntries();
            UpdateSuggestedTags();
            UpdateCounts();
            if (wasSelected) RequestSelectAll?.Invoke();
            else Refresh();
            ScheduleSave();
        }

        public void AddTagToFont(FontItem f, string name)
        {
            if (f == null) return;
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var existing = _lib.Tags.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.CurrentCultureIgnoreCase));
            if (existing == null)
            {
                _lib.Tags[name] = new List<string>();
                existing = name;
                SyncTagEntries();
            }
            if (!f.Tags.Contains(existing))
            {
                InsertSorted(f.Tags, existing);
                if (!_lib.Tags[existing].Contains(f.Key)) _lib.Tags[existing].Add(f.Key);
            }
            UpdateSuggestedTags();
            UpdateCounts();
            ScheduleSave();
        }

        public void RemoveTagFromFont(FontItem f, string name)
        {
            if (f == null || name == null) return;
            f.Tags.Remove(name);
            if (_lib.Tags.TryGetValue(name, out var keys)) keys.Remove(f.Key);
            UpdateSuggestedTags();
            UpdateCounts();
            if (SelectedEntry?.Kind == SidebarKind.Tag && SelectedEntry.Value == name) Refresh();
            ScheduleSave();
        }

        private static void InsertSorted(ObservableCollection<string> list, string item)
        {
            int i = 0;
            while (i < list.Count && string.Compare(list[i], item, StringComparison.CurrentCultureIgnoreCase) < 0) i++;
            list.Insert(i, item);
        }

        private void UpdateSuggestedTags()
        {
            SuggestedTags.Clear();
            if (SelectedFont == null) return;
            foreach (var t in AllTagNames)
                if (!SelectedFont.Tags.Contains(t)) SuggestedTags.Add(t);
        }

        private void SyncTagEntries()
        {
            var names = AllTagNames.ToList();
            foreach (var e in TagEntries.Where(e => !names.Contains(e.Value)).ToList()) TagEntries.Remove(e);
            for (int i = 0; i < names.Count; i++)
            {
                if (i < TagEntries.Count && TagEntries[i].Value == names[i]) continue;
                var existing = TagEntries.FirstOrDefault(e => e.Value == names[i]);
                if (existing != null) TagEntries.Move(TagEntries.IndexOf(existing), i);
                else TagEntries.Insert(i, new SidebarEntry { Kind = SidebarKind.Tag, Title = names[i], Value = names[i], Icon = "" });
            }
            UpdateCounts();
        }

        public List<FontItem> FontsWithTag(string tag) => _all.Where(f => f.Tags.Contains(tag)).ToList();

        #endregion

        #region Mape, aktivacija, izvoz

        private void SyncFolderEntries()
        {
            foreach (var e in FolderEntries.Where(e => !_lib.Folders.Contains(e.Value)).ToList()) FolderEntries.Remove(e);
            foreach (var path in _lib.Folders)
            {
                if (FolderEntries.Any(e => e.Value == path)) continue;
                var title = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(title)) title = path;
                FolderEntries.Add(new SidebarEntry { Kind = SidebarKind.Folder, Title = title, Value = path, Icon = "" });
            }
            UpdateCounts();
        }

        private static string NormalizeFolder(string path)
        {
            path = Path.GetFullPath(path).TrimEnd('\\');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            return path;
        }

        public async Task AddFolderAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            path = NormalizeFolder(path);
            if (_lib.Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = "Ta mapa je već u biblioteci.";
                return;
            }

            IsBusy = true;
            StatusText = "Učitavam fontove iz mape…";
            var items = await Task.Run(() => FontLoader.LoadFolder(path));
            _lib.Folders.Add(path);
            int before = _all.Count;
            AddUnique(items);
            ApplyLibraryState(items);
            UpdateActivationState();
            SyncFolderEntries();
            RebuildView();
            await AnalyzeAsync(items);
            IsBusy = false;
            StatusText = items.Count == 0
                ? "U mapi nisam našao nijedan font (.ttf, .otf, .ttc)."
                : $"Dodano: {Plural.Fonts(_all.Count - before)} iz mape {Path.GetFileName(path)}";
            ScheduleSave();
        }

        /// <summary>Ponovno učita fontove iz mape koja je već u biblioteci (npr. nakon preuzimanja s Google Fonts).</summary>
        public async Task ReloadFolderAsync(string path)
        {
            path = NormalizeFolder(path);
            if (!_lib.Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
            {
                await AddFolderAsync(path);
                return;
            }
            var items = await Task.Run(() => FontLoader.LoadFolder(path));
            var existing = new HashSet<string>(_all.Select(f => f.Key));
            var fresh = items.Where(i => !existing.Contains(i.Key)).ToList();
            if (fresh.Count == 0) return;
            AddUnique(fresh);
            ApplyLibraryState(fresh);
            UpdateActivationState();
            RebuildView();
            UpdateCounts();
            await AnalyzeAsync(fresh);
            ScheduleSave();
        }

        public async Task RemoveFolderAsync(string path)
        {
            var items = _all.Where(f => string.Equals(f.FolderPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
            var files = items.Where(f => f.FilePaths != null).SelectMany(f => f.FilePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var activeFiles = files.Where(p => _lib.ActivatedFiles.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            if (activeFiles.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var p in activeFiles) FontActivator.Deactivate(p);
                    FontActivator.Broadcast();
                });
                _lib.ActivatedFiles.RemoveAll(p => activeFiles.Contains(p, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var f in items)
            {
                _all.Remove(f);
                if (f.IsComparing) RemoveFromCompare(f);
            }
            if (SelectedFont != null && items.Contains(SelectedFont)) SelectedFont = null;

            bool wasSelected = SelectedEntry?.Kind == SidebarKind.Folder && SelectedEntry.Value == path;
            _lib.Folders.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            SyncFolderEntries();
            RebuildView();
            if (wasSelected) RequestSelectAll?.Invoke();
            StatusText = "Mapa uklonjena iz biblioteke (datoteke nisu obrisane).";
            ScheduleSave();
        }

        public async Task SetFilesActivatedAsync(IList<string> files, bool activate, string label)
        {
            files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return;

            IsBusy = true;
            StatusText = activate ? "Aktiviram…" : "Deaktiviram…";
            var ok = await Task.Run(() =>
            {
                var done = new List<string>();
                foreach (var p in files)
                {
                    if (activate ? FontActivator.Activate(p) : FontActivator.Deactivate(p)) done.Add(p);
                    else if (!activate) done.Add(p);
                }
                FontActivator.Broadcast();
                return done;
            });

            if (activate)
            {
                foreach (var p in ok)
                    if (!_lib.ActivatedFiles.Contains(p, StringComparer.OrdinalIgnoreCase)) _lib.ActivatedFiles.Add(p);
            }
            else
            {
                _lib.ActivatedFiles.RemoveAll(p => ok.Contains(p, StringComparer.OrdinalIgnoreCase));
            }

            UpdateActivationState();
            UpdateCounts();
            if (SelectedEntry?.Kind == SidebarKind.Activated) Refresh();
            IsBusy = false;
            if (activate && ok.Count < files.Count)
                StatusText = $"Neke datoteke nije bilo moguće aktivirati ({files.Count - ok.Count}).";
            else
                StatusText = (activate ? "Aktivirano: " : "Deaktivirano: ") + label +
                             (activate ? (TemporaryActivation ? " — do zatvaranja aplikacije." : " — dostupno u drugim aplikacijama.") : ".");
            ScheduleSave();
        }

        public Task SetActivatedAsync(IList<FontItem> fonts, bool activate)
        {
            var files = fonts.Where(f => f.FilePaths != null).SelectMany(f => f.FilePaths).ToList();
            return SetFilesActivatedAsync(files, activate, fonts.Count == 1 ? $"„{fonts[0].Name}”" : Plural.Fonts(fonts.Count));
        }

        public Task ToggleActivationAsync(FontItem f)
            => f == null || f.IsSystem ? Task.CompletedTask : SetActivatedAsync(new[] { f }, !f.IsActivated);

        public Task ToggleStyleActivationAsync(FontItem f, FontStyleItem s)
            => s?.FilePath == null ? Task.CompletedTask
                : SetFilesActivatedAsync(new[] { s.FilePath }, !s.IsActivated, $"„{f?.Name} {s.Name}”");

        public Task ActivateFolderAsync(string path, bool activate)
        {
            var fonts = _all.Where(f => string.Equals(f.FolderPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
            return SetActivatedAsync(fonts, activate);
        }

        public bool IsFolderFullyActive(string path)
        {
            var fonts = _all.Where(f => string.Equals(f.FolderPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
            return fonts.Count > 0 && fonts.All(f => f.IsActivated);
        }

        public async Task InstallAsync(FontItem f)
        {
            if (f == null || f.IsSystem) return;
            IsBusy = true;
            try
            {
                var n = await Task.Run(() => FontActivator.InstallForUser(f.FilePaths));
                StatusText = $"„{f.Name}” je trajno instaliran ({n} {Plural.Of(n, "datoteka", "datoteke", "datoteka")}).";
            }
            catch (Exception ex)
            {
                StatusText = "Instalacija nije uspjela: " + ex.Message;
            }
            IsBusy = false;
        }

        public async Task ExportAsync(IList<FontItem> fonts, string targetDir)
        {
            IsBusy = true;
            StatusText = "Izvozim…";
            try
            {
                var n = await Task.Run(() => FontExporter.CopyFiles(fonts.SelectMany(FontAnalyzer.AllFiles).ToList(), targetDir));
                StatusText = $"Izvezeno {n} {Plural.Of(n, "datoteka", "datoteke", "datoteka")} u {Path.GetFileName(targetDir.TrimEnd('\\'))}.";
            }
            catch (Exception ex)
            {
                StatusText = "Izvoz nije uspio: " + ex.Message;
            }
            IsBusy = false;
        }

        #endregion

        #region Google Fonts

        private List<GoogleFontEntry> _google;
        private ListCollectionView _googleView;
        public ListCollectionView GoogleView { get => _googleView; private set => Set(ref _googleView, value); }

        private int _googleVisibleCount;
        public int GoogleVisibleCount
        {
            get => _googleVisibleCount;
            private set { if (Set(ref _googleVisibleCount, value)) OnPropertyChanged(nameof(CountText)); }
        }

        private string _googleCategory = "";
        public string GoogleCategory
        {
            get => _googleCategory;
            set { if (Set(ref _googleCategory, value ?? "")) RefreshGoogle(); }
        }

        private string _googleStatus = "";
        public string GoogleStatus { get => _googleStatus; private set => Set(ref _googleStatus, value); }

        private bool _googleLoading;
        public bool GoogleLoading { get => _googleLoading; private set => Set(ref _googleLoading, value); }

        public async Task EnsureGoogleAsync(bool force = false)
        {
            if ((_google != null && !force) || GoogleLoading) return;
            GoogleLoading = true;
            GoogleStatus = "Učitavam katalog Google Fonts…";
            try
            {
                var list = await GoogleFontsService.LoadCatalogAsync(force);
                foreach (var e in list) e.IsDownloaded = GoogleFontsService.IsDownloaded(e.Family);
                _google = list;
                GoogleView = new ListCollectionView(_google) { Filter = FilterGoogle };
                GoogleVisibleCount = GoogleView.Count;
                GoogleStatus = "";
                UpdateCounts();
            }
            catch (Exception ex)
            {
                GoogleStatus = "Katalog nije moguće učitati (provjeri internetsku vezu). " + ex.Message;
            }
            GoogleLoading = false;
        }

        private bool FilterGoogle(object o)
        {
            var e = (GoogleFontEntry)o;
            if (!string.IsNullOrEmpty(GoogleCategory) && e.Category != GoogleCategory) return false;
            var q = SearchText?.Trim();
            return string.IsNullOrEmpty(q) || e.Family.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void RefreshGoogle()
        {
            if (GoogleView == null) return;
            GoogleView.Refresh();
            GoogleVisibleCount = GoogleView.Count;
        }

        public async Task DownloadGoogleAsync(GoogleFontEntry e)
        {
            if (e == null || e.IsDownloading) return;
            e.IsDownloading = true;
            try
            {
                var n = await GoogleFontsService.DownloadAsync(e);
                e.IsDownloaded = true;
                await ReloadFolderAsync(GoogleFontsService.Dir);
                StatusText = $"Preuzet „{e.Family}” ({n} {Plural.Of(n, "stil", "stila", "stilova")}) — nalazi se u mapi Google Fonts.";
            }
            catch (Exception ex)
            {
                StatusText = $"Preuzimanje „{e.Family}” nije uspjelo: {ex.Message}";
            }
            e.IsDownloading = false;
        }

        public FontItem FindDownloadedGoogleFont(GoogleFontEntry e)
        {
            var dir = GoogleFontsService.FamilyDir(e.Family);
            return _all.FirstOrDefault(f => f.FirstFile != null && f.FirstFile.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                   ?? _all.FirstOrDefault(f => string.Equals(f.Name, e.Family, StringComparison.CurrentCultureIgnoreCase));
        }

        #endregion
    }
}
