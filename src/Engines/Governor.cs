namespace StressLab.Engines;

/// <summary>
/// Duty-cycle governor shared by all engines: every engine works in short bursts and
/// sleeps between bursts to approximate a user-chosen load percentage.
/// </summary>
public static class Governor
{
    public const double BurstMs = 25.0;

    public static int ComputeSleepMs(int loadPercent, double burstMs)
    {
        if (loadPercent >= 100) return 0;
        if (loadPercent <= 0) return (int)Math.Ceiling(burstMs * 9);
        return Math.Max(1, (int)Math.Round(burstMs * (100 - loadPercent) / loadPercent));
    }
}
