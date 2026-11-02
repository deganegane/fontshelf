using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using FontShelf.Models;
using FontShelf.Services;

namespace FontShelf.ViewModels
{
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

        public ObservableCollection<SidebarEntry> LibraryEntries { get; } = new ObservableCollection<SidebarEntry>();
        public ObservableCollection<SidebarEntry> TagEntries { get; } = new ObservableCollection<SidebarEntry>();
        public ObservableCollection<SidebarEntry> FolderEntries { get; } = new ObservableCollection<SidebarEntry>();
        public ObservableCollection<FontItem> CompareFonts { get; } = new ObservableCollection<FontItem>();
        public ObservableCollection<string> SuggestedTags { get; } = new ObservableCollection<string>();

        public string Upper => UpperSample;
        public string Lower => LowerSample;
        public string Digits => DigitSample;
        public string Paragraph => ParagraphSample;

        public MainViewModel()
        {
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); Refresh(); };
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); LibraryStore.Save(_lib); };

            CompareFonts.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasCompare));
                OnPropertyChanged(nameof(CompareTabText));
            };
            FolderEntries.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasFolders));
        }

        #region Svojstva

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
                    _lib.PreviewSize = v;
                    ScheduleSave();
                }
            }
        }
        public string PreviewSizeText => $"{PreviewSize:0} pt";

        private FontItem _selectedFont;
        public FontItem SelectedFont
        {
            get => _selectedFont;
            set
            {
                if (Set(ref _selectedFont, value))
                {
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
                    IsCompareMode = false;
                    Refresh();
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
        public string CountText => Plural.Fonts(VisibleCount);

        private bool _isCompareMode;
        public bool IsCompareMode
        {
            get => _isCompareMode;
            set { if (Set(ref _isCompareMode, value)) OnPropertyChanged(nameof(IsListMode)); }
        }
        public bool IsListMode
        {
            get => !_isCompareMode;
            set => IsCompareMode = !value;
        }

        public bool HasCompare => CompareFonts.Count > 0;
        public string CompareTabText => CompareFonts.Count > 0 ? $"Usporedba ({CompareFonts.Count})" : "Usporedba";
        public bool HasFolders => FolderEntries.Count > 0;

        private bool _showGlyphs;
        public bool ShowGlyphs
        {
            get => _showGlyphs;
            set
            {
                if (Set(ref _showGlyphs, value))
                {
                    OnPropertyChanged(nameof(ShowDetails));
                    if (value) LoadGlyphs();
                }
            }
        }
        public bool ShowDetails
        {
            get => !_showGlyphs;
            set => ShowGlyphs = !value;
        }

        private List<GlyphItem> _glyphs;
        public List<GlyphItem> Glyphs { get => _glyphs; private set => Set(ref _glyphs, value); }

        private GlyphItem _selectedGlyph;
        public GlyphItem SelectedGlyph { get => _selectedGlyph; set => Set(ref _selectedGlyph, value); }

        private string _glyphInfo = "";
        public string GlyphInfo { get => _glyphInfo; private set => Set(ref _glyphInfo, value); }

        private string _statusText = "";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

        #endregion

        #region Učitavanje

        public async Task InitializeAsync()
        {
            _lib = LibraryStore.Load();
            _previewText = _lib.PreviewText ?? "";
            _previewSize = _lib.PreviewSize;
            OnPropertyChanged(nameof(PreviewText));
            OnPropertyChanged(nameof(EffectivePreviewText));
            OnPropertyChanged(nameof(PreviewSize));
            OnPropertyChanged(nameof(PreviewSizeText));

            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.All, Title = "Svi fontovi", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Favorites, Title = "Favoriti", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Activated, Title = "Aktivirani", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Mono, Title = "Monospace", Icon = "" });
            LibraryEntries.Add(new SidebarEntry { Kind = SidebarKind.Croatian, Title = "Hrvatski znakovi", Icon = "" });

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

            SyncTagEntries();
            SyncFolderEntries();
            RebuildView();

            // Ponovno aktiviraj fontove koji su bili aktivni (aktivacija vrijedi do odjave)
            var toActivate = _lib.ActivatedFiles.ToList();
            if (toActivate.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var p in toActivate) FontActivator.Activate(p);
                    FontActivator.Broadcast();
                });
            }

            StatusText = "Analiziram znakove…";
            await AnalyzeAsync(_all.ToList());
            IsBusy = false;
            StatusText = $"{Plural.Fonts(_all.Count)} u biblioteci";
            ScheduleSave();
        }

        private void AddUnique(IEnumerable<FontItem> items)
        {
            var keys = new HashSet<string>(_all.Select(f => f.Key));
            foreach (var f in items)
                if (keys.Add(f.Key)) _all.Add(f);
        }

        private void ApplyLibraryState(IEnumerable<FontItem> items)
        {
            var fav = new HashSet<string>(_lib.Favorites);
            var active = new HashSet<string>(_lib.ActivatedFiles, StringComparer.OrdinalIgnoreCase);
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
                f.IsActivated = f.FilePaths != null && f.FilePaths.Count > 0 && f.FilePaths.All(active.Contains);
                if (tagsByKey.TryGetValue(f.Key, out var tags))
                    foreach (var t in tags.OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase))
                        if (!f.Tags.Contains(t)) f.Tags.Add(t);
            }
        }

        private async Task AnalyzeAsync(List<FontItem> items)
        {
            var results = await Task.Run(() => items.Select(f => (f, r: FontAnalyzer.Analyze(f))).ToList());
            foreach (var (f, r) in results)
            {
                f.IsMono = r.mono;
                f.HasCroatian = r.croatian;
            }
            UpdateCounts();
            if (SelectedEntry != null && (SelectedEntry.Kind == SidebarKind.Mono || SelectedEntry.Kind == SidebarKind.Croatian))
                Refresh();
        }

        private void RebuildView()
        {
            var prev = SelectedFont;
            var sorted = _all.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var view = new ListCollectionView(sorted) { Filter = FilterFont };
            FontsView = view;
            VisibleCount = view.Count;
            UpdateCounts();
            if (prev != null && view.Contains(prev)) SelectedFont = prev;
        }

        public void Refresh()
        {
            if (FontsView == null) return;
            var prev = SelectedFont;
            FontsView.Refresh();
            VisibleCount = ((ListCollectionView)FontsView).Count;
            if (prev != null && SelectedFont != prev && FontsView.Contains(prev)) SelectedFont = prev;
        }

        private bool FilterFont(object o)
        {
            var f = (FontItem)o;
            var q = SearchText?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                bool match = f.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                             || f.Tags.Any(t => t.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);
                if (!match) return false;
            }

            var e = SelectedEntry;
            if (e == null) return true;
            switch (e.Kind)
            {
                case SidebarKind.Favorites: return f.IsFavorite;
                case SidebarKind.Activated: return f.IsActivated;
                case SidebarKind.Mono: return f.IsMono == true;
                case SidebarKind.Croatian: return f.HasCroatian == true;
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
                    case SidebarKind.Activated: e.Count = _all.Count(f => f.IsActivated); break;
                    case SidebarKind.Mono: e.Count = _all.Count(f => f.IsMono == true); break;
                    case SidebarKind.Croatian: e.Count = _all.Count(f => f.HasCroatian == true); break;
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

        public void SaveNow()
        {
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
            if (SelectedEntry?.Kind == SidebarKind.Favorites) Refresh();
            ScheduleSave();
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

        public event Action RequestSelectAll;

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

        #endregion

        #region Mape i aktivacija

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

        public async Task AddFolderAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            path = Path.GetFullPath(path).TrimEnd('\\');
            if (path.Length == 2 && path[1] == ':') path += "\\";
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
            SyncFolderEntries();
            RebuildView();
            await AnalyzeAsync(items);
            IsBusy = false;
            StatusText = items.Count == 0
                ? "U mapi nisam našao nijedan font (.ttf, .otf, .ttc)."
                : $"Dodano: {Plural.Fonts(_all.Count - before)} iz mape {Path.GetFileName(path)}";
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

        public async Task SetActivatedAsync(IList<FontItem> fonts, bool activate)
        {
            var files = fonts.Where(f => f.FilePaths != null).SelectMany(f => f.FilePaths)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return;

            IsBusy = true;
            StatusText = activate ? "Aktiviram…" : "Deaktiviram…";
            var ok = await Task.Run(() =>
            {
                var done = new List<string>();
                foreach (var p in files)
                {
                    if (activate ? FontActivator.Activate(p) : FontActivator.Deactivate(p)) done.Add(p);
                    else if (!activate) done.Add(p); // ionako ga više ne smatramo aktivnim
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

            var active = new HashSet<string>(_lib.ActivatedFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var f in _all.Where(f => f.FilePaths != null))
                f.IsActivated = f.FilePaths.Count > 0 && f.FilePaths.All(active.Contains);

            UpdateCounts();
            if (SelectedEntry?.Kind == SidebarKind.Activated) Refresh();
            IsBusy = false;
            if (activate && ok.Count < files.Count)
                StatusText = $"Neke datoteke nije bilo moguće aktivirati ({files.Count - ok.Count}).";
            else
                StatusText = activate
                    ? $"Aktivirano: {Plural.Fonts(fonts.Count)} — dostupni su u drugim aplikacijama."
                    : $"Deaktivirano: {Plural.Fonts(fonts.Count)}.";
            ScheduleSave();
        }

        public Task ToggleActivationAsync(FontItem f)
            => f == null || f.IsSystem ? Task.CompletedTask : SetActivatedAsync(new[] { f }, !f.IsActivated);

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
                var n = await Task.Run(() => FontActivator.InstallForUser(f));
                StatusText = $"„{f.Name}” je trajno instaliran ({n} {Plural.Of(n, "datoteka", "datoteke", "datoteka")}).";
            }
            catch (Exception ex)
            {
                StatusText = "Instalacija nije uspjela: " + ex.Message;
            }
            IsBusy = false;
        }

        #endregion

        #region Glifovi

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
    }
}
