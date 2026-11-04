using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StickFix.Core;

namespace StickFix.App;

public partial class HomePage : UserControl
{
    private static readonly (ushort Bit, string Label)[] ButtonLabels =
    [
        (PadButtons.A, "A"), (PadButtons.B, "B"), (PadButtons.X, "X"), (PadButtons.Y, "Y"),
        (PadButtons.LeftShoulder, "LB"), (PadButtons.RightShoulder, "RB"),
        (PadButtons.Back, "View"), (PadButtons.Start, "Menu"), (PadButtons.Guide, "Guide"),
        (PadButtons.LeftThumb, "L3"), (PadButtons.RightThumb, "R3"),
        (PadButtons.DpadUp, "Up"), (PadButtons.DpadDown, "Down"), (PadButtons.DpadLeft, "Left"), (PadButtons.DpadRight, "Right"),
    ];

    private readonly Dictionary<ushort, (Border Box, TextBlock Text)> _chips = [];
    private PadService _service = null!;
    private Action _openCalibrate = () => { };
    private string? _hideMessage;
    private bool _syncingHide;
    private Brush _accent = Brushes.Transparent, _onAccent = Brushes.Black, _text = Brushes.White, _border = Brushes.Gray;

    public bool CorrectionWanted => CorrectionCheck.IsChecked == true;

    public HomePage()
    {
        InitializeComponent();
        foreach (var (bit, label) in ButtonLabels)
        {
            var text = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center };
            var box = new Border
            {
                Child = text, MinWidth = 46, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(2),
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            };
            _chips[bit] = (box, text);
            ButtonsPanel.Children.Add(box);
        }
    }

    public void Attach(PadService service, Action openCalibrate)
    {
        _service = service;
        _openCalibrate = openCalibrate;
        _accent = (Brush)FindResource("AccentBrush");
        _onAccent = (Brush)FindResource("OnAccentBrush");
        _text = (Brush)FindResource("TextBrush");
        _border = (Brush)FindResource("CardBorderBrush");
        _syncingHide = true;
        HideCheck.IsChecked = service.HideFromGames;
        _syncingHide = false;
    }

    public void SetCorrection(bool on) => CorrectionCheck.IsChecked = on;

    public string TrayStatus(PadView v) =>
        !v.Connected ? "controller not connected"
        : !v.Running ? "stopped"
        : !v.CorrectionOn ? "running, drift fix off"
        : v.Hidden ? "drift fix on, real controller hidden"
        : "drift fix on, real controller visible";

    public void Update(PadView v)
    {
        var profile = _service.Profile;

        Guidance.Text = !v.Connected ? "Plug in your controller."
            : profile is null ? "Calibrate first so StickFix can fix your drift."
            : !v.Running ? "Press Start to fix your drift."
            : !v.Hidden ? "Drift fix is running. Hide the real controller so games use only the fixed one."
            : "All set. Minimize StickFix to the tray and play.";

        var ready = v.Running && v.Hidden && v.CorrectionOn;
        StateText.Text = !v.Connected ? "NOT CONNECTED" : ready ? "ACTIVE" : v.Running ? "RUNNING" : "STOPPED";
        StatePill.Background = (Brush)FindResource(ready ? "GoodBrush" : "CardBorderBrush");
        StateText.Foreground = (Brush)FindResource(ready ? "OnGoodBrush" : "TextBrush");

        ControllerStatus.Text = v.Connected ? $"{v.ControllerName}, connected" : "Not connected";
        VirtualStatus.Text = !v.Running ? "Off"
            : v.Mismatches == 0 ? $"On (slot {v.VirtualSlot}), output verified"
            : $"On (slot {v.VirtualSlot}), {v.Mismatches} read-back mismatches. Check Help.";
        GamesStatus.Text = !v.Connected && !v.Running ? "No controller"
            : !v.Running ? "Your real controller, with drift"
            : v.Hidden ? (v.CorrectionOn ? "Only the corrected controller" : "Only the virtual controller, with drift (Fix drift is off)")
            : "Both controllers. The real one still drifts until you hide it";

        var problem = v.Problem ?? _hideMessage;
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;

        StartStop.Content = v.Running ? "Stop" : "Start";
        StartStop.IsEnabled = v.Connected || v.Running;
        CalibrateNow.IsEnabled = v.Connected;
        GetViGEm.Visibility = v.DriverMissing ? Visibility.Visible : Visibility.Collapsed;
        GetHidHide.Visibility = _service.HidHideInstalled ? Visibility.Collapsed : Visibility.Visible;
        CorrectionCheck.IsEnabled = profile is not null;

        ShowStick(LeftStick, LeftText, v.Raw.LeftX, v.Raw.LeftY, v.Output.LeftX, v.Output.LeftY, profile?.Left, v);
        ShowStick(RightStick, RightText, v.Raw.RightX, v.Raw.RightY, v.Output.RightX, v.Output.RightY, profile?.Right, v);

        LeftTrigger.Value = v.Raw.LeftTrigger;
        RightTrigger.Value = v.Raw.RightTrigger;
        LeftTriggerText.Text = $"LT {v.Raw.LeftTrigger * 100 / 255}%";
        RightTriggerText.Text = $"RT {v.Raw.RightTrigger * 100 / 255}%";

        foreach (var (bit, (box, text)) in _chips)
        {
            var pressed = (v.Raw.Buttons & bit) != 0;
            box.BorderBrush = pressed ? _accent : _border;
            box.Background = pressed ? _accent : Brushes.Transparent;
            text.Foreground = pressed ? _onAccent : _text;
            text.FontWeight = pressed ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private static void ShowStick(StickView view, TextBlock label, short rawX, short rawY, short outX, short outY, StickSettings? settings, PadView v)
    {
        double rx = Axis.ToUnit(rawX), ry = Axis.ToUnit(rawY), ox = Axis.ToUnit(outX), oy = Axis.ToUnit(outY);
        view.Update(rx, ry, ox, oy, settings?.DeadzoneInner ?? 0, v.Running);
        label.Text = $"Raw   X {rx,6:+0.000;-0.000}  Y {ry,6:+0.000;-0.000}\n"
                   + (v.Running ? $"Games X {ox,6:+0.000;-0.000}  Y {oy,6:+0.000;-0.000}" : "Games (not running)");
    }

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        _hideMessage = null;
        if (_service.IsRunning) _service.UserStop();
        else _service.UserStart();
    }

    private void OnCalibrate(object sender, RoutedEventArgs e) => _openCalibrate();

    private void OnCorrection(object sender, RoutedEventArgs e)
    {
        if (_service is not null) _service.SetCorrection(CorrectionCheck.IsChecked == true);
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        if (_syncingHide || _service is null) return;
        _hideMessage = _service.SetHide(HideCheck.IsChecked == true);
        if (_hideMessage is null) return;

        _syncingHide = true;
        HideCheck.IsChecked = false;
        _syncingHide = false;
    }

    private void OnGetViGEm(object sender, RoutedEventArgs e) => Links.Open(Links.ViGEm);

    private void OnGetHidHide(object sender, RoutedEventArgs e) => Links.Open(Links.HidHide);
}
