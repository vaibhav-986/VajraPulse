using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace VajraPulse;

public partial class App : Application
{
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, "Local\\VajraPulse.Desktop", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Vajra Pulse is already running. Look for its tray icon or press Ctrl + Alt + Space.",
                "Vajra Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VajraPulse");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "last-error.txt"), args.Exception.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            args.Handled = true;
            MessageBox.Show("Vajra Pulse encountered an error and will close. Details were saved to %LOCALAPPDATA%\\VajraPulse\\last-error.txt.", "Vajra Pulse");
            Shutdown(1);
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
