using System.Windows;
using System.Windows.Media;

namespace DeskBuddy.App;

public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources[System.Windows.SystemFonts.MessageFontFamilyKey] = new FontFamily("Segoe UI");
        ControlStyles.Register(app.Resources);
        if (args.Length == 4 && args[0] == "--workspace-smoke")
        {
            // Isolated local-files/fake-peer fixture; never discovers hardware.
            app.Run(DeskBuddy.App.MainWindow.CreateWorkspaceSmoke(args[1], args[2], args[3]));
            return Environment.ExitCode;
        }
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show("Desk Buddy Hub stopped this operation. No command will be retried automatically.\n\n" + e.Exception.Message,
                "Desk Buddy Hub", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        var styleSmoke = Array.IndexOf(args, "--control-style-smoke");
        if (styleSmoke >= 0 && styleSmoke + 1 < args.Length)
        {
            app.Run(ControlStyleSmoke.CreateWindow(args[styleSmoke + 1]));
            return Environment.ExitCode;
        }
        var smoke = Array.IndexOf(args, "--render-smoke");
        string? dir = smoke >= 0 && smoke + 1 < args.Length ? args[smoke + 1] : null;
        var deviceSmoke = Array.IndexOf(args, "--device-smoke");
        string? deviceDir = deviceSmoke >= 0 && deviceSmoke + 1 < args.Length ? args[deviceSmoke + 1] : null;
        if (deviceDir != null && (dir != null || args.Contains("--demo"))) return 2;
        var window = new MainWindow(args.Contains("--demo") || dir != null, dir, deviceDir);
        app.Run(window);
        return Environment.ExitCode;
    }
}

