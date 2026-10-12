using System.IO;
using System.Windows;
using StressLab.Monitoring;

namespace StressLab.Smoke;

/// <summary>
/// Headless verification pass: `StressLab.exe --smoke` opens the real window, exercises
/// every engine at small scale, captures a screenshot of a live run, writes a
/// smoke-result.txt verdict file and exits with code 0 (pass) or 2 (hard failure).
/// The runner itself is wrapped so it can never die without emitting a verdict.
/// </summary>
public static class SmokeRunner
{
    private delegate void CheckFn(string name, bool pass, string detail, bool soft = false);

    public static async Task RunAsync(MainWindow window)
    {
        var results = new List<string>();
        bool hardFail = false;

        void Check(string name, bool pass, string detail, bool soft = false)
        {
            if (!pass && !soft) hardFail = true;
            string verdict = pass ? "PASS" : soft ? "SOFT" : "FAIL";
            results.Add($"{verdict}: {name} — {detail}");
        }

        try
        {
            await RunChecksAsync(Check);
        }
        catch (Exception ex)
        {
            Check("Engine checks", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        results.Insert(0, $"StressLab smoke run — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        string baseDir = AppContext.BaseDirectory;
        string resultFile = Path.Combine(baseDir, "smoke-result.txt");

        string screenshotPath = Path.Combine(baseDir, "screenshot.png");
        try
        {
            await window.DemoRunForScreenshotAsync(screenshotPath);
            results.Add("PASS: live demo run + screenshot captured");
        }
        catch (Exception ex)
        {
            results.Add($"SOFT: live demo run — {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            await File.WriteAllLinesAsync(resultFile, results);
        }
        catch (Exception ex)
        {
            results.Add($"FAIL: could not write {resultFile} — {ex.Message}");
        }

        await window.Dispatcher.InvokeAsync(() =>
        {
            foreach (var line in results) window.Log(line);
            Application.Current.Shutdown(hardFail ? 2 : 0);
        });
    }

    private static async Task RunChecksAsync(CheckFn check)
    {
        await Task.Run(async () =>
        {
            var sys = SystemProbe.Probe();
            check("System info",
                !string.IsNullOrWhiteSpace(sys.CpuName) && sys.RamTotalGb > 0,
                $"{sys.CpuName}, {sys.LogicalCores} threads, {sys.RamTotalGb:0.#} GB RAM",
                soft: false);

            try
            {
                using var monitor = new HardwareMonitor();
                monitor.Open();
                var m = monitor.Read();
                check("Hardware monitor", true,
                    $"opened (admin={HardwareMonitor.IsAdmin()}, " +
                    $"cpuTemp={(m.CpuTempC?.ToString("0") ?? "n/a")}, gpuTemp={(m.GpuTempC?.ToString("0") ?? "n/a")})",
                    soft: false);
            }
            catch (Exception ex)
            {
                check("Hardware monitor", false, ex.Message, soft: true);
            }

            var cpu = new Engines.CpuStressor
            {
                Kernel = Engines.CpuStressor.KernelKind.FmaFlops,
                Threads = 2,
                LoadPercent = 100
            };
            cpu.Start();
            await Task.Delay(700);
            cpu.Stop();
            var cs = cpu.Snapshot();
            check("CPU FMA kernel", cs.TotalWork > 0 && cs.WorkPerSecond > 0,
                $"{cs.TotalWork:N0} flops · {cs.WorkPerSecond / 1e9:0.##} GFLOPS", soft: false);

            cpu = new Engines.CpuStressor
            {
                Kernel = Engines.CpuStressor.KernelKind.IntegerMix,
                Threads = 2,
                LoadPercent = 100
            };
            cpu.Start();
            await Task.Delay(400);
            cpu.Stop();
            cs = cpu.Snapshot();
            check("CPU integer kernel", cs.TotalWork > 0,
                $"{cs.TotalWork:N0} ops · {cs.WorkPerSecond / 1e6:0.#} M/s", soft: false);

            var ram = new Engines.RamTester { TargetBytes = 128L << 20, VerifyEnabled = true };
            ram.Start();
            await Task.Delay(900);
            ram.Stop();
            var rs = ram.Snapshot();
            check("RAM write+verify", rs.TotalWork > 0 && ram.ErrorCount == 0,
                $"{rs.TotalWork / (1024 * 1024):N0} MB exercised · {rs.WorkPerSecond / (1024 * 1024):0} MB/s · {ram.ErrorCount} errors",
                soft: false);

            try
            {
                var gpu = new Engines.GpuStressor { LoadPercent = 100 };
                gpu.Start();
                // Device + shader compile can take a few seconds on first init —
                // wait until draws actually flow (or 6s) before measuring.
                int waited = 0;
                while (gpu.Snapshot().TotalWork == 0 && waited < 6000)
                {
                    await Task.Delay(250);
                    waited += 250;
                }
                await Task.Delay(600);
                gpu.Stop();
                var gs = gpu.Snapshot();
                bool clean = gs.ErrorText is null;
                check("GPU D3D11 shader stress", gs.TotalWork > 0 && clean,
                    gs.TotalWork == 0
                        ? $"no draws — {gs.ErrorText ?? gs.StatusLine}"
                        : clean
                            ? $"{gs.TotalWork:N0} draws · {Engines.GpuStressor.EstimateGflops(gs.TotalWork, gs.ElapsedSeconds):0} GFLOPS est."
                            : $"{gs.TotalWork:N0} draws before the driver reset — {gs.ErrorText}",
                    soft: true);
            }
            catch (Exception ex)
            {
                check("GPU D3D11 shader stress", false, ex.Message, soft: true);
            }
        });
    }
}
