using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace StickFix.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan VisibleRate = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan HiddenRate = TimeSpan.FromMilliseconds(250);

    private readonly PadService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = VisibleRate };
    private readonly Tray _tray;

    public MainWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Tabs.Tag = $"Version {version?.ToString(3)}";

        Home.Attach(_service, () => Tabs.SelectedIndex = 1);
        Calibrate.Attach(_service, () => Tabs.SelectedIndex = 0);
        Tune.Attach(_service);
        Help.Attach(_service);

        _tray = new Tray(ShowFromTray, on => Home.SetCorrection(on), () => Application.Current.Shutdown());
        Application.Current.Exit += (_, _) =>
        {
            _timer.Stop();
            _service.Dispose();
            _tray.Dispose();
        };

        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) return;
            MoveToTray();
            _tray.TellUserOnce();
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public void StartInTray() => _timer.Interval = HiddenRate;

    public void ShowFromTray()
    {
        _timer.Interval = VisibleRate;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // Hidden rather than minimized, which is what takes it off the taskbar. The engine has its own thread and keeps running.
    private void MoveToTray()
    {
        _timer.Interval = HiddenRate;
        Hide();
    }

    private void Refresh()
    {
        // The wizard reads the pad directly and owns it while measuring.
        if (Calibrate.IsMeasuring) return;

        var view = _service.Poll();
        _tray.Update(Home.CorrectionWanted, Home.TrayStatus(view));
        if (!IsVisible) return;

        if (Tabs.SelectedIndex == 0) Home.Update(view);
        else if (Tabs.SelectedIndex == 1) Calibrate.Update(view);
        else if (Tabs.SelectedIndex == 2) Tune.Update(view);
    }
}
