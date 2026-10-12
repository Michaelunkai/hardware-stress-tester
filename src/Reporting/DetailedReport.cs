using System.Globalization;
using System.IO;
using System.Text;

namespace StressLab.Reporting;

/// <summary>
/// Writes the post-run deliverables: an exhaustive Markdown report (verdict, health
/// checks, per-metric statistics, full event timeline, sample table) and a CSV with
/// every recorded sample at full resolution.
/// </summary>
public static class DetailedReport
{
    public static string BuildMarkdown(RunTelemetry t)
    {
        var samples = t.Samples;
        var events = t.Events;
        var findings = Analyzer.DetectAll(t);
        var sb = new StringBuilder();

        sb.AppendLine("# 🧯 StressLab — Detailed Stress Report");
        sb.AppendLine();
        sb.AppendLine($"## Verdict: {Analyzer.Badge(findings)}");
        sb.AppendLine();
        sb.AppendLine($"- **Run:** {t.StartedAt:yyyy-MM-dd HH:mm:ss} → {(t.EndedAt > t.StartedAt ? t.EndedAt.ToString("HH:mm:ss") : "—")} " +
                      $"({TimeSpan.FromSeconds(t.DurationSeconds):hh\\:mm\\:ss})");
        sb.AppendLine($"- **Samples recorded:** {samples.Count} (1 Hz) · **Events logged:** {events.Count}");
        var parts = new[] { t.CpuEnabled ? "CPU" : null, t.GpuEnabled ? "GPU" : null, t.RamEnabled ? "RAM" : null }
            .Where(p => p is not null);
        sb.AppendLine($"- **Components:** {string.Join(" + ", parts)}");
        sb.AppendLine();

        // ---------------------------------------------------------------- system
        sb.AppendLine("## 1. System");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| CPU | {t.CpuName} ({t.LogicalCores} logical cores) |");
        sb.AppendLine($"| GPU | {t.GpuName} |");
        sb.AppendLine($"| RAM | {t.RamTotalGb:0.#} GB |");
        sb.AppendLine($"| OS | {t.OsVersion} |");
        sb.AppendLine($"| Elevation | {(t.Elevated ? "Administrator — full sensor access" : "Standard user — temperature sensors may be unavailable")} |");
        sb.AppendLine($"| StressLab | v{t.AppVersion} |");
        sb.AppendLine();

        // ---------------------------------------------------------------- config
        sb.AppendLine("## 2. Configuration");
        sb.AppendLine();
        sb.AppendLine("| Engine | Setting |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| CPU | {(t.CpuEnabled ? $"enabled — {t.CpuKernel}, {t.CpuThreads} threads, {t.CpuLoad}% duty cycle" : "off")} |");
        sb.AppendLine($"| GPU | {(t.GpuEnabled ? $"enabled — {t.GpuLoad}% duty cycle, offscreen 1024×1024 shader burn-in" : "off")} |");
        sb.AppendLine($"| RAM | {(t.RamEnabled ? $"enabled — {t.RamTargetGb:0.#} GB target, verification {(t.RamVerify ? "ON" : "OFF")}" : "off")} |");
        sb.AppendLine($"| DISK | {(t.DiskEnabled ? $"enabled — {t.DiskTargetGb:0.#} GB scratch, {t.DiskLoad}% duty cycle, checksum verification ON" : "off")} |");
        sb.AppendLine($"| FORCE MAX | {(t.ForceMax ? "ON — fans pinned to 100%, aggressive boost power plan" : "off")} |");
        sb.AppendLine($"| Watchdog | CPU limit {t.CpuLimit:0}°C · GPU limit {t.GpuLimit:0}°C |");
        sb.AppendLine();

        // ---------------------------------------------------------------- health checks
        sb.AppendLine("## 3. Health checks");
        sb.AppendLine();
        sb.AppendLine("| Check | Result |");
        sb.AppendLine("|---|---|");
        foreach (var f in findings)
            sb.AppendLine($"| {f.Title} | {f.Severity} |");
        if (findings.Count == 0)
            sb.AppendLine("| Throughput stability | OK |");
        if (t.CpuEnabled)
            sb.AppendLine($"| Thermal watchdog armed (CPU) | {(t.Elevated ? "yes" : "temperature sensor unavailable — limited")} |");
        sb.AppendLine();

        // ---------------------------------------------------------------- findings
        sb.AppendLine("## 4. Findings");
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine("**None.** Every detector came back clean: throughput was stable, thermal margins " +
                          "were comfortable, no memory errors, no driver resets, sensors were available.");
        }
        else
        {
            foreach (var f in findings)
            {
                sb.AppendLine($"### [{f.Severity}] {f.Title}");
                sb.AppendLine();
                sb.AppendLine(f.Detail);
                sb.AppendLine();
            }
        }

        // ---------------------------------------------------------------- deep dive
        sb.AppendLine("## 5. Engine deep dive");
        sb.AppendLine();
        AppendEngine(sb, "CPU", t.CpuEnabled, samples,
            ("Load", s => s.CpuLoad, "%", "0"),
            ("Temperature", s => s.CpuTemp, "°C", "0"),
            ("Package power", s => s.CpuPower, "W", "0.0"),
            ("Max core clock", s => s.CpuClock, "MHz", "0"),
            ("Throughput", s => s.CpuRate, t.CpuKernel.Contains("FMA", StringComparison.OrdinalIgnoreCase) ? "GFLOPS (FP64)" : "G work-units/s", "0.00"));
        AppendEngine(sb, "GPU", t.GpuEnabled, samples,
            ("Load", s => s.GpuLoad, "%", "0"),
            ("Temperature", s => s.GpuTemp, "°C", "0"),
            ("VRAM used", s => s.GpuVramGb, "GB", "0.0"),
            ("Executed draws", s => s.GpuDrawsPerSecond, "draws/s", "0"),
            ("Est. compute", s => s.GpuGflops, "GFLOPS (est.)", "0"));
        AppendEngine(sb, "RAM", t.RamEnabled, samples,
            ("System RAM in use", s => s.RamUsedPct, "%", "0"),
            ("Write bandwidth", s => s.RamWriteMbps, "MB/s", "0"),
            ("Verify bandwidth", s => s.RamVerifyMbps, "MB/s", "0"));
        AppendEngine(sb, "DISK", t.DiskEnabled, samples,
            ("Write bandwidth", s => s.DiskWriteMbps, "MB/s", "0"),
            ("Read bandwidth", s => s.DiskReadMbps, "MB/s", "0"),
            ("Fan speed (max)", s => s.FansRpm, "RPM", "0"));

        long finalErrors = samples.Count > 0 ? samples[^1].RamErrors : 0;
        sb.AppendLine($"- **RAM integrity:** {(t.RamEnabled ? (t.RamVerify ? $"{finalErrors:N0} verification errors across {samples.Count:N0} checked seconds — {(finalErrors == 0 ? "✅ memory is stable" : "❌ memory is NOT stable")}" : "verification disabled for this run") : "RAM engine off")}");
        long finalDiskErrors = samples.Count > 0 ? samples[^1].DiskErrors : 0;
        if (t.DiskEnabled)
            sb.AppendLine($"- **DISK integrity:** {finalDiskErrors:N0} checksum errors — {(finalDiskErrors == 0 ? "✅ storage verified clean" : "❌ storage is NOT reliable")}");
        if (t.ForceMax)
            sb.AppendLine("- **FORCE MAX:** fans were pinned to 100% and the power plan ran in aggressive-boost mode for this run (restored afterwards).");
        sb.AppendLine();

        // ---------------------------------------------------------------- events
        sb.AppendLine("## 6. Event timeline");
        sb.AppendLine();
        if (events.Count == 0)
        {
            sb.AppendLine("*No notable events — a quiet, healthy run.*");
        }
        else
        {
            foreach (var e in events)
                sb.AppendLine($"- `{e.Time:HH:mm:ss}` **{e.Severity}** — {e.Message}");
        }
        sb.AppendLine();

        // ---------------------------------------------------------------- samples
        sb.AppendLine("## 7. Sample-by-sample telemetry");
        sb.AppendLine();
        if (samples.Count == 0)
        {
            sb.AppendLine("*No samples recorded.*");
        }
        else
        {
            sb.AppendLine("Time | CPU % | CPU °C | CPU W | GPU % | GPU °C | VRAM GB | RAM % | FANS RPM | CPU rate | GPU draws/s | GPU GFLOPS | RAM W/V MB/s | DISK W/R MB/s | Errors");
            sb.AppendLine("---|---|---|---|---|---|---|---|---|---|---|---|---|---|---");
            int stride = Math.Max(1, (int)Math.Ceiling(samples.Count / 120.0));
            for (int i = 0; i < samples.Count; i += stride)
            {
                var s = samples[i];
                sb.AppendLine($"{s.Time:HH:mm:ss} | {F(s.CpuLoad)} | {F(s.CpuTemp)} | {F(s.CpuPower)} | {F(s.GpuLoad)} | {F(s.GpuTemp)} | {F(s.GpuVramGb, "0.0")} | {F(s.RamUsedPct)} | {F(s.FansRpm)} | {s.CpuRate:0} | {s.GpuDrawsPerSecond:0} | {s.GpuGflops:0} | {s.RamWriteMbps:0}/{s.RamVerifyMbps:0} | {s.DiskWriteMbps:0}/{s.DiskReadMbps:0} | {s.RamErrors + s.DiskErrors}");
            }
            if (stride > 1)
                sb.AppendLine();
            sb.AppendLine($"*Showing every {stride}. sample of {samples.Count}. Full-resolution series in the companion CSV.*");
        }
        sb.AppendLine();

        // ---------------------------------------------------------------- guide
        sb.AppendLine("## 8. How to read this report");
        sb.AppendLine();
        sb.AppendLine("- **Throughput stability** compares the middle 80% of samples (warmup and shutdown trimmed). " +
                      "A sustained decline with high temperatures means thermal throttling — improve cooling.");
        sb.AppendLine("- **Thermal margin** is the closest any temperature came to its limit. Under 5°C is tight for long runs.");
        sb.AppendLine("- **RAM integrity** is absolute: any verification error means unstable memory (or an unstable memory overclock).");
        sb.AppendLine("- **GPU GFLOPS** are estimates (draws/s × a conservative per-shader cost); draws/s itself is measured by " +
                      "execution-fencing, so it reflects real work.");
        sb.AppendLine("- **Sensor coverage** below 50% means the watchdog was partially blind — run as administrator.");

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*Generated by StressLab v{t.AppVersion} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}*");
        return sb.ToString();
    }

    private static void AppendEngine(StringBuilder sb, string name, bool enabled,
        List<TelemetrySample> samples, params (string Label, Func<TelemetrySample, double?> Get, string Unit, string Fmt)[] metrics)
    {
        sb.AppendLine($"### {name} — {(enabled ? "ran in this test" : "not enabled")}");
        sb.AppendLine();
        if (!enabled || samples.Count == 0)
        {
            sb.AppendLine();
            return;
        }
        sb.AppendLine("| Metric | min · avg · median · p95 · max |");
        sb.AppendLine("|---|---|");
        foreach (var (label, get, unit, fmt) in metrics)
        {
            var stats = MetricStats.Of(samples.Select(get));
            sb.AppendLine($"| {label} | {stats.Format(unit, fmt)} |");
        }
        sb.AppendLine();
    }

    private static string F(float? v, string fmt = "0")
        => v is null ? "—" : v.Value.ToString(fmt, CultureInfo.InvariantCulture);

    public static void WriteCsv(RunTelemetry t, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("timestamp,cpu_load_pct,cpu_temp_c,cpu_power_w,cpu_clock_mhz,gpu_load_pct,gpu_temp_c,gpu_vram_gb,ram_used_pct,fans_rpm,cpu_rate,gpu_draws_per_sec,gpu_gflops_est,ram_write_mbps,ram_verify_mbps,disk_write_mbps,disk_read_mbps,disk_errors,ram_errors,cpu_state,gpu_state,ram_state,disk_state");
        foreach (var s in t.Samples)
        {
            sb.AppendLine(string.Join(',',
                s.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                N(s.CpuLoad), N(s.CpuTemp), N(s.CpuPower, "0.0"), N(s.CpuClock),
                N(s.GpuLoad), N(s.GpuTemp), N(s.GpuVramGb, "0.00"), N(s.RamUsedPct), N(s.FansRpm),
                s.CpuRate.ToString("0", CultureInfo.InvariantCulture),
                s.GpuDrawsPerSecond.ToString("0", CultureInfo.InvariantCulture),
                s.GpuGflops.ToString("0", CultureInfo.InvariantCulture),
                s.RamWriteMbps.ToString("0", CultureInfo.InvariantCulture),
                s.RamVerifyMbps.ToString("0", CultureInfo.InvariantCulture),
                s.DiskWriteMbps.ToString("0", CultureInfo.InvariantCulture),
                s.DiskReadMbps.ToString("0", CultureInfo.InvariantCulture),
                s.DiskErrors.ToString(CultureInfo.InvariantCulture),
                s.RamErrors.ToString(CultureInfo.InvariantCulture),
                s.CpuState, s.GpuState, s.RamState, s.DiskState));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static string N(float? v, string fmt = "0")
        => v is null ? "" : v.Value.ToString(fmt, CultureInfo.InvariantCulture);
}
