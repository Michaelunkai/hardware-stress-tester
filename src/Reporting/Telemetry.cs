namespace StressLab.Reporting;

/// <summary>One 1-second observation of the whole system plus every engine, during a run.</summary>
public sealed record TelemetrySample(
    DateTime Time,
    float? CpuLoad,
    float? CpuTemp,
    float? CpuPower,
    float? CpuClock,
    float? GpuLoad,
    float? GpuTemp,
    float? GpuVramGb,
    float? RamUsedPct,
    float? FansRpm,
    double CpuRate,
    double GpuDrawsPerSecond,
    double GpuGflops,
    double RamWriteMbps,
    double RamVerifyMbps,
    double DiskWriteMbps,
    double DiskReadMbps,
    long DiskErrors,
    long RamErrors,
    string CpuState,
    string GpuState,
    string RamState,
    string DiskState);

/// <summary>A timestamped notable occurrence. Severity: INFO, WARN or FAIL.</summary>
public sealed record TelemetryEvent(DateTime Time, string Severity, string Message);

/// <summary>
/// Full in-memory record of one stress run: configuration snapshot, 1 Hz samples of every
/// metric, and a timestamped event log. Thread-safe; the report writers read copies.
/// </summary>
public sealed class RunTelemetry
{
    private readonly object _lock = new();
    private readonly List<TelemetrySample> _samples = new();
    private readonly List<TelemetryEvent> _events = new();

    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }

    public string CpuName { get; set; } = "";
    public string GpuName { get; set; } = "";
    public double RamTotalGb { get; set; }
    public int LogicalCores { get; set; }
    public bool Elevated { get; set; }
    public string OsVersion { get; set; } = "";
    public string AppVersion { get; set; } = "";

    public bool CpuEnabled { get; set; }
    public bool GpuEnabled { get; set; }
    public bool RamEnabled { get; set; }
    public bool DiskEnabled { get; set; }
    public bool ForceMax { get; set; }
    public string CpuKernel { get; set; } = "";
    public int CpuThreads { get; set; }
    public int CpuLoad { get; set; }
    public int GpuLoad { get; set; }
    public double RamTargetGb { get; set; }
    public bool RamVerify { get; set; }
    public double DiskTargetGb { get; set; }
    public int DiskLoad { get; set; }
    public float CpuLimit { get; set; }
    public float GpuLimit { get; set; }

    public void Begin()
    {
        lock (_lock)
        {
            _samples.Clear();
            _events.Clear();
            StartedAt = DateTime.Now;
            EndedAt = default;
        }
    }

    public void AddSample(TelemetrySample sample)
    {
        lock (_lock) _samples.Add(sample);
    }

    public void AddEvent(string severity, string message)
    {
        lock (_lock) _events.Add(new TelemetryEvent(DateTime.Now, severity, message));
    }

    public List<TelemetrySample> Samples
    {
        get { lock (_lock) return new List<TelemetrySample>(_samples); }
    }

    public List<TelemetryEvent> Events
    {
        get { lock (_lock) return new List<TelemetryEvent>(_events); }
    }

    public double DurationSeconds
        => EndedAt > StartedAt ? (EndedAt - StartedAt).TotalSeconds : 0;
}
