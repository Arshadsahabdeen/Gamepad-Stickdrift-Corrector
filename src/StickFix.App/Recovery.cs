using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using StickFix.Core;
using StickFix.Windows;

namespace StickFix.App;

internal static class Paths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StickFix");

    public static readonly string Journal = Path.Combine(Data, "hiding.json");
    public static readonly string LogFile = Path.Combine(Data, "stickfix.log");
    public static readonly string Settings = Path.Combine(Data, "settings.json");
    public static readonly string Exe = Environment.ProcessPath!;
}

internal static class Log
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never take the input engine down with it.
        }
    }
}

internal sealed class AppSettings
{
    public bool HideFromGames { get; set; }

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(this));
    }
}

internal static class StartupEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValue = "StickFix", RecoveryValue = "StickFixRecovery";

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppValue) is not null;
        }
        set => Set(AppValue, value ? $"\"{Paths.Exe}\" --minimized" : null);
    }

    /// <summary>While hidden, the next logon must undo it even if the PC lost power with us running.</summary>
    public static void SetRecovery(bool armed) => Set(RecoveryValue, armed ? $"\"{Paths.Exe}\" --recover" : null);

    private static void Set(string name, string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (value is null) key.DeleteValue(name, false);
        else key.SetValue(name, value);
    }
}

internal static class Recovery
{
    public static HidingGuard Guard(HidHideDriver driver) => new(driver, Paths.Journal);

    /// <summary>Runs in a separate process, so a crash or a Task Manager kill of the app still restores the pad.</summary>
    public static void WatchAndRestore(int pid)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            parent.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Already gone: restore right away.
        }
        RestoreNow();
    }

    public static void RestoreNow()
    {
        // Only one process can hold the driver at a time, so give whoever has it a moment to finish.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(Paths.Journal))
                {
                    StartupEntry.SetRecovery(false);
                    return;
                }
                if (Guard(new HidHideDriver()).Restore()) Log.Write("Recovery restored controller visibility.");
                StartupEntry.SetRecovery(false);
                return;
            }
            catch (Exception e)
            {
                Log.Write($"Recovery attempt {attempt + 1} failed: {e.Message}");
                Thread.Sleep(500);
            }
        }
    }
}
