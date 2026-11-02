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

    public class FontStyleItem
    {
        public string Name { get; set; }
        public FontFamily Family { get; set; }
        public FontWeight Weight { get; set; }
        public FontStyle Style { get; set; }
        public FontStretch Stretch { get; set; }
    }

    public class FontItem : ObservableObject
    {
        public FontItem()
        {
            Tags.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasTags));
        }

        public string Key { get; set; }
        public string Name { get; set; }
        public FontFamily Family { get; set; }
        public List<FontStyleItem> Styles { get; set; } = new List<FontStyleItem>();

        /// <summary>Datoteke fonta (samo za fontove iz mapa). Null za instalirane fontove.</summary>
        public List<string> FilePaths { get; set; }

        /// <summary>Mapa iz biblioteke kojoj font pripada (null za instalirane).</summary>
        public string FolderPath { get; set; }

        public bool IsSystem => FilePaths == null;
        public bool IsFromFolder => !IsSystem;
        public string SourceLabel => IsSystem ? "Instaliran u sustavu" : FolderPath;
        public string FirstFile => FilePaths?.FirstOrDefault();

        public int StyleCount => Styles.Count;
        public string StyleCountText => $"{StyleCount} {Plural.Of(StyleCount, "stil", "stila", "stilova")}";

        public ObservableCollection<string> Tags { get; } = new ObservableCollection<string>();
        public bool HasTags => Tags.Count > 0;

        private bool _isFavorite;
        public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }

        private bool _isComparing;
        public bool IsComparing { get => _isComparing; set => Set(ref _isComparing, value); }

        private bool _isActivated;
        public bool IsActivated
        {
            get => _isActivated;
            set { if (Set(ref _isActivated, value)) OnPropertyChanged(nameof(ActivateText)); }
        }

        public string ActivateText => IsActivated ? "Deaktiviraj" : "Aktiviraj";

        private bool? _isMono;
        public bool? IsMono
        {
            get => _isMono;
            set { if (Set(ref _isMono, value)) OnPropertyChanged(nameof(IsMonoBadge)); }
        }
        public bool IsMonoBadge => IsMono == true;

        private bool? _hasCroatian;
        public bool? HasCroatian
        {
            get => _hasCroatian;
            set { if (Set(ref _hasCroatian, value)) OnPropertyChanged(nameof(MissingCroatian)); }
        }
        public bool MissingCroatian => HasCroatian == false;
    }

    public enum SidebarKind { All, Favorites, Activated, Mono, Croatian, Tag, Folder }

    public class SidebarEntry : ObservableObject
    {
        public SidebarKind Kind { get; set; }
        public string Title { get; set; }
        public string Icon { get; set; }
        public string Value { get; set; }

        private int _count;
        public int Count { get => _count; set => Set(ref _count, value); }
    }

    public class GlyphItem
    {
        public int CodePoint { get; set; }
        public string Text { get; set; }
        public string Code => $"U+{CodePoint:X4}";
        public string Decimal => $"&#{CodePoint};";
    }

    public class LibraryData
    {
        public List<string> Favorites { get; set; } = new List<string>();
        public Dictionary<string, List<string>> Tags { get; set; } = new Dictionary<string, List<string>>();
        public List<string> Folders { get; set; } = new List<string>();
        public List<string> ActivatedFiles { get; set; } = new List<string>();
        public string PreviewText { get; set; } = "";
        public double PreviewSize { get; set; } = 40;
    }
}
