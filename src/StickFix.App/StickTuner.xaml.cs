using System.Windows;
using System.Windows.Controls;
using StickFix.Core;

namespace StickFix.App;

public partial class StickTuner : UserControl
{
    private StickSettings? _settings;
    private bool _loading;

    public event Action<StickSettings>? Changed;

    public string StickName
    {
        get => Title.Text;
        set => Title.Text = value;
    }

    public StickTuner() => InitializeComponent();

    public void Load(StickSettings? settings)
    {
        _settings = settings;
        var s = settings ?? new StickSettings();
        _loading = true;
        Deadzone.Value = s.DeadzoneInner;
        Outer.Value = s.DeadzoneOuter;
        Sensitivity.Value = s.Sensitivity;
        Curve.Value = s.Gamma;
        Smooth.IsChecked = s.FilterEnabled;
        InvertX.IsChecked = s.X.Invert;
        InvertY.IsChecked = s.Y.Invert;
        _loading = false;
        UpdateLabels();
    }

    public void ShowLive(short rawX, short rawY, short outX, short outY, bool running) =>
        Preview.Update(Axis.ToUnit(rawX), Axis.ToUnit(rawY), Axis.ToUnit(outX), Axis.ToUnit(outY), Deadzone.Value, running);

    private void UpdateLabels()
    {
        DeadzoneLabel.Text = $"Deadzone: {Deadzone.Value:P1}";
        OuterLabel.Text = $"Full output at: {Outer.Value:P0}";
        SensitivityLabel.Text = $"Sensitivity: {Sensitivity.Value:0.00}";
        CurveLabel.Text = $"Response curve: {Curve.Value:0.00}";
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized || _settings is null) return;
        UpdateLabels();

        _settings = _settings with
        {
            DeadzoneInner = Deadzone.Value,
            // Hysteresis follows the deadzone, as it does in calibration.
            Hysteresis = Deadzone.Value * 0.25,
            DeadzoneOuter = Outer.Value,
            Sensitivity = Sensitivity.Value,
            Gamma = Curve.Value,
            FilterEnabled = Smooth.IsChecked == true,
            X = _settings.X with { Invert = InvertX.IsChecked == true },
            Y = _settings.Y with { Invert = InvertY.IsChecked == true },
        };
        Changed?.Invoke(_settings);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _loading = true;
        Outer.Value = 1;
        Sensitivity.Value = 1;
        Curve.Value = 1;
        Smooth.IsChecked = false;
        _loading = false;
        OnChanged(sender, e);
    }
}
