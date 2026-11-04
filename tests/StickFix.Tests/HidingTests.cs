using StickFix.Core;
using Xunit;

namespace StickFix.Tests;

internal sealed class FakeHidingDriver : IHidingDriver
{
    private readonly List<string> _ids = [], _apps = [];

    public bool IsActive { get; set; }
    public bool IsAppListInverted { get; set; }
    public IReadOnlyList<string> BlockedIds => _ids;
    public IReadOnlyList<string> AppPaths => _apps;
    public List<string> Calls { get; } = [];
    public bool FailOnBlock { get; set; }

    public void AddBlocked(string id)
    {
        if (FailOnBlock) throw new IOException("driver busy");
        Calls.Add($"block {id}");
        _ids.Add(id);
    }

    public void RemoveBlocked(string id) => _ids.Remove(id);

    public void AddApp(string path)
    {
        Calls.Add($"allow {path}");
        _apps.Add(path);
    }

    public void RemoveApp(string path) => _apps.Remove(path);
}

public sealed class HidingGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stickfix-hide").FullName;
    private readonly FakeHidingDriver _driver = new();
    private string Journal => Path.Combine(_dir, "hiding.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private HidingGuard Guard() => new(_driver, Journal);

    [Fact]
    public void App_is_allowed_before_any_device_is_blocked()
    {
        Guard().Hide(["HID\\A"], "C:\\app.exe");
        Assert.Equal(["allow C:\\app.exe", "block HID\\A"], _driver.Calls);
        Assert.True(_driver.IsActive);
    }

    [Fact]
    public void Restore_undoes_only_our_changes()
    {
        _driver.AddBlocked("HID\\USERS");
        _driver.AddApp("C:\\users.exe");
        _driver.IsActive = true;

        var guard = Guard();
        guard.Hide(["HID\\A", "HID\\USERS"], "C:\\app.exe");
        Assert.True(guard.Restore());

        Assert.Equal(["HID\\USERS"], _driver.BlockedIds);
        Assert.Equal(["C:\\users.exe"], _driver.AppPaths);
        Assert.True(_driver.IsActive);
        Assert.False(guard.IsEngaged);
    }

    [Fact]
    public void Restore_turns_hiding_back_off_if_it_was_off()
    {
        var guard = Guard();
        guard.Hide(["HID\\A"], "C:\\app.exe");
        guard.Restore();
        Assert.False(_driver.IsActive);
        Assert.Empty(_driver.BlockedIds);
        Assert.Empty(_driver.AppPaths);
    }

    [Fact]
    public void A_new_process_can_recover_after_a_crash()
    {
        Guard().Hide(["HID\\A"], "C:\\app.exe");
        Assert.True(Guard().Restore());
        Assert.Empty(_driver.BlockedIds);
    }

    [Fact]
    public void Hiding_again_after_replug_adds_new_ids_and_restores_all_of_them()
    {
        var guard = Guard();
        guard.Hide(["HID\\OLD"], "C:\\app.exe");
        guard.Hide(["HID\\NEW"], "C:\\app.exe");
        Assert.Equal(2, _driver.BlockedIds.Count);
        Assert.Single(_driver.AppPaths);

        guard.Restore();
        Assert.Empty(_driver.BlockedIds);
        Assert.False(_driver.IsActive);
    }

    [Fact]
    public void Driver_failure_midway_rolls_back_and_surfaces_the_error()
    {
        _driver.FailOnBlock = true;
        var guard = Guard();
        Assert.Throws<IOException>(() => guard.Hide(["HID\\A"], "C:\\app.exe"));
        Assert.False(guard.IsEngaged);
        Assert.False(_driver.IsActive);
        Assert.Empty(_driver.AppPaths);
    }

    [Fact]
    public void Refuses_inverted_mode_and_empty_lists_without_changing_anything()
    {
        Assert.Throws<HidingException>(() => Guard().Hide([], "C:\\app.exe"));
        _driver.IsAppListInverted = true;
        Assert.Throws<HidingException>(() => Guard().Hide(["HID\\A"], "C:\\app.exe"));
        Assert.Empty(_driver.Calls);
        Assert.False(Guard().IsEngaged);
    }

    [Fact]
    public void Restore_with_no_journal_does_nothing()
    {
        _driver.IsActive = true;
        Assert.False(Guard().Restore());
        Assert.True(_driver.IsActive);
    }

    [Fact]
    public void Damaged_journal_gives_a_clear_error_instead_of_guessing()
    {
        File.WriteAllText(Journal, "not json");
        Assert.Throws<HidingException>(() => Guard().Restore());
    }
}

public class CorrectionCheckTests
{
    [Fact]
    public void Reports_before_and_after_for_this_controllers_real_left_stick()
    {
        // Values measured on the PowerA 20D6:2005: stable at (-0.0526, +0.1989).
        var settings = new StickSettings
        {
            X = new AxisCalibration { Center = -0.0526, RangePositive = 1.0526 * 0.98, RangeNegative = 0.9474 * 0.98 },
            Y = new AxisCalibration { Center = 0.1989, RangePositive = 0.8011 * 0.98, RangeNegative = 1.1989 * 0.98 },
            DeadzoneInner = 0.02,
        };
        var check = Diagnosis.Check(settings, [(-0.0526, 0.1989)], [(1, 0.1989), (-1, 0.1989), (-0.0526, 1), (-0.0526, -1)]);

        Assert.Equal(0.2058, check.RestBefore, 3);
        Assert.Equal(0, check.RestAfter);
        Assert.Equal(1.0, check.MinReach, 6);
    }

    [Fact]
    public void A_sweep_that_misses_a_direction_shows_low_reach()
    {
        Assert.Equal(0, Diagnosis.Check(new StickSettings(), [(0, 0)], [(1, 0), (0, 1), (0, -1)]).MinReach);
    }
}
