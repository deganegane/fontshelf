using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FontShelf.Models;
using FontShelf.Services;
using FontShelf.ViewModels;
using Microsoft.Win32;

namespace FontShelf
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm = new MainViewModel();
        private bool _syncing;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            _vm.RequestSelectAll += SelectAllEntry;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await _vm.InitializeAsync();
            if (LibraryList.SelectedItem == null && TagList.SelectedItem == null && FolderList.SelectedItem == null)
                SelectAllEntry();
        }

        private void Window_Closing(object sender, CancelEventArgs e) => _vm.SaveNow();

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

        private static void SafeCopy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }
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

        private void NewTagBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var name = NewTagBox.Text;
            if (_vm.CreateTag(name)) NewTagBox.Text = "";
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

        #region Kartice i usporedba

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

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            var f = _vm.SelectedFont;
            if (f == null) return;
            var r = MessageBox.Show(this,
                $"Trajno instalirati „{f.Name}” za tvoj korisnički račun?\n\nFont će biti dostupan i nakon ponovnog pokretanja računala.",
                "Instaliraj font", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.OK) await _vm.InstallAsync(f);
        }

        private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            var file = _vm.SelectedFont?.FirstFile;
            if (file == null || !File.Exists(file)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }); }
            catch { }
        }

        private void CopyName_Click(object sender, RoutedEventArgs e)
        {
            SafeCopy(_vm.SelectedFont?.Name);
            _vm.StatusText = "Naziv fonta kopiran.";
        }

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

        private void Glyph_Click(object sender, RoutedEventArgs e)
        {
            var g = Ctx<GlyphItem>(sender);
            if (g != null) _vm.SelectedGlyph = g;
        }

        private void CopyGlyph_Click(object sender, RoutedEventArgs e)
        {
            SafeCopy(_vm.SelectedGlyph?.Text);
            _vm.StatusText = "Znak kopiran.";
        }

        private void CopyGlyphCode_Click(object sender, RoutedEventArgs e)
        {
            SafeCopy(_vm.SelectedGlyph?.Code);
            _vm.StatusText = "Kod znaka kopiran.";
        }

        #endregion
    }
}
