using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FontShelf.Models;
using FontShelf.Services;

namespace FontShelf
{
    /// <summary>Prozor sa svim znakovima fonta; klik odabire znak.</summary>
    public class GlyphPickerWindow : Window
    {
        public string Picked { get; private set; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public GlyphPickerWindow(FontItem font, string title)
        {
            Title = title;
            Width = 860; Height = 640; MinWidth = 420; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            UseLayoutRounding = true;
            SetResourceReference(BackgroundProperty, "PanelBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            SourceInitialized += (s, e) =>
            {
                try
                {
                    int dark = ThemeManager.IsLight ? 0 : 1;
                    DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
                }
                catch { }
            };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            var root = new DockPanel { Margin = new Thickness(20, 16, 12, 16) };
            var info = new TextBlock { Margin = new Thickness(4, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
            info.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            DockPanel.SetDock(info, Dock.Top);
            root.Children.Add(info);

            var panel = new WrapPanel();
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
            root.Children.Add(scroll);
            Content = root;

            int total = 0;
            var glyphs = FontAnalyzer.GetGlyphs(font, out total);
            if (glyphs == null || glyphs.Count == 0)
            {
                info.Text = "Znakove ovog fonta nije moguće prikazati.";
                return;
            }
            info.Text = glyphs.Count < total
                ? $"{font.Name} — prikazano prvih {glyphs.Count} od {total} znakova. Klikni znak koji želiš upotrijebiti."
                : $"{font.Name} — {total} znakova. Klikni znak koji želiš upotrijebiti.";

            // interpunkcija i simboli (najbolji kandidati za akcente) idu na početak
            bool Likely(int cp) => (cp < 0x30) || (cp >= 0x3A && cp <= 0x40) || (cp >= 0x5B && cp <= 0x60) || (cp >= 0x7B && cp < 0xC0)
                                   || (cp >= 0x2B0 && cp <= 0x36F) || (cp >= 0x2010 && cp <= 0x203A);
            foreach (var g in glyphs.OrderBy(x => Likely(x.CodePoint) ? 0 : 1).ThenBy(x => x.CodePoint))
            {
                var text = new TextBlock
                {
                    Text = g.Text, FontFamily = font.Family, FontSize = 30,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                var cell = new Border
                {
                    Width = 62, Height = 62, Margin = new Thickness(4), CornerRadius = new CornerRadius(8),
                    Child = text, Cursor = Cursors.Hand, ToolTip = $"{g.Text}   {g.Code}", Tag = g.Text
                };
                cell.SetResourceReference(Border.BackgroundProperty, "CardBrush");
                cell.MouseEnter += (s, e) => ((Border)s).SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                cell.MouseLeave += (s, e) => ((Border)s).SetResourceReference(Border.BackgroundProperty, "CardBrush");
                cell.MouseLeftButtonUp += (s, e) => { Picked = (string)((Border)s).Tag; DialogResult = true; };
                panel.Children.Add(cell);
            }
        }
    }
}
