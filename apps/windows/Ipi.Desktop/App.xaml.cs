using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Ipi.Desktop.Services;

namespace Ipi.Desktop;

public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            try
            {
                MessageBox.Show(
                    "ipi encountered an unexpected error and must close. Details were written to the local crash log.",
                    "ipi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // The dispatcher may already be partially unavailable.
            }
            args.Handled = true;
            Shutdown(-1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        var setupMode = e.Args.Any(arg => arg.Equals("--setup", StringComparison.OrdinalIgnoreCase) || arg.Equals("--setup-preview", StringComparison.OrdinalIgnoreCase));
        var postInstallMode = e.Args.Any(arg => arg.Equals("--post-install", StringComparison.OrdinalIgnoreCase));
        var runtimeReady = false;
        if (!setupMode)
        {
            try { runtimeReady = new RuntimeBootstrapService().Inspect().IsReady; }
            catch { runtimeReady = false; }
        }

        Window window = setupMode || !runtimeReady
            ? new SetupPreviewWindow(postInstallMode || !setupMode)
            : new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var dir = IpiPathService.AppDataDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {source}\n{exception}\n\n");
        }
        catch
        {
            // Last-resort logging must never crash the app.
        }
    }
}
