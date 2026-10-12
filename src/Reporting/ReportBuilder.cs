using System.Text;

namespace StressLab.Reporting;

public sealed class MetricSummary
{
    private double _sum;
    private int _count;

    public string Unit { get; }
    public double Min { get; private set; } = double.MaxValue;
    public double Max { get; private set; } = double.MinValue;
    public bool HasData => _count > 0;
    public double Average => _count == 0 ? 0 : _sum / _count;

    public MetricSummary(string unit) => Unit = unit;

    public void Add(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;
        _sum += value;
        _count++;
        if (value < Min) Min = value;
        if (value > Max) Max = value;
    }

    public void Reset()
    {
        _sum = 0;
        _count = 0;
        Min = double.MaxValue;
        Max = double.MinValue;
    }

    public string Format()
        => HasData ? $"{Average:0.0} avg · {Min:0.0} – {Max:0.0} {Unit}" : "no data";
}

public sealed class SessionRecord
{
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public double DurationSeconds { get; set; }

    public string CpuName { get; set; } = "";
    public string GpuName { get; set; } = "";
    public double RamTotalGb { get; set; }

    public bool CpuEnabled { get; set; }
    public bool GpuEnabled { get; set; }
    public bool RamEnabled { get; set; }

    public string CpuKernel { get; set; } = "";
    public int CpuThreads { get; set; }
    public int CpuLoad { get; set; }
    public int GpuLoad { get; set; }
    public double RamTargetGb { get; set; }
    public bool RamVerify { get; set; }

    public MetricSummary CpuLoadMetric { get; } = new("%");
    public MetricSummary CpuTemp { get; } = new("°C");
    public MetricSummary CpuGflops { get; } = new("GFLOPS");
    public MetricSummary GpuLoadMetric { get; } = new("%");
    public MetricSummary GpuTemp { get; } = new("°C");
    public MetricSummary GpuGflops { get; } = new("GFLOPS");
    public MetricSummary RamThroughput { get; } = new("MB/s");
    public MetricSummary RamUsedMetric { get; } = new("%");

    public long RamErrors { get; set; }
    public long CpuTotalWork { get; set; }
    public long GpuTotalWork { get; set; }
    public long RamTotalWork { get; set; }
    public List<string> Events { get; } = new();

    public void Reset()
    {
        StartedAt = default;
        EndedAt = default;
        DurationSeconds = 0;
        RamErrors = 0;
        CpuTotalWork = 0;
        GpuTotalWork = 0;
        RamTotalWork = 0;
        CpuLoadMetric.Reset();
        CpuTemp.Reset();
        CpuGflops.Reset();
        GpuLoadMetric.Reset();
        GpuTemp.Reset();
        GpuGflops.Reset();
        RamThroughput.Reset();
        RamUsedMetric.Reset();
        Events.Clear();
    }
}

public static class ReportBuilder
{
    public static string BuildMarkdown(SessionRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 🧯 StressLab — Stress-Test Report");
        sb.AppendLine();
        sb.AppendLine($"**Started:** {r.StartedAt:yyyy-MM-dd HH:mm:ss}  ·  **Ended:** {r.EndedAt:HH:mm:ss}  ·  **Duration:** {TimeSpan.FromSeconds(r.DurationSeconds):hh\\:mm\\:ss}");
        sb.AppendLine();
        sb.AppendLine("## System");
        sb.AppendLine($"- **CPU:** {r.CpuName}");
        sb.AppendLine($"- **GPU:** {r.GpuName}");
        sb.AppendLine($"- **RAM:** {r.RamTotalGb:0.#} GB");
        sb.AppendLine();
        sb.AppendLine("## Configuration");
        sb.AppendLine($"- **CPU stress:** {(r.CpuEnabled ? $"ON — {r.CpuKernel}, {r.CpuThreads} threads, {r.CpuLoad}% load" : "off")}");
        sb.AppendLine($"- **GPU stress:** {(r.GpuEnabled ? $"ON — {r.GpuLoad}% load" : "off")}");
        sb.AppendLine($"- **RAM stress:** {(r.RamEnabled ? $"ON — {r.RamTargetGb:0.#} GB target, verify {(r.RamVerify ? "enabled" : "disabled")}" : "off")}");
        sb.AppendLine();
        sb.AppendLine("## Results");
        if (r.CpuEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("### CPU");
            sb.AppendLine($"- Load: {r.CpuLoadMetric.Format()}");
            sb.AppendLine($"- Temperature: {r.CpuTemp.Format()}");
            sb.AppendLine($"- Throughput: {r.CpuGflops.Format()}");
            sb.AppendLine($"- Work completed: {r.CpuTotalWork:N0}");
        }
        if (r.GpuEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("### GPU");
            sb.AppendLine($"- Load: {r.GpuLoadMetric.Format()}");
            sb.AppendLine($"- Temperature: {r.GpuTemp.Format()}");
            sb.AppendLine($"- Estimated throughput: {r.GpuGflops.Format()}");
            sb.AppendLine($"- Draws submitted: {r.GpuTotalWork:N0}");
        }
        if (r.RamEnabled)
        {
            sb.AppendLine();
            sb.AppendLine("### RAM");
            sb.AppendLine($"- Memory bandwidth: {r.RamThroughput.Format()}");
            sb.AppendLine($"- System RAM in use: {r.RamUsedMetric.Format()}");
            sb.AppendLine($"- Bytes exercised: {r.RamTotalWork:N0}");
            sb.AppendLine($"- **Errors: {r.RamErrors:N0}** {(r.RamErrors == 0 && r.RamVerify ? "✅ no memory errors detected" : "")}");
        }
        sb.AppendLine();
        sb.AppendLine("## Events");
        if (r.Events.Count == 0)
            sb.AppendLine("- None.");
        else
            foreach (var e in r.Events)
                sb.AppendLine($"- {e}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Generated by StressLab — Hardware Stress Tester.*");
        return sb.ToString();
    }
}
