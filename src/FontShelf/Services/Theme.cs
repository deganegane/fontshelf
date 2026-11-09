using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FontShelf.Services
{
    public static class ThemeManager
    {
        public static bool IsLight { get; private set; }
        public static bool UseMica { get; set; }

        /// <summary>Je li Windows postavljen na svijetli način za aplikacije.</summary>
        public static bool SystemPrefersLight()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
            }
            catch { }
            return false;
        }

        private static void S(ResourceDictionary r, string key, string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            r[key] = b;
        }

        public static void Apply(bool light)
        {
            IsLight = light;
            var r = Application.Current.Resources;
            var p = light ? Light : Dark;
            foreach (var kv in p) S(r, kv.Key, kv.Value);

            // S Mica pozadinom prozor je proziran, a slojevi lagano prozirni
            if (UseMica)
            {
                S(r, "WindowBgBrush", "#00000000");
                S(r, "LayerBrush", light ? "#E6FBFBFD" : "#D9222227");
            }
        }

        private static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
        {
            ["WindowBgBrush"] = "#1A1A1E",
            ["LayerBrush"] = "#222227",
            ["BgBrush"] = "#222227",
            ["PanelBrush"] = "#222227",
            ["CardBrush"] = "#2A2A30",
            ["CardHoverBrush"] = "#2E2E35",
            ["RowHoverBrush"] = "#0DFFFFFF",
            ["LineBrush"] = "#313137",
            ["InputBrush"] = "#2A2A30",
            ["TextBrush"] = "#EDEDF0",
            ["SubTextBrush"] = "#8D9099",
            ["BodyTextBrush"] = "#C4C5CC",
            ["AccentBrush"] = "#7C8BFF",
            ["AccentSoftBrush"] = "#2A7C8BFF",
            ["OnAccentBrush"] = "#101114",
            ["StarBrush"] = "#FFC857",
            ["WarnBrush"] = "#FF9F6B",
            ["GoodBrush"] = "#5FD38F",
            ["ControlBrush"] = "#303037",
            ["SegmentOnBrush"] = "#3D3D46",
            ["SelectedNavBrush"] = "#14FFFFFF",
            ["HoverBrush"] = "#FFFFFF",
            ["ThumbBrush"] = "#40FFFFFF",
            ["PopupBrush"] = "#2B2B31",
            ["ToastBrush"] = "#F2303037",
            ["OverlayABrush"] = "#FF6B8B",
            ["OverlayBBrush"] = "#4FD1C5",
            ["CloseHoverBrush"] = "#C42B1C",
        };

        private static readonly Dictionary<string, string> Light = new Dictionary<string, string>
        {
            ["WindowBgBrush"] = "#ECEDF1",
            ["LayerBrush"] = "#FAFAFC",
            ["BgBrush"] = "#FAFAFC",
            ["PanelBrush"] = "#FAFAFC",
            ["CardBrush"] = "#FFFFFF",
            ["CardHoverBrush"] = "#F4F5F8",
            ["RowHoverBrush"] = "#08000000",
            ["LineBrush"] = "#E4E5EB",
            ["InputBrush"] = "#F0F1F5",
            ["TextBrush"] = "#17181C",
            ["SubTextBrush"] = "#6A6E79",
            ["BodyTextBrush"] = "#3B3D45",
            ["AccentBrush"] = "#3B63F3",
            ["AccentSoftBrush"] = "#1C3B63F3",
            ["OnAccentBrush"] = "#FFFFFF",
            ["StarBrush"] = "#E5A000",
            ["WarnBrush"] = "#D2591F",
            ["GoodBrush"] = "#14904D",
            ["ControlBrush"] = "#E8E9EE",
            ["SegmentOnBrush"] = "#FFFFFF",
            ["SelectedNavBrush"] = "#0F000000",
            ["HoverBrush"] = "#000000",
            ["ThumbBrush"] = "#38000000",
            ["PopupBrush"] = "#FFFFFF",
            ["ToastBrush"] = "#F2202024",
            ["OverlayABrush"] = "#E0245E",
            ["OverlayBBrush"] = "#0F9D8F",
            ["CloseHoverBrush"] = "#C42B1C",
        };

        // Boje za tagove (točkice) – birane po imenu taga
        private static readonly string[] TagColors =
            { "#FF6B6B", "#FFA94D", "#FFD43B", "#69DB7C", "#38D9A9", "#4DABF7", "#748FFC", "#DA77F2", "#F783AC", "#A9A9B3" };

        public static Brush TagBrush(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return Brushes.Gray;
            int h = 0;
            foreach (var c in tag.ToLowerInvariant()) h = unchecked(h * 31 + c);
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(TagColors[Math.Abs(h % TagColors.Length)]));
            b.Freeze();
            return b;
        }

        public static string Css(string key)
        {
            if (Application.Current.Resources[key] is SolidColorBrush b)
            {
                var c = b.Color;
                if (c.A < 255 && (key == "LayerBrush" || key == "PanelBrush" || key == "BgBrush"))
                    c = Color.FromRgb(c.R, c.G, c.B);
                return $"rgba({c.R},{c.G},{c.B},{(c.A / 255.0).ToString("0.###", CultureInfo.InvariantCulture)})";
            }
            return "#888";
        }
    }
}
