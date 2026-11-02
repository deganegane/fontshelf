using System.Windows;
using System.Windows.Threading;

namespace FontShelf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnUnhandled;
            base.OnStartup(e);
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("Dogodila se greška:\n\n" + e.Exception.Message, "FontShelf",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
