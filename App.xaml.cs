using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DrawingTakeoff;

public partial class App : Application
{
    private int _dialogOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        Logger.Init();
        Logger.Debug($"App start. OS={Environment.OSVersion} .NET={Environment.Version} 64bit={Environment.Is64BitProcess} CPU={Environment.ProcessorCount} dir={AppContext.BaseDirectory}");

        DispatcherUnhandledException += (_, a) =>
        {
            Logger.Error("Unhandled UI exception", a.Exception);
            WriteCrashFile(a.Exception);
            a.Handled = true;
            ShowError("Unexpected error", a.Exception);
            // nothing visible left? then do not linger as an invisible process
            if (MainWindow is null || !MainWindow.IsVisible) Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            var ex = a.ExceptionObject as Exception;
            Logger.Error("Fatal unhandled exception", ex);
            WriteCrashFile(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Logger.Error("Unobserved task exception", a.Exception);
            a.SetObserved();
        };

        base.OnStartup(e);

        try
        {
            var w = new MainWindow();
            MainWindow = w;
            w.Show();
            Logger.Debug("MainWindow shown.");
        }
        catch (Exception ex)
        {
            Logger.Error("Startup failed while creating the main window", ex);
            WriteCrashFile(ex);
            ShowError("Startup failed", ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Shutdown();
        base.OnExit(e);
    }

    private void ShowError(string title, Exception? ex)
    {
        if (Interlocked.Exchange(ref _dialogOpen, 1) == 1) return;   // never stack dialogs
        try
        {
            var detail = ex?.ToString() ?? "(no details)";
            if (detail.Length > 1800) detail = detail[..1800] + "\n...";
            MessageBox.Show($"{title}\n\n{ex?.GetType().Name}: {ex?.Message}\n\nLog folder:\n{Logger.LogDirectory}\n\n{detail}",
                            "Drawing Takeoff Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        finally { Interlocked.Exchange(ref _dialogOpen, 0); }
    }

    private static void WriteCrashFile(Exception? ex)
    {
        try
        {
            var dir = Logger.LogDirectory.Length > 0 ? Logger.LogDirectory : Path.GetTempPath();
            File.WriteAllText(Path.Combine(dir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt"),
                $"{DateTime.Now:O}\nOS {Environment.OSVersion}  .NET {Environment.Version}\n\n{ex}");
        }
        catch { }
    }
}
