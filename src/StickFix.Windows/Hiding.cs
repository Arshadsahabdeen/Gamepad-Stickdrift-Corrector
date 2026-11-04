using System.Text.RegularExpressions;
using Nefarius.Drivers.HidHide;
using Nefarius.Utilities.DeviceManagement.PnP;
using StickFix.Core;

namespace StickFix.Windows;

public sealed class HidHideDriver : IHidingDriver
{
    private readonly HidHideControlService _service = new();

    public bool IsInstalled => _service.IsInstalled;

    public bool IsActive
    {
        get => _service.IsActive;
        set => _service.IsActive = value;
    }

    public bool IsAppListInverted => _service.IsAppListInverted;
    public IReadOnlyList<string> BlockedIds => _service.BlockedInstanceIds.ToList();
    public IReadOnlyList<string> AppPaths => _service.ApplicationPaths.ToList();
    public void AddBlocked(string instanceId) => _service.AddBlockedInstanceId(instanceId);
    public void RemoveBlocked(string instanceId) => _service.RemoveBlockedInstanceId(instanceId);
    public void AddApp(string path) => _service.AddApplicationPath(path, true);
    public void RemoveApp(string path) => _service.RemoveApplicationPath(path);
}

/// <summary>A real XInput-compatible pad and every Windows device node that has to be hidden for it.</summary>
public sealed record PhysicalDevice(string Vendor, string Product, IReadOnlyList<string> InstanceIds)
{
    // No serial: a real PowerA 20D6:2005 reported a different one after reconnecting, stranding its calibration.
    // shortcut: two identical pads share a profile, add a controller picker when multi-pad use matters.
    public string ControllerId => $"{Vendor}-{Product}";

    public string DisplayName => (Vendor.ToUpperInvariant() switch
    {
        "20D6" => "PowerA",
        "045E" => "Microsoft",
        "24C6" => "PowerA / ThrustMaster",
        "0E6F" => "PDP",
        "0F0D" => "Hori",
        _ => "Controller",
    }) + $" {Vendor}:{Product}";
}

public static partial class DeviceTree
{
    /// <summary>
    /// Walks up from each XInput HID node (IG_xx) to the USB device behind it. A chain that ends at ROOT\ is a
    /// software device such as our own ViGEm pad, and must never be hidden.
    /// </summary>
    public static IReadOnlyList<PhysicalDevice> FindPhysical()
    {
        var found = new Dictionary<string, PhysicalDevice>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; Devcon.FindByInterfaceGuid(DeviceInterfaceIds.HidDevice, out PnPDevice device, i, true); i++)
        {
            if (!device.InstanceId.Contains("&IG_", StringComparison.OrdinalIgnoreCase)) continue;
            if (Walk(device) is { } physical) found[physical.ControllerId] = physical;
        }
        return found.Values.ToList();
    }

    private static PhysicalDevice? Walk(IPnPDevice start)
    {
        var chain = new List<string>();
        IPnPDevice? node = start;
        while (node is not null)
        {
            var id = node.InstanceId;
            if (id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) return null;
            if (!id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase) && !id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)) break;
            if (IsSharedUsbNode(node)) break;
            chain.Add(id);
            node = node.Parent;
        }
        if (chain.Count == 0) return null;

        // The topmost node carries the real maker's IDs: USB\VID_20D6&PID_2005\....
        var match = VidPid().Match(chain[^1]);
        return new PhysicalDevice(
            match.Success ? match.Groups[1].Value : "0000",
            match.Success ? match.Groups[2].Value : "0000",
            chain);
    }

    // Hiding a hub, or a composite device that also holds a keyboard, would take out more than the pad.
    private static bool IsSharedUsbNode(IPnPDevice node) =>
        node.CompatibleIds?.Any(id =>
            id.StartsWith(@"USB\Class_09", StringComparison.OrdinalIgnoreCase) ||
            id.Equals(@"USB\COMPOSITE", StringComparison.OrdinalIgnoreCase)) == true;

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")]
    private static partial Regex VidPid();
}
