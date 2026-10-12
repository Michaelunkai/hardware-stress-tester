namespace StressLab.Monitoring;

public sealed class MetricsSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public float? CpuTempC { get; init; }
    public float? CpuLoadPercent { get; init; }
    public float? CpuPowerW { get; init; }
    public float? CpuClockMhz { get; init; }
    public float? GpuTempC { get; init; }
    public float? GpuLoadPercent { get; init; }
    public float? GpuMemUsedMb { get; init; }
    public float? GpuMemTotalMb { get; init; }
    public float? RamUsedPercent { get; init; }
    public float? RamUsedGb { get; init; }
    public float? RamTotalGb { get; init; }
    public float? FansRpm { get; init; }

    public static string F(float? value, string format = "0", string suffix = "")
        => value is null ? "—" : value.Value.ToString(format) + suffix;
}
