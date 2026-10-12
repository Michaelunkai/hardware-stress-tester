namespace StressLab.Reporting;

/// <summary>Descriptive statistics over one metric series (nulls/NaN skipped).</summary>
public sealed class MetricStats
{
    public int N { get; private set; }
    public double Min { get; private set; }
    public double Avg { get; private set; }
    public double Max { get; private set; }
    public double Median { get; private set; }
    public double P95 { get; private set; }
    public double Stdev { get; private set; }

    public static MetricStats Of(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value))
                         .Select(v => v!.Value)
                         .OrderBy(v => v)
                         .ToList();
        var s = new MetricStats();
        if (list.Count == 0) return s;

        s.N = list.Count;
        s.Min = list[0];
        s.Max = list[^1];
        s.Avg = list.Average();
        s.Median = list.Count % 2 == 1
            ? list[list.Count / 2]
            : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2.0;
        s.P95 = list[Math.Min(list.Count - 1, (int)Math.Ceiling(list.Count * 0.95) - 1)];
        double variance = list.Sum(v => (v - s.Avg) * (v - s.Avg)) / list.Count;
        s.Stdev = Math.Sqrt(variance);
        return s;
    }

    public string Format(string unit, string format = "0.0")
        => N == 0
            ? "no data"
            : $"min {Min.ToString(format)} · avg {Avg.ToString(format)} · median {Median.ToString(format)} · p95 {P95.ToString(format)} · max {Max.ToString(format)} {unit} (n={N})";
}

/// <summary>One finding. Severity: FAIL (problem), WARN (worth knowing), INFO (context).</summary>
public sealed record Anomaly(string Severity, string Title, string Detail);

public static class Analyzer
{
    public const double UnstableCvLimit = 0.15;
    public const double ThrottleDeclineLimit = 0.08;
    public const double TightThermalMarginC = 5.0;

    /// <summary>Trims the first/last 10% (warmup + shutdown), then measures stability:
    /// coefficient of variation across the middle, plus first-vs-last decade decline.</summary>
    public static (double cv, double declineFrac, double[] middle) StabilityProfile(double[] rates)
    {
        if (rates.Length < 4) return (0, 0, rates);
        int trim = Math.Max(1, rates.Length / 10);
        var middle = rates[trim..^trim];
        if (middle.Length == 0) return (0, 0, rates);

        double avg = middle.Average();
        double cv = avg <= 0 ? 0 : Math.Sqrt(middle.Sum(v => (v - avg) * (v - avg)) / middle.Length) / avg;

        int decade = Math.Max(1, middle.Length / 10);
        double first = middle.Take(decade).Average();
        double last = middle.TakeLast(decade).Average();
        double decline = first <= 0 ? 0 : Math.Max(0, (first - last) / first);
        return (cv, decline, middle);
    }

    public static Anomaly? ThroughputAnomaly(string engine, double[] ratesPerSecond, string unit)
    {
        if (ratesPerSecond.Length < 8) return null;
        var (cv, decline, _) = StabilityProfile(ratesPerSecond);

        if (decline >= ThrottleDeclineLimit)
            return new Anomaly("WARN", $"{engine} sustained throughput decline",
                $"Throughput fell {decline * 100:0}% from the start to the end of the run " +
                $"(first decade vs last decade, {unit}). Combined with high temperature this " +
                "indicates thermal throttling; otherwise it can indicate background system load.");

        if (cv >= UnstableCvLimit)
            return new Anomaly("WARN", $"{engine} unstable throughput",
                $"Throughput varied by {cv * 100:0}% (coefficient of variation) across the run " +
                $"({unit}). Occasional dips are normal on a shared system; persistent jitter is not.");
        return null;
    }

    public static Anomaly? ThermalMargin(string component, double? minTemp, float limitC)
    {
        if (minTemp is null) return null;
        double margin = limitC - minTemp.Value;
        if (margin <= 0)
            return new Anomaly("FAIL", $"{component} exceeded its thermal limit",
                $"Minimum observed headroom: {margin:0.0}°C against the {limitC:0}°C limit. " +
                "The watchdog aborts the run the moment the limit itself is crossed.");
        if (margin < TightThermalMarginC)
            return new Anomaly("WARN", $"{component} thermal margin was tight",
                $"Temperature came within {margin:0.0}°C of the {limitC:0}°C limit. " +
                "Improve cooling or raise airflow before longer runs.");
        return null;
    }

    public static Anomaly? SensorCoverage(string component, int presentCount, int totalCount)
        => totalCount > 0 && presentCount < totalCount * 0.5
            ? new Anomaly("WARN", $"{component} temperature sensor was mostly unavailable",
                $"Sensor present in only {presentCount}/{totalCount} samples. Run as administrator " +
                "so the hardware driver can read temperature — the thermal watchdog is partially blind without it.")
            : null;    public static Anomaly? RamErrors(long errorCount, IEnumerable<string> errorEvents)
    {
        if (errorCount <= 0) return null;
        return new Anomaly("FAIL", $"RAM verification errors detected ({errorCount:N0})",
            "Every 64-bit word written is verified read-back; any mismatch means unstable memory. " +
            "Details: " + string.Join(" | ", errorEvents.Take(3)));
    }

    public static Anomaly? GpuDeviceReset(bool deviceLost, string? errorText)
        => deviceLost
            ? new Anomaly("FAIL", "GPU driver reset during the run",
                errorText ?? "The graphics driver stopped responding and was reset (TDR).")
            : null;

    public static string Badge(IEnumerable<Anomaly> anomalies)
    {
        var list = anomalies.ToList();
        if (list.Any(a => a.Severity == "FAIL")) return "❌ PROBLEMS FOUND";
        if (list.Any(a => a.Severity == "WARN")) return "⚠️ PASSED WITH WARNINGS";
        return "✅ ALL CLEAR";
    }

    /// <summary>Runs every detector over a finished run. Ordered FAIL → WARN → INFO.</summary>
    public static List<Anomaly> DetectAll(RunTelemetry t)
    {
        var findings = new List<Anomaly>();
        var samples = t.Samples;

        if (t.CpuEnabled && samples.Count > 0)
        {
            var a = ThroughputAnomaly("CPU", samples.Select(s => s.CpuRate).ToArray(), "work units/s");
            if (a is not null) findings.Add(a);
            var tm = ThermalMargin("CPU", MinOf(samples.Select(s => s.CpuTemp)), t.CpuLimit);
            if (tm is not null) findings.Add(tm);
        }
        if (t.GpuEnabled && samples.Count > 0)
        {
            var a = ThroughputAnomaly("GPU", samples.Select(s => s.GpuDrawsPerSecond).ToArray(), "draws/s");
            if (a is not null) findings.Add(a);
            var tm = ThermalMargin("GPU", MinOf(samples.Select(s => s.GpuTemp)), t.GpuLimit);
            if (tm is not null) findings.Add(tm);
        }

        int cpuTempPresent = samples.Count(s => s.CpuTemp.HasValue);
        findings.AddIfNotNull(SensorCoverage("CPU", cpuTempPresent, samples.Count));
        int gpuTempPresent = samples.Count(s => s.GpuTemp.HasValue);
        findings.AddIfNotNull(SensorCoverage("GPU", gpuTempPresent, samples.Count));

        var ramErrorEvents = t.Events.Where(e => e.Severity == "FAIL" && e.Message.Contains("memory verification errors", StringComparison.OrdinalIgnoreCase))
                                     .Select(e => e.Message);
        var ramA = RamErrors(samples.Count > 0 ? samples[^1].RamErrors : 0, ramErrorEvents);
        if (ramA is not null) findings.Add(ramA);

        if (t.DiskEnabled)
        {
            long diskErrors = samples.Count > 0 ? samples[^1].DiskErrors : 0;
            if (diskErrors > 0)
                findings.Add(new Anomaly("FAIL", $"DISK verification errors detected ({diskErrors:N0})",
                    "Read-back checksums did not match what was written. This indicates failing storage, " +
                    "an unstable storage controller/overclock, or (rarely) a cable problem — not a StressLab fault."));
        }

        var tdrEvent = t.Events.FirstOrDefault(e => e.Message.Contains("driver reset", StringComparison.OrdinalIgnoreCase));
        var gpuA = GpuDeviceReset(tdrEvent is not null, tdrEvent?.Message);
        if (gpuA is not null) findings.Add(gpuA);

        return findings
            .OrderByDescending(a => a.Severity switch { "FAIL" => 2, "WARN" => 1, _ => 0 })
            .ToList();
    }

    private static double? MinOf(IEnumerable<float?> values)
    {
        double? min = null;
        foreach (var v in values)
        {
            if (!v.HasValue || float.IsNaN(v.Value)) continue;
            if (min is null || v.Value < min) min = v.Value;
        }
        return min;
    }
}

internal static class AnomalyListExtensions
{
    public static void AddIfNotNull(this List<Anomaly> list, Anomaly? anomaly)
    {
        if (anomaly is not null) list.Add(anomaly);
    }
}
