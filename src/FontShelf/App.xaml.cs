using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FontShelf.Services;

namespace FontShelf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Način bez sučelja (koristi ga i automatski test u GitHub Actions):
            // FontShelf.exe --patch-hr ulaz.ttf izlaz.ttf
            if (e.Args.Length >= 3 && e.Args[0] == "--patch-hr")
            {
                int code = 0;
                try
                {
                    var r = FontPatcher.Patch(File.ReadAllBytes(e.Args[1]), new PatchOptions());
                    File.WriteAllBytes(e.Args[2], r.Data);
                    File.WriteAllText(e.Args[2] + ".log",
                        $"family={r.NewFamily}\nadded={string.Join(" ", r.Added)}\nskipped={string.Join(" | ", r.Skipped)}\n");
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(e.Args[2] + ".err", ex.ToString()); } catch { }
                    code = 1;
                }
                Shutdown(code);
                return;
            }

            DispatcherUnhandledException += OnUnhandled;
            base.OnStartup(e);
            var w = new MainWindow();
            MainWindow = w;
            w.Show();
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("Dogodila se greška:\n\n" + e.Exception.Message, "FontShelf",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
