namespace StressLab.Monitoring;

public static class ThermalWatchdog
{
    public const float DefaultCpuLimitC = 90f;
    public const float DefaultGpuLimitC = 85f;

    /// <summary>Returns a human-readable trip reason when a limit is breached, otherwise null.</summary>
    public static string? Check(MetricsSnapshot m, float cpuLimit, float gpuLimit, bool cpuActive, bool gpuActive)
    {
        if (cpuActive && m.CpuTempC is float cpuTemp && cpuTemp >= cpuLimit)
            return $"CPU temperature {cpuTemp:0}°C reached the {cpuLimit:0}°C safety limit";
        if (gpuActive && m.GpuTempC is float gpuTemp && gpuTemp >= gpuLimit)
            return $"GPU temperature {gpuTemp:0}°C reached the {gpuLimit:0}°C safety limit";
        return null;
    }
}
