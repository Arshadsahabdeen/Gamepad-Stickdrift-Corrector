using System.Runtime.InteropServices;

namespace StickFix.Windows;

/// <summary>Makes Thread.Sleep(1) take about 1 ms instead of up to 15.6 ms, for this process only.</summary>
public sealed class HighResTimer : IDisposable
{
    public HighResTimer() => timeBeginPeriod(1);

    public void Dispose() => timeEndPeriod(1);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
