using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StickFix.Windows;

namespace StickFix.App;

public partial class HelpPage : UserControl
{
    private PadService _service = null!;

    public HelpPage() => InitializeComponent();

    public void Attach(PadService service)
    {
        _service = service;
        Startup.IsChecked = StartupEntry.StartsWithWindows;
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        _service.UserStop();
        _service.SetHide(false);
        RestoreStatus.Text = File.Exists(Paths.Journal)
            ? "Couldn't reach HidHide. Close the HidHide app if it's open, then try again."
            : "Done. StickFix has stopped and your real controller is visible again. Replug it if a game still can't see it.";
    }

    private void OnStartup(object sender, RoutedEventArgs e)
    {
        var wanted = Startup.IsChecked == true;
        if (wanted != StartupEntry.StartsWithWindows) StartupEntry.StartsWithWindows = wanted;
    }

    private void OnTest(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = _service.RunVirtualTest();
            ToolStatus.Text = result.Passed
                ? $"Passed: all {result.Steps} states came back identical."
                : $"Failed: {result.Failures.Count} of {result.Steps} states didn't match. First: {result.Failures[0]}";
        }
        catch (VirtualPadException ex)
        {
            ToolStatus.Text = ex.Message;
        }
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "StickFix report.txt", Filter = "Text (*.txt)|*.txt" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, _service.BuildReport());
        ToolStatus.Text = $"Saved to {dialog.FileName}. It includes your controller's serial number, so check before sharing.";
    }

    private void OnLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Paths.Data);
        Process.Start(new ProcessStartInfo(Paths.Data) { UseShellExecute = true });
    }
}
