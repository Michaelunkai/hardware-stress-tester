using System.IO;
using StressLab.Reporting;
using Xunit;

namespace StressLab.Tests;

public class MetricStatsTests
{
    [Fact]
    public void Stats_over_known_values()
    {
        var s = MetricStats.Of(new double?[] { 1, 2, 3, 4, 5 });
        Assert.Equal(5, s.N);
        Assert.Equal(1, s.Min);
        Assert.Equal(3, s.Avg);
        Assert.Equal(5, s.Max);
        Assert.Equal(3, s.Median);
        Assert.Equal(5, s.P95);
        Assert.True(s.Stdev > 1.4 && s.Stdev < 1.7);
    }

    [Fact]
    public void Nulls_and_nans_are_skipped()
    {
        var s = MetricStats.Of(new double?[] { null, double.NaN, 10, null });
        Assert.Equal(1, s.N);
        Assert.Equal(10, s.Avg);
    }

    [Fact]
    public void Empty_series_has_zero_count()
        => Assert.Equal(0, MetricStats.Of(Array.Empty<double?>()).N);
}

public class AnalyzerTests
{
    [Fact]
    public void Stable_throughput_has_no_anomaly()
    {
        var rates = Enumerable.Repeat(100.0, 30).ToArray();
        Assert.Null(Analyzer.ThroughputAnomaly("CPU", rates, "GFLOPS"));
    }

    [Fact]
    public void Sustained_decline_flags_throttling()
    {
        var rates = Enumerable.Range(0, 40).Select(i => 100.0 - i * 1.5).ToArray();
        var a = Analyzer.ThroughputAnomaly("CPU", rates, "GFLOPS");
        Assert.NotNull(a);
        Assert.Equal("WARN", a!.Severity);
        Assert.Contains("decline", a.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Jittery_throughput_flags_instability()
    {
        var rates = Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? 120.0 : 60.0).ToArray();
        var a = Analyzer.ThroughputAnomaly("CPU", rates, "GFLOPS");
        Assert.NotNull(a);
        Assert.Equal("WARN", a!.Severity);
    }

    [Fact]
    public void Thermal_limit_breach_is_a_fail()
    {
        var a = Analyzer.ThermalMargin("CPU", 91, limitC: 90);
        Assert.NotNull(a);
        Assert.Equal("FAIL", a!.Severity);
    }

    [Fact]
    public void Tight_thermal_margin_warns()
    {
        var a = Analyzer.ThermalMargin("CPU", 87, limitC: 90);
        Assert.NotNull(a);
        Assert.Equal("WARN", a!.Severity);
    }

    [Fact]
    public void Comfortable_thermal_margin_is_silent()
        => Assert.Null(Analyzer.ThermalMargin("CPU", 70, limitC: 90));

    [Fact]
    public void Missing_temperature_sensor_warns()
    {
        var a = Analyzer.SensorCoverage("CPU temperature", presentCount: 3, totalCount: 20);
        Assert.NotNull(a);
        Assert.Equal("WARN", a!.Severity);
    }

    [Fact]
    public void Full_sensor_coverage_is_silent()
        => Assert.Null(Analyzer.SensorCoverage("CPU temperature", presentCount: 20, totalCount: 20));

    [Fact]
    public void Ram_errors_are_a_fail()
    {
        var a = Analyzer.RamErrors(7, new[] { "7 memory verification errors detected so far" });
        Assert.NotNull(a);
        Assert.Equal("FAIL", a!.Severity);
        Assert.Null(Analyzer.RamErrors(0, Array.Empty<string>()));
    }

    [Fact]
    public void Badge_reflects_the_worst_finding()
    {
        Assert.Equal("✅ ALL CLEAR", Analyzer.Badge(Array.Empty<Anomaly>()));
        Assert.Equal("⚠️ PASSED WITH WARNINGS",
            Analyzer.Badge(new[] { new Anomaly("WARN", "t", "d") }));
        Assert.Equal("❌ PROBLEMS FOUND",
            Analyzer.Badge(new[] { new Anomaly("WARN", "t", "d"), new Anomaly("FAIL", "t2", "d2") }));
    }

    [Fact]
    public void Detect_all_pulls_ram_errors_and_driver_resets_from_events()
    {
        var t = new RunTelemetry
        {
            CpuEnabled = true, GpuEnabled = true, RamEnabled = true,
            CpuLimit = 90, GpuLimit = 85
        };
        t.Begin();
        t.AddSample(new TelemetrySample(
            DateTime.Now, 100, 70, 80, 4700, 99, 60, 0.4f, 31,
            2e9, 1400, 6900, 9000, 500, 3, "Running", "Running", "Running"));
        t.AddEvent("FAIL", "3 memory verification errors detected so far (first corrupt addresses are recorded by the engine)");
        t.AddEvent("FAIL", "GPU engine error: GPU driver reset detected (device removed / TDR). The stress outran this driver.");

        var findings = Analyzer.DetectAll(t);

        Assert.Contains(findings, f => f.Severity == "FAIL" && f.Title.Contains("RAM verification errors"));
        Assert.Contains(findings, f => f.Severity == "FAIL" && f.Title.Contains("GPU driver reset"));
        Assert.Equal("❌ PROBLEMS FOUND", Analyzer.Badge(findings));
    }

    [Fact]
    public void Clean_run_detects_nothing()
    {
        var t = new RunTelemetry
        {
            CpuEnabled = true, GpuEnabled = true, RamEnabled = true,
            CpuLimit = 90, GpuLimit = 85
        };
        t.Begin();
        for (int i = 0; i < 20; i++)
        {
            t.AddSample(new TelemetrySample(
                DateTime.Now.AddSeconds(i), 100, 70, 80, 4700, 99, 60, 0.4f, 31,
                2e9, 1400, 6900, 9000, 500, 0, "Running", "Running", "Running"));
        }

        var findings = Analyzer.DetectAll(t);

        Assert.Empty(findings);
        Assert.Equal("✅ ALL CLEAR", Analyzer.Badge(findings));
    }
}

public class DetailedReportTests
{
    private static RunTelemetry SampleTelemetry()
    {
        var t = new RunTelemetry
        {
            CpuName = "Test CPU", GpuName = "Test GPU", RamTotalGb = 32,
            LogicalCores = 16, Elevated = true, OsVersion = "Microsoft Windows 11", AppVersion = "1.1.0",
            CpuEnabled = true, GpuEnabled = true, RamEnabled = true,
            CpuKernel = "Floating-point FMA (GFLOPS)", CpuThreads = 16, CpuLoad = 100, GpuLoad = 100,
            RamTargetGb = 4, RamVerify = true, CpuLimit = 90, GpuLimit = 85
        };
        t.Begin();
        for (int i = 0; i < 10; i++)
        {
            t.AddSample(new TelemetrySample(
                DateTime.Now.AddSeconds(i), 100, 70 + i * 0.5f, 80, 4700, 99, 60 + i * 0.5f, 0.4f, 31,
                2e9, 1400, 6900, 9000, 500, 0, "Running", "Running", "Running"));
        }
        t.AddEvent("INFO", "Stress run started — CPU + GPU + RAM — duration unlimited");
        t.EndedAt = DateTime.Now.AddSeconds(10);
        return t;
    }

    [Fact]
    public void Markdown_contains_verdict_system_health_events_and_samples()
    {
        string md = DetailedReport.BuildMarkdown(SampleTelemetry());

        Assert.Contains("ALL CLEAR", md);
        Assert.Contains("Test CPU", md);
        Assert.Contains("Test GPU", md);
        Assert.Contains("## 1. System", md);
        Assert.Contains("## 3. Health checks", md);
        Assert.Contains("## 4. Findings", md);
        Assert.Contains("## 6. Event timeline", md);
        Assert.Contains("Stress run started", md);
        Assert.Contains("## 7. Sample-by-sample telemetry", md);
        Assert.Contains("verification errors across", md);
        Assert.Contains("Floating-point FMA", md);
    }

    [Fact]
    public void Failing_run_reports_problems_found()
    {
        var t = SampleTelemetry();
        t.AddSample(new TelemetrySample(
            DateTime.Now.AddSeconds(11), 100, 75, 82, 4700, 99, 66, 0.4f, 31,
            2e9, 1400, 6900, 9000, 500, 3, "Running", "Running", "Running"));
        t.AddEvent("FAIL", "3 memory verification errors detected so far (first corrupt addresses are recorded by the engine)");

        string md = DetailedReport.BuildMarkdown(t);

        Assert.Contains("❌ PROBLEMS FOUND", md);
        Assert.Contains("RAM verification errors detected (3)", md);
    }

    [Fact]
    public void Csv_writes_header_plus_every_sample()
    {
        var t = SampleTelemetry();
        string path = Path.Combine(Path.GetTempPath(), $"sl-test-{Guid.NewGuid():N}.csv");
        try
        {
            DetailedReport.WriteCsv(t, path);
            var lines = File.ReadAllLines(path);
            Assert.Equal(11, lines.Length); // header + 10 samples
            Assert.StartsWith("timestamp,cpu_load_pct", lines[0]);
            Assert.Contains(",0,Running,Running,Running", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
