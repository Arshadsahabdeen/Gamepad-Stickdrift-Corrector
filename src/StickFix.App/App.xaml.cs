using System.Diagnostics;
using System.Windows;

namespace StickFix.App;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length == 2 && args[0] == "--watchdog" && int.TryParse(args[1], out var pid))
        {
            Recovery.WatchAndRestore(pid);
            Shutdown();
            return;
        }
        if (args.Contains("--recover"))
        {
            Recovery.RestoreNow();
            Shutdown();
            return;
        }

        _single = new Mutex(true, @"Local\StickFixApp", out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StickFixShow");
        if (!isFirst)
        {
            // It's probably hiding in the tray, so opening it again should bring it back.
            _showSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"Unhandled: {ex.Exception}");
            MessageBox.Show($"Something went wrong: {ex.Exception.Message}\n\nDetails are in the log. Your controller's visibility will be restored when StickFix closes.", "StickFix");
            ex.Handled = true;
        };

        var window = new MainWindow();
        MainWindow = window;
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, false);

        if (args.Contains("--minimized")) window.StartInTray();
        else window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}

internal static class Links
{
    public const string ViGEm = "https://github.com/nefarius/ViGEmBus/releases";
    public const string HidHide = "https://github.com/nefarius/HidHide/releases";

    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
