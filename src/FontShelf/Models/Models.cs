using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace FontShelf.Models
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public static class Plural
    {
        public static string Of(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            if (m10 == 1 && m100 != 11) return one;
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
            return many;
        }

        public static string Fonts(int n) => $"{n} {Of(n, "font", "fonta", "fontova")}";
    }

    public class FontStyleItem : ObservableObject
    {
        public string Name { get; set; }
        public FontFamily Family { get; set; }
        public FontWeight Weight { get; set; }
        public FontStyle Style { get; set; }
        public FontStretch Stretch { get; set; }

        /// <summary>Datoteka ovog stila (samo za fontove iz mapa).</summary>
        public string FilePath { get; set; }
        public bool CanToggle => FilePath != null;

        public int WeightValue => Weight.ToOpenTypeWeight();
        public bool IsItalic => Style != FontStyles.Normal;

        private bool _isActivated;
        public bool IsActivated { get => _isActivated; set => Set(ref _isActivated, value); }
    }

    public class FontItem : ObservableObject
    {
        public FontItem()
        {
            Tags.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasTags));
                OnPropertyChanged(nameof(TagsText));
            };
        }

        public string Key { get; set; }
        public string Name { get; set; }
        public FontFamily Family { get; set; }
        public List<FontStyleItem> Styles { get; set; } = new List<FontStyleItem>();
        public int AddedIndex { get; set; }

        /// <summary>Datoteke fonta (samo za fontove iz mapa). Null za instalirane fontove.</summary>
        public List<string> FilePaths { get; set; }

        /// <summary>Mapa iz biblioteke kojoj font pripada (null za instalirane).</summary>
        public string FolderPath { get; set; }

        public bool IsSystem => FilePaths == null;
        public bool IsFromFolder => !IsSystem;
        public string SourceLabel => IsSystem ? "Instaliran u sustavu" : FolderPath;
        public string SourceShort => IsSystem ? "Sustav" : System.IO.Path.GetFileName(FolderPath?.TrimEnd('\\'));
        public string FirstFile => FilePaths?.FirstOrDefault();

        public int StyleCount => Styles.Count;
        public string StyleCountText => $"{StyleCount} {Plural.Of(StyleCount, "stil", "stila", "stilova")}";

        public ObservableCollection<string> Tags { get; } = new ObservableCollection<string>();
        public bool HasTags => Tags.Count > 0;
        public string TagsText => string.Join(", ", Tags);

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        private bool _isFavorite;
        public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }

        private bool _isComparing;
        public bool IsComparing { get => _isComparing; set => Set(ref _isComparing, value); }

        private bool _isActivated;
        public bool IsActivated
        {
            get => _isActivated;
            set { if (Set(ref _isActivated, value)) { OnPropertyChanged(nameof(ActivateText)); OnPropertyChanged(nameof(ActivationLabel)); OnPropertyChanged(nameof(HasActivationLabel)); } }
        }

        private bool _isPartiallyActivated;
        public bool IsPartiallyActivated
        {
            get => _isPartiallyActivated;
            set { if (Set(ref _isPartiallyActivated, value)) { OnPropertyChanged(nameof(ActivationLabel)); OnPropertyChanged(nameof(HasActivationLabel)); } }
        }

        public string ActivateText => IsActivated ? "Deaktiviraj" : "Aktiviraj";
        public string ActivationLabel => IsActivated ? "aktiviran" : IsPartiallyActivated ? "djelomično" : "";
        public bool HasActivationLabel => IsActivated || IsPartiallyActivated;

        private bool? _isMono;
        public bool? IsMono
        {
            get => _isMono;
            set { if (Set(ref _isMono, value)) { OnPropertyChanged(nameof(IsMonoBadge)); OnPropertyChanged(nameof(MonoText)); } }
        }
        public bool IsMonoBadge => IsMono == true;
        public string MonoText => IsMono == true ? "da" : "";

        private bool? _hasCroatian;
        public bool? HasCroatian
        {
            get => _hasCroatian;
            set { if (Set(ref _hasCroatian, value)) { OnPropertyChanged(nameof(MissingCroatian)); OnPropertyChanged(nameof(CroatianText)); } }
        }
        public bool MissingCroatian => HasCroatian == false;
        public string CroatianText => HasCroatian == true ? "da" : HasCroatian == false ? "ne" : "";

        private bool _isVariable;
        public bool IsVariable { get => _isVariable; set => Set(ref _isVariable, value); }

        private bool _isDuplicate;
        public bool IsDuplicate { get => _isDuplicate; set => Set(ref _isDuplicate, value); }

        private string _version = "";
        public string Version { get => _version; set => Set(ref _version, value); }

        private string _designer = "";
        public string Designer { get => _designer; set => Set(ref _designer, value); }

        private string _manufacturer = "";
        public string Manufacturer { get => _manufacturer; set => Set(ref _manufacturer, value); }

        /// <summary>Glavna datoteka (za instalirane fontove, otkrivena analizom).</summary>
        public string MainFile { get; set; }
    }

    public enum SidebarKind { All, Favorites, Activated, Mono, Croatian, Variable, Duplicates, Google, Tag, Folder }

    public class SidebarEntry : ObservableObject
    {
        public SidebarKind Kind { get; set; }
        public string Title { get; set; }
        public string Icon { get; set; }
        public string Value { get; set; }

        private int _count;
        public int Count { get => _count; set { if (Set(ref _count, value)) OnPropertyChanged(nameof(CountText)); } }
        public string CountText => Kind == SidebarKind.Google && Count == 0 ? "" : Count.ToString();
    }

    public class GlyphItem
    {
        public int CodePoint { get; set; }
        public string Text { get; set; }
        public string Code => $"U+{CodePoint:X4}";
        public string Decimal => $"&#{CodePoint};";
    }

    public class GoogleFontEntry : ObservableObject
    {
        public string Family { get; set; }
        public string Category { get; set; }
        public List<string> Variants { get; set; } = new List<string>();
        public int Popularity { get; set; }
        public bool IsVariableFont { get; set; }
        public string Info => $"{CategoryHr} · {Variants.Count} {Plural.Of(Variants.Count, "stil", "stila", "stilova")}" + (IsVariableFont ? " · varijabilni" : "");

        public string CategoryHr => Category switch
        {
            "Sans Serif" => "Sans serif",
            "Serif" => "Serif",
            "Display" => "Display",
            "Handwriting" => "Rukopisni",
            "Monospace" => "Monospace",
            _ => Category ?? ""
        };

        private bool _isDownloaded;
        public bool IsDownloaded { get => _isDownloaded; set { if (Set(ref _isDownloaded, value)) OnPropertyChanged(nameof(ButtonText)); } }

        private bool _isDownloading;
        public bool IsDownloading { get => _isDownloading; set { if (Set(ref _isDownloading, value)) OnPropertyChanged(nameof(ButtonText)); } }

        public string ButtonText => IsDownloading ? "Preuzimam…" : IsDownloaded ? "Preuzeto ✓" : "Preuzmi";
    }

    public enum SortMode { NameAsc, NameDesc, Styles, Recent, FavoritesFirst }
    public enum ViewMode { List, Grid, Table, Compare }
    public enum PreviewMode { Line, Paragraph, Alphabet }
    public enum CompareMode { Columns, Overlay, Pairing }

    public class LibraryData
    {
        public List<string> Favorites { get; set; } = new List<string>();
        public Dictionary<string, List<string>> Tags { get; set; } = new Dictionary<string, List<string>>();
        public List<string> Folders { get; set; } = new List<string>();
        public List<string> ActivatedFiles { get; set; } = new List<string>();
        public string PreviewText { get; set; } = "";
        public double PreviewSize { get; set; } = 40;
        public bool HideSystemFonts { get; set; }
        public bool TemporaryActivation { get; set; }
        public bool LightTheme { get; set; }
        /// <summary>0 = prati Windows, 1 = svijetla, 2 = tamna</summary>
        public int ThemeMode { get; set; }
        public SortMode Sort { get; set; } = SortMode.NameAsc;
        public ViewMode View { get; set; } = ViewMode.List;
        public PreviewMode Preview { get; set; } = PreviewMode.Line;
    }
}
