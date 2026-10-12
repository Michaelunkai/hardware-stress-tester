namespace StressLab.Engines;

public enum EngineState
{
    Idle,
    Running,
    Stopped,
    Error
}

public sealed class EngineStats
{
    public EngineState State { get; set; } = EngineState.Idle;
    public double WorkPerSecond { get; set; }
    /// <summary>Secondary rate where meaningful (RAM/DISK: verify bytes/s; others: 0).</summary>
    public double AuxWorkPerSecond { get; set; }
    /// <summary>Measured busy-ratio of the engine itself, 0-100 (GPU: duty cycle including
    /// fence waits; CPU: configured duty). Used when no sensor reports load.</summary>
    public double DutyPercent { get; set; }
    public long TotalWork { get; set; }
    public double ElapsedSeconds { get; set; }
    public string StatusLine { get; set; } = "Idle";
    public string? ErrorText { get; set; }
}

public interface IStressEngine : IDisposable
{
    string Name { get; }
    EngineStats Snapshot();
    void Start();

    /// <summary>Sets the stop flag and performs cleanup on a background thread.
    /// Never blocks the caller — safe to call from the UI thread.</summary>
    void RequestStop();

    /// <summary>Blocking stop — signals the flag, joins the workers, finalizes stats.
    /// Joins happen OUTSIDE the stats lock so snapshots never stall.</summary>
    void Stop();
}
