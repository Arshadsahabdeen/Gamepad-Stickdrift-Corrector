using StickFix.Windows;
using Xunit;

namespace StickFix.Tests;

/// <summary>Real-device checks. They do nothing on a PC without the driver, so they only prove something where it's installed.</summary>
public class HardwareTests
{
    [Fact]
    public void Our_virtual_pad_is_never_a_candidate_for_hiding()
    {
        ViGEmPad pad;
        try
        {
            pad = ViGEmPad.Create();
        }
        catch (VirtualPadException)
        {
            return;
        }

        using (pad)
        {
            Assert.All(DeviceTree.FindPhysical().SelectMany(d => d.InstanceIds), id =>
            {
                Assert.DoesNotContain("PID_028E", id, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ROOT_HUB", id, StringComparison.OrdinalIgnoreCase);
                Assert.False(id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase));
            });
        }
    }
}
