using System.Windows.Forms;

namespace StickFix.App;

/// <summary>The notification-area icon that keeps StickFix reachable while its window is hidden.</summary>
public sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _fixDrift;
    private bool _toldUser;

    public Tray(Action open, Action<bool> setCorrection, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open StickFix", null, (_, _) => open());
        _fixDrift = new ToolStripMenuItem("Fix drift") { CheckOnClick = true };
        _fixDrift.Click += (_, _) => setCorrection(_fixDrift.Checked);
        menu.Items.Add(_fixDrift);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Paths.Exe),
            Text = "StickFix",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void Update(bool correctionOn, string status)
    {
        _fixDrift.Checked = correctionOn;
        // Windows rejects tooltips longer than 127 characters.
        var text = $"StickFix: {status}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void TellUserOnce()
    {
        if (_toldUser) return;
        _toldUser = true;
        _icon.ShowBalloonTip(3000, "StickFix is still running", "It's working in the system tray. Double-click the icon to open it.", ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
