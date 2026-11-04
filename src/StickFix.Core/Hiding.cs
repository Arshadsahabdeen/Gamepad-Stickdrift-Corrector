using System.Text.Json;

namespace StickFix.Core;

public interface IHidingDriver
{
    bool IsActive { get; set; }
    bool IsAppListInverted { get; }
    IReadOnlyList<string> BlockedIds { get; }
    IReadOnlyList<string> AppPaths { get; }
    void AddBlocked(string instanceId);
    void RemoveBlocked(string instanceId);
    void AddApp(string path);
    void RemoveApp(string path);
}

public sealed class HidingException(string message) : Exception(message);

/// <summary>
/// Hiding outlives this process, so every change is written to a journal first. Whoever runs next (the app,
/// the watchdog, or the logon recovery) can undo exactly what we did and leave the user's own entries alone.
/// </summary>
public sealed class HidingGuard(IHidingDriver driver, string journalPath)
{
    private sealed record Journal(bool WasActive, List<string> AddedIds, List<string> AddedApps);

    public bool IsEngaged => File.Exists(journalPath);

    /// <summary>Safe to call again after a replug, when the pad may come back with new instance IDs.</summary>
    public void Hide(IReadOnlyCollection<string> instanceIds, string appPath)
    {
        if (instanceIds.Count == 0) throw new HidingException("No controller to hide was found.");
        if (driver.IsAppListInverted)
            throw new HidingException("HidHide is set to inverted mode, which this app doesn't support. Turn it off in the HidHide client.");

        var journal = Read() ?? new Journal(driver.IsActive, [], []);
        var newIds = instanceIds.Where(id => !Contains(driver.BlockedIds, id)).ToList();
        var addApp = !Contains(driver.AppPaths, appPath);
        journal.AddedIds.AddRange(newIds);
        if (addApp) journal.AddedApps.Add(appPath);
        Write(journal);

        try
        {
            // The app must be allowed before the pad vanishes, or we'd lose our own input.
            if (addApp) driver.AddApp(appPath);
            foreach (var id in newIds) driver.AddBlocked(id);
            driver.IsActive = true;
        }
        catch
        {
            Restore();
            throw;
        }
    }

    /// <summary>Returns false when there was nothing to undo.</summary>
    public bool Restore()
    {
        if (Read() is not { } journal) return false;

        foreach (var id in journal.AddedIds)
            if (Contains(driver.BlockedIds, id)) driver.RemoveBlocked(id);
        foreach (var app in journal.AddedApps)
            if (Contains(driver.AppPaths, app)) driver.RemoveApp(app);
        driver.IsActive = journal.WasActive;

        File.Delete(journalPath);
        return true;
    }

    private static bool Contains(IReadOnlyList<string> list, string value) =>
        list.Contains(value, StringComparer.OrdinalIgnoreCase);

    private Journal? Read()
    {
        if (!File.Exists(journalPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath));
        }
        catch (JsonException e)
        {
            throw new HidingException($"The recovery file '{journalPath}' is damaged ({e.Message}). Use the HidHide client to turn hiding off, then delete the file.");
        }
    }

    private void Write(Journal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        var temp = journalPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(journal));
        File.Move(temp, journalPath, overwrite: true);
    }
}
