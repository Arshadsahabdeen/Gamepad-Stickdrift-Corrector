using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StickFix.Core;

namespace StickFix.App;

public partial class TunePage : UserControl
{
    private PadService _service = null!;
    private Profile? _shown;

    public TunePage() => InitializeComponent();

    public void Attach(PadService service)
    {
        _service = service;
        LeftTuner.Changed += settings => Save(p => p with { Left = settings });
        RightTuner.Changed += settings => Save(p => p with { Right = settings });
    }

    public void Update(PadView v)
    {
        var profile = _service.Profile;
        if (!ReferenceEquals(profile, _shown))
        {
            _shown = profile;
            LeftTuner.Load(profile?.Left);
            RightTuner.Load(profile?.Right);
        }
        var hasProfile = profile is not null;
        LeftTuner.IsEnabled = hasProfile;
        RightTuner.IsEnabled = hasProfile;
        Note.Text = hasProfile
            ? "Optional. Most people don't need this. Changes apply right away and save automatically."
            : "Calibrate first. These settings build on your calibration.";

        LeftTuner.ShowLive(v.Raw.LeftX, v.Raw.LeftY, v.Output.LeftX, v.Output.LeftY, v.Running);
        RightTuner.ShowLive(v.Raw.RightX, v.Raw.RightY, v.Output.RightX, v.Output.RightY, v.Running);
    }

    private void Save(Func<Profile, Profile> change)
    {
        if (_service.Profile is not { } profile) return;
        try
        {
            var updated = change(profile);
            _service.SetProfile(updated);
            _shown = updated;
            Status.Text = "";
        }
        catch (ProfileException e)
        {
            Status.Text = e.Message;
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_service.Profile is not { } profile)
        {
            Status.Text = "Nothing to export yet. Calibrate first.";
            return;
        }
        var dialog = new SaveFileDialog { FileName = "StickFix profile.json", Filter = "Profile (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        ProfileStore.Save(profile, dialog.FileName);
        Status.Text = $"Saved to {dialog.FileName}";
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Profile (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _service.SetProfile(ProfileStore.Load(dialog.FileName) with { ControllerId = _service.ControllerId });
            Status.Text = "Profile imported.";
        }
        catch (Exception ex) when (ex is ProfileException or System.IO.IOException)
        {
            Status.Text = ex.Message;
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Remove all correction for this controller?", "StickFix", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _service.DeleteProfile();
        Status.Text = "Calibration deleted.";
    }
}
