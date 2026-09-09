using System.Diagnostics;
using static Hindsight.Core.Win32.NativeMethods;

namespace Hindsight.Core.Etw;

/// <summary>Measures CPU cycles per CPU second on the own process to convert ETW CPUCycleCount to time.</summary>
public static class CycleCalibrator
{
    private const double Fallback = 3e9;

    public static double CyclesPerSecond()
    {
        IntPtr me = GetCurrentProcess();
        if (!QueryProcessCycleTime(me, out ulong c0) || !GetProcessTimes(me, out _, out _, out long k0, out long u0)) return Fallback;
        var sw = Stopwatch.StartNew();
        double sink = 0;
        while (sw.ElapsedMilliseconds < 120) sink += Math.Sqrt(sw.ElapsedTicks);
        GC.KeepAlive(sink);
        if (!QueryProcessCycleTime(me, out ulong c1) || !GetProcessTimes(me, out _, out _, out long k1, out long u1)) return Fallback;
        double cpuSeconds = ((k1 - k0) + (u1 - u0)) / 1e7;
        if (cpuSeconds < 0.02 || c1 <= c0) return Fallback;
        double cps = (c1 - c0) / cpuSeconds;
        return cps is > 5e8 and < 1e11 ? cps : Fallback;
    }
}
