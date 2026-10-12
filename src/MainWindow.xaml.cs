using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using StressLab.Engines;
using StressLab.Monitoring;
using StressLab.Reporting;

namespace StressLab;

public partial class MainWindow : Window
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS = 0x80000000u;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001u;

    private readonly CpuStressor _cpu = new();
    private readonly GpuStressor _gpu = new();
    private readonly RamTester _ram = new();
    private readonly DiskStressor _disk = new();
    private readonly SensorClient _monitor = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly SessionRecord _session = new();
    private readonly RunTelemetry _telemetry = new();
    private long _lastRamErrors;
    private long _lastDiskErrors;
    private bool _cpuThermalWarned;
    private bool _gpuThermalWarned;

    private MetricsSnapshot _lastSnapshot = new();
    private DateTime _runStartUtc;
    private TimeSpan _duration;
    private bool _running;
    private bool _probed;
    private string _gpuName = "GPU";
    private readonly HashSet<string> _loggedEngineErrors = new();

    private void LogEngineErrorOnce(string engine, EngineStats stats)
    {
        if (stats.State == EngineState.Error
            && !string.IsNullOrEmpty(stats.ErrorText)
            && _loggedEngineErrors.Add($"{engine}:{stats.ErrorText}"))
        {
            Log($"{engine} engine stopped with an error: {stats.ErrorText}");
            _telemetry.AddEvent("FAIL", $"{engine} engine error: {stats.ErrorText}");
        }
    }

    public MainWindow()
    {
        // The UI thread outranks every stress worker, so STOP and the whole window
        // stay responsive even while all logical cores are pegged at 100%.
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        InitializeComponent();
        Closed += (_, _) => EmergencyCleanup();
        _uiTimer.Tick += (_, _) => UiTick();
        _monitorTimer.Tick += (_, _) => MonitorTick();
        Loaded += OnLoaded;
    }

    // ---------------------------------------------------------------- startup

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // async void: nothing inside may ever escape and kill the process.
        try
        {
            await OnLoadedCoreAsync();
        }
        catch (Exception ex)
        {
            App.Breadcrumb($"startup failed: {ex.GetType().Name} — {ex.Message}");
            try
            {
                SystemLine.Text = "Hardware probe failed — engines still available.";
                Log($"Startup warning: {ex.Message}");
            }
            catch { /* UI not ready — nothing more we can do */ }
        }
    }

    private async Task OnLoadedCoreAsync()
    {
        foreach (var spark in new[] { CpuSpark, GpuSpark, RamSpark, DiskSpark }) spark.Reset();
        CpuSpark.SetColor(Color.FromRgb(0x4D, 0xA3, 0xFF));
        GpuSpark.SetColor(Color.FromRgb(0x3D, 0xDC, 0x97));
        RamSpark.SetColor(Color.FromRgb(0xB5, 0x7B, 0xFF));
        DiskSpark.SetColor(Color.FromRgb(0xFF, 0x9F, 0x43));

        bool sensorUp = false;
        await Task.Run(() =>
        {
            try
            {
                sensorUp = _monitor.Start();
                _gpuName = _monitor.GpuName.Length > 0 ? _monitor.GpuName : GpuStressor.TryGetAdapterName();
                int controls = _monitor.CountFanControls();
                LogSafe(controls > 0
                    ? $"Hardware ready — {controls} controllable fan(s) detected (FORCE MAX can pin them to 100%)."
                    : "Hardware ready — no fan PWM controls found (FORCE MAX will still boost the power plan).");
                if (!sensorUp)
                    LogSafe("Sensor host unavailable — temperatures and fan pinning are disabled; all engines still work.");
            }
            catch (Exception ex)
            {
                _gpuName = GpuStressor.TryGetAdapterName();
                LogSafe($"Sensor monitor limited: {ex.Message}");
            }
        });

        _lastSnapshot = ReadMonitorSafe() ?? _lastSnapshot;
        _probed = true;

        var sys = SystemProbe.Probe(_gpuName);
        SystemLine.Text = $"{sys.CpuName}  ·  {sys.LogicalCores} threads  ·  {sys.RamTotalGb:0.#} GB RAM  ·  {sys.GpuName}";

        CpuThreadsSlider.Maximum = sys.LogicalCores;
        CpuThreadsSlider.Value = sys.LogicalCores;
        double ramMax = Math.Max(1, Math.Floor(sys.RamTotalGb * 0.9));
        RamGbSlider.Maximum = ramMax;
        RamGbSlider.Value = Math.Clamp(Math.Floor(sys.RamTotalGb * 0.5), 1, ramMax);

        UpdateAdminBadge();
        _monitorTimer.Start();
        UiTick();
        Log("Ready. Choose components (or a preset), set a duration, then press START TEST.");

        if (App.IsSmoke)
        {
            App.Breadcrumb("smoke path entered — launching SmokeRunner");
            _ = RunSmokeGuardedAsync();
        }
        else
        {
            App.Breadcrumb("normal startup complete");
        }
    }

    private async Task RunSmokeGuardedAsync()
    {
        try
        {
            await Smoke.SmokeRunner.RunAsync(this);
        }
        catch (Exception ex)
        {
            App.Breadcrumb($"smoke crashed: {ex}");
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"),
                    $"FAIL: smoke runner crashed — {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
            await Dispatcher.InvokeAsync(() => Application.Current.Shutdown(2));
        }
    }

    private MetricsSnapshot? ReadMonitorSafe()
    {
        try
        {
            return _monitor.Read();
        }
        catch
        {
            return null;
        }
    }

    private void UpdateAdminBadge()
    {
        if (HardwareMonitor.IsAdmin())
        {
            AdminBadgeText.Text = "Administrator";
        }
        else
        {
            AdminBadgeText.Text = "Standard user — run as admin for temps";
        }
    }

    // ---------------------------------------------------------------- run control

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_running) StopRun("stopped by user");
        else StartRun();
    }

    private void ForceMax_Click(object sender, RoutedEventArgs e)
    {
        if (ForceMaxButton.IsChecked == true)
        {
            int fans = 0;
            try { fans = _monitor.SetAllFanControls(100f); }
            catch (Exception ex) { Log($"Fan control failed: {ex.Message}"); }

            var (powerOk, powerNote) = (false, "");
            try { (powerOk, powerNote) = PowerBooster.Engage(); }
            catch (Exception ex) { Log($"Power boost failed: {ex.Message}"); }

            _telemetry.ForceMax = true;
            _telemetry.AddEvent("WARN", $"FORCE MAX engaged — {fans} fan control(s) pinned to 100%, power plan: {(powerOk ? "aggressive boost" : "unavailable")}");
            Log($"⚡ FORCE MAX engaged — {fans} fan control(s) pinned to 100%{(fans == 0 ? " (none found: run as administrator, or this board exposes no PWM controls)" : "")} · power plan: {(powerOk ? $"boosted — {powerNote}" : powerNote)}.");
        }
        else
        {
            DisengageForceMax();
        }
    }

    private void DisengageForceMax()
    {
        if (ForceMaxButton is null || (!ForceMaxButton.IsChecked.HasValue && !_telemetry.ForceMax)) return;
        int fans = 0;
        try { fans = _monitor.RestoreAllFanControls(); } catch { }
        try { PowerBooster.Restore(); } catch { }
        if (_telemetry.ForceMax)
        {
            _telemetry.ForceMax = false;
            _telemetry.AddEvent("INFO", $"FORCE MAX disengaged — {fans} fan control(s) back to automatic, previous power plan restored");
            Log("FORCE MAX off — fans back to automatic, previous power plan restored.");
        }
    }

    private void StartRun()
    {
        bool cpu = CpuEnabled.IsChecked == true;
        bool gpu = GpuEnabled.IsChecked == true;
        bool ram = RamEnabled.IsChecked == true;
        bool disk = DiskEnabled.IsChecked == true;
        if (!cpu && !gpu && !ram && !disk)
        {
            Log("Select at least one component (or a preset) before starting.");
            return;
        }

        _duration = SelectedDuration();
        _runStartUtc = DateTime.UtcNow;
        _session.Reset();
        _session.StartedAt = DateTime.Now;
        _session.CpuEnabled = cpu;
        _session.GpuEnabled = gpu;
        _session.RamEnabled = ram;
        _session.CpuName = _monitor.CpuName.Length > 0 ? _monitor.CpuName : SystemProbe.GetCpuName();
        _session.GpuName = _gpuName;
        var (totalBytes, _) = SystemProbe.GetMemory();
        _session.RamTotalGb = totalBytes / 1073741824.0;

        ThermalBanner.Visibility = Visibility.Collapsed;
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); // never sleep mid-test

        if (cpu)
        {
            _cpu.Threads = (int)CpuThreadsSlider.Value;
            _cpu.LoadPercent = (int)CpuLoadSlider.Value;
            _cpu.Kernel = (CpuStressor.KernelKind)Math.Max(0, CpuKernelBox.SelectedIndex);
            _session.CpuKernel = CpuKernelBox.Text;
            _session.CpuThreads = _cpu.Threads;
            _session.CpuLoad = _cpu.LoadPercent;
            _cpu.Start();
        }
        if (gpu)
        {
            _gpu.LoadPercent = (int)GpuLoadSlider.Value;
            _session.GpuLoad = _gpu.LoadPercent;
            _gpu.Start();
        }
        if (ram)
        {
            var (total, avail) = SystemProbe.GetMemory();
            _ram.TargetBytes = RamTester.ComputeTargetBytes((long)(RamGbSlider.Value * (1L << 30)), total, avail);
            _ram.VerifyEnabled = RamVerifyBox.IsChecked == true;
            _session.RamTargetGb = _ram.TargetBytes / 1073741824.0;
            _session.RamVerify = _ram.VerifyEnabled;
            _ram.Start();
        }
        if (disk)
        {
            _disk.TargetBytes = DiskStressor.ComputeTargetBytes((long)(DiskGbSlider.Value * (1L << 30)));
            _disk.LoadPercent = (int)DiskLoadSlider.Value;
            _disk.VerifyEnabled = DiskVerifyBox.IsChecked == true;
            _disk.Start();
        }

        CpuSpark.Reset();
        GpuSpark.Reset();
        RamSpark.Reset();
        DiskSpark.Reset();

        // fresh telemetry record with the full configuration snapshot
        _telemetry.Begin();
        _telemetry.CpuName = _session.CpuName;
        _telemetry.GpuName = _gpuName;
        _telemetry.RamTotalGb = _session.RamTotalGb;
        _telemetry.LogicalCores = Environment.ProcessorCount;
        _telemetry.Elevated = HardwareMonitor.IsAdmin();
        _telemetry.OsVersion = Environment.OSVersion.VersionString;
        _telemetry.AppVersion = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
        _telemetry.CpuEnabled = cpu;
        _telemetry.GpuEnabled = gpu;
        _telemetry.RamEnabled = ram;
        _telemetry.DiskEnabled = disk;
        _telemetry.ForceMax = ForceMaxButton.IsChecked == true;
        _telemetry.CpuKernel = CpuKernelBox.Text;
        _telemetry.CpuThreads = (int)CpuThreadsSlider.Value;
        _telemetry.CpuLoad = (int)CpuLoadSlider.Value;
        _telemetry.GpuLoad = (int)GpuLoadSlider.Value;
        _telemetry.RamTargetGb = _session.RamTargetGb;
        _telemetry.RamVerify = _ram.VerifyEnabled;
        _telemetry.DiskTargetGb = _disk.TargetBytes / 1073741824.0;
        _telemetry.DiskLoad = _disk.LoadPercent;
        _telemetry.CpuLimit = float.TryParse(CpuLimitBox.Text, out float cfgCpu) ? cfgCpu : ThermalWatchdog.DefaultCpuLimitC;
        _telemetry.GpuLimit = float.TryParse(GpuLimitBox.Text, out float cfgGpu) ? cfgGpu : ThermalWatchdog.DefaultGpuLimitC;
        _lastRamErrors = 0;
        _lastDiskErrors = 0;
        _cpuThermalWarned = false;
        _gpuThermalWarned = false;

        _running = true;
        SetRunUi(true);
        _uiTimer.Start();
        string parts = string.Join(" + ", new[] { cpu ? "CPU" : null, gpu ? "GPU" : null, ram ? "RAM" : null, disk ? "DISK" : null });
        Log($"Stress run started — {parts} — duration {(_duration.TotalSeconds == 0 ? "unlimited" : $"{_duration.TotalSeconds:0}s")}.");
        _telemetry.AddEvent("INFO", $"Stress run started — {parts} — duration {(_duration.TotalSeconds == 0 ? "unlimited" : $"{_duration.TotalSeconds:0}s")}");
        Log(_monitor.Connected
            ? "Administrator: temperature watchdog is armed."
            : "Not elevated / sensor host down: temperatures may be unavailable — the thermal watchdog will be limited.");
    }

    private void StopRun(string reason)
    {
        if (!_running) return;
        _running = false;
        _uiTimer.Stop();

        // Instant: only sets flags; joins/frees happen on background threads.
        try { _cpu.RequestStop(); } catch { }
        try { _gpu.RequestStop(); } catch { }
        try { _ram.RequestStop(); } catch { }
        try { _disk.RequestStop(); } catch { }
        SetThreadExecutionState(ES_CONTINUOUS); // allow sleep again

        _session.EndedAt = DateTime.Now;
        _session.DurationSeconds = (DateTime.UtcNow - _runStartUtc).TotalSeconds;
        CaptureEngineTotals();
        _telemetry.EndedAt = DateTime.Now;
        _telemetry.AddEvent("INFO", $"Stress run {reason} after {TimeSpan.FromSeconds(_session.DurationSeconds):hh\\:mm\\:ss}");

        SetRunUi(false);
        if (_duration.TotalSeconds > 0) RunProgress.Value = 100;
        Log($"Run {reason}. Duration {TimeSpan.FromSeconds(_session.DurationSeconds):hh\\:mm\\:ss} — " +
            $"CPU {_session.CpuGflops.Average:0.##} GFLOPS avg · GPU {_session.GpuGflops.Average:0} GFLOPS avg · " +
            $"RAM {_session.RamThroughput.Average:0} MB/s avg · {_session.RamErrors:N0} memory errors.");
        ExportButton.IsEnabled = true;
        SaveDetailedReport();
        UiTick();
    }

    private void SaveDetailedReport()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "reports");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string mdPath = Path.Combine(dir, $"StressLab-Report-{stamp}.md");
            File.WriteAllText(mdPath, DetailedReport.BuildMarkdown(_telemetry));
            string csvPath = Path.Combine(dir, $"StressLab-Telemetry-{stamp}.csv");
            DetailedReport.WriteCsv(_telemetry, csvPath);
            Log("📋 Detailed report saved:");
            Log($"   {mdPath}");
            Log($"   {csvPath} (full-resolution telemetry)");
        }
        catch (Exception ex)
        {
            Log($"Could not save the detailed report: {ex.Message}");
        }
    }

    private void CaptureEngineTotals()
    {
        var cs = _cpu.Snapshot();
        _session.CpuTotalWork = cs.TotalWork;
        if (cs.ElapsedSeconds > 0 && _cpu.Kernel == CpuStressor.KernelKind.FmaFlops)
            _session.CpuGflops.Add(cs.TotalWork / cs.ElapsedSeconds / 1e9);

        var gs = _gpu.Snapshot();
        _session.GpuTotalWork = gs.TotalWork;
        if (gs.ElapsedSeconds > 0) _session.GpuGflops.Add(GpuStressor.EstimateGflops(gs.TotalWork, gs.ElapsedSeconds));

        var rs = _ram.Snapshot();
        _session.RamTotalWork = rs.TotalWork;
        _session.RamErrors = _ram.ErrorCount;
    }

    private void SetRunUi(bool running)
    {
        StartButton.Content = running ? "STOP" : "START TEST";
        StartButton.Style = (Style)FindResource(running ? "DangerButton" : "StartButton");
        PresetRow.IsEnabled = !running;
        CpuKernelBox.IsEnabled = !running;
        CpuThreadsSlider.IsEnabled = !running;
        CpuLoadSlider.IsEnabled = !running;
        GpuLoadSlider.IsEnabled = !running;
        RamGbSlider.IsEnabled = !running;
        RamVerifyBox.IsEnabled = !running;
        DiskGbSlider.IsEnabled = !running;
        DiskLoadSlider.IsEnabled = !running;
        DiskVerifyBox.IsEnabled = !running;
        CpuLimitBox.IsEnabled = !running;
        GpuLimitBox.IsEnabled = !running;
        DurationBox.IsEnabled = !running;
    }

    private TimeSpan SelectedDuration()
        => DurationBox.SelectedIndex switch
        {
            0 => TimeSpan.FromSeconds(30),
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(2),
            4 => TimeSpan.FromMinutes(15),
            5 => TimeSpan.FromMinutes(30),
            6 => TimeSpan.Zero,
            _ => TimeSpan.FromMinutes(5)
        };

    // ---------------------------------------------------------------- timers

    private void MonitorTick()
    {
        if (!_probed) return;
        var m = ReadMonitorSafe();
        if (m is null) return;
        _lastSnapshot = m;

        if (m.CpuLoadPercent is float cl) _session.CpuLoadMetric.Add(cl);
        if (m.CpuTempC is float ct) _session.CpuTemp.Add(ct);
        if (m.GpuLoadPercent is float gl) _session.GpuLoadMetric.Add(gl);
        if (m.GpuTempC is float gt) _session.GpuTemp.Add(gt);
        if (m.RamUsedPercent is float ru) _session.RamUsedMetric.Add(ru);

        if (_running)
        {
            var cs = _cpu.Snapshot();
            _session.CpuTotalWork = cs.TotalWork;
            if (_cpu.Kernel == CpuStressor.KernelKind.FmaFlops && cs.ElapsedSeconds > 0.2)
                _session.CpuGflops.Add(cs.WorkPerSecond / 1e9);

            var gs = _gpu.Snapshot();
            _session.GpuTotalWork = gs.TotalWork;
            if (gs.ElapsedSeconds > 0.2) _session.GpuGflops.Add(GpuStressor.EstimateGflops(gs.TotalWork, gs.ElapsedSeconds));

            var rs = _ram.Snapshot();
            _session.RamTotalWork = rs.TotalWork;
            _session.RamErrors = _ram.ErrorCount;
            if (rs.WorkPerSecond > 0) _session.RamThroughput.Add(rs.WorkPerSecond / (1024.0 * 1024.0));

            var ds = _disk.Snapshot();

            float cpuLimit = float.TryParse(CpuLimitBox.Text, out float parsedCpu) ? parsedCpu : ThermalWatchdog.DefaultCpuLimitC;
            float gpuLimit = float.TryParse(GpuLimitBox.Text, out float parsedGpu) ? parsedGpu : ThermalWatchdog.DefaultGpuLimitC;

            // ---- 1 Hz telemetry sample of the whole system + every engine
            double ramWriteMbps = Math.Max(0, rs.WorkPerSecond - rs.AuxWorkPerSecond) / (1024.0 * 1024.0);
            double ramVerifyMbps = rs.AuxWorkPerSecond / (1024.0 * 1024.0);
            double diskWriteMbps = Math.Max(0, ds.WorkPerSecond - ds.AuxWorkPerSecond) / (1024.0 * 1024.0);
            double diskReadMbps = ds.AuxWorkPerSecond / (1024.0 * 1024.0);
            float? gpuLoadTelemetry = m.GpuLoadPercent;
            if (gs.State == EngineState.Running && gs.DutyPercent > 0 && (gpuLoadTelemetry is null || gpuLoadTelemetry < 1f))
                gpuLoadTelemetry = (float)Math.Min(100.0, gs.DutyPercent);
            _telemetry.AddSample(new TelemetrySample(
                DateTime.Now,
                m.CpuLoadPercent, m.CpuTempC, m.CpuPowerW, m.CpuClockMhz,
                gpuLoadTelemetry, m.GpuTempC,
                m.GpuMemUsedMb is float vramMb ? vramMb / 1024f : null,
                m.RamUsedPercent,
                m.FansRpm,
                cs.WorkPerSecond / 1e9,   // GFLOPS for the FMA kernel, G work-units/s otherwise
                gs.WorkPerSecond,
                gs.WorkPerSecond > 0 ? GpuStressor.EstimateGflops((long)gs.WorkPerSecond, 1.0) : 0,
                ramWriteMbps, ramVerifyMbps,
                diskWriteMbps, diskReadMbps,
                _disk.ErrorCount,
                _ram.ErrorCount,
                cs.State.ToString(), gs.State.ToString(), rs.State.ToString(), ds.State.ToString()));

            if (_ram.ErrorCount > _lastRamErrors)
            {
                _telemetry.AddEvent("FAIL", $"{_ram.ErrorCount:N0} memory verification errors detected so far (first corrupt addresses are recorded by the engine)");
                Log($"⚠ RAM verification errors: {_ram.ErrorCount:N0} — see the report for details.");
                _lastRamErrors = _ram.ErrorCount;
            }
            if (_disk.ErrorCount > _lastDiskErrors)
            {
                _telemetry.AddEvent("FAIL", $"{_disk.ErrorCount:N0} disk checksum errors detected so far (block offsets are recorded by the engine)");
                Log($"⚠ DISK checksum errors: {_disk.ErrorCount:N0} — see the report for details.");
                _lastDiskErrors = _disk.ErrorCount;
            }

            if (_ram.ErrorCount > _lastRamErrors)
            {
                _telemetry.AddEvent("FAIL", $"{_ram.ErrorCount:N0} memory verification errors detected so far (first corrupt addresses are recorded by the engine)");
                Log($"⚠ RAM verification errors: {_ram.ErrorCount:N0} — see the report for details.");
                _lastRamErrors = _ram.ErrorCount;
            }

            if (m.CpuTempC is float cpuWarnTemp && CpuEnabled.IsChecked == true
                && !_cpuThermalWarned && cpuWarnTemp >= cpuLimit - 10)
            {
                _cpuThermalWarned = true;
                _telemetry.AddEvent("WARN", $"CPU temperature approaching its limit: {cpuWarnTemp:0}°C (limit {cpuLimit:0}°C)");
                Log($"CPU temperature {cpuWarnTemp:0}°C — approaching the {cpuLimit:0}°C limit.");
            }
            if (m.GpuTempC is float gpuWarnTemp && GpuEnabled.IsChecked == true
                && !_gpuThermalWarned && gpuWarnTemp >= gpuLimit - 10)
            {
                _gpuThermalWarned = true;
                _telemetry.AddEvent("WARN", $"GPU temperature approaching its limit: {gpuWarnTemp:0}°C (limit {gpuLimit:0}°C)");
                Log($"GPU temperature {gpuWarnTemp:0}°C — approaching the {gpuLimit:0}°C limit.");
            }

            string? trip = ThermalWatchdog.Check(
                m, cpuLimit, gpuLimit,
                CpuEnabled.IsChecked == true, GpuEnabled.IsChecked == true);
            if (trip is not null)
            {
                _telemetry.AddEvent("FAIL", $"THERMAL STOP — {trip}");
                _session.Events.Add($"[{DateTime.Now:HH:mm:ss}] THERMAL STOP — {trip}");
                ThermalBannerText.Text = $"⚠ {trip} — every engine was stopped safely.";
                ThermalBanner.Visibility = Visibility.Visible;
                StopRun("aborted by the thermal watchdog");
            }
        }
    }

    private void UiTick()
    {
        var m = _lastSnapshot;

        CpuBig.Text = MetricsSnapshot.F(m.CpuLoadPercent);
        CpuGauge.Value = m.CpuLoadPercent ?? 0;
        if (_running) CpuSpark.Push(m.CpuLoadPercent ?? 0);
        CpuTempChip.Text = MetricsSnapshot.F(m.CpuTempC, "0", " °C");
        CpuPowerChip.Text = MetricsSnapshot.F(m.CpuPowerW, "0.0", " W");
        CpuClockChip.Text = m.CpuClockMhz is float mhz ? (mhz / 1000.0).ToString("0.00") + " GHz" : "—";
        var cs = _cpu.Snapshot();
        CpuStatusLine.Text = cs.State switch
        {
            EngineState.Running => $"RUNNING · {cs.StatusLine}",
            EngineState.Stopped => $"DONE · {cs.StatusLine}",
            EngineState.Error => cs.StatusLine,
            _ => "Idle — enabled, waiting for START"
        };
        LogEngineErrorOnce("CPU", cs);

        var gs = _gpu.Snapshot();
        // Real-time GPU usage: prefer the hardware sensor; when a board doesn't expose
        // one (common on AMD APUs), show the engine's own measured busy-ratio instead
        // of a misleading 0%.
        float? gpuShown = m.GpuLoadPercent;
        if (gs.State == EngineState.Running && gs.DutyPercent > 0 && (gpuShown is null || gpuShown < 1f))
            gpuShown = (float)Math.Min(100.0, gs.DutyPercent);
        GpuBig.Text = MetricsSnapshot.F(gpuShown);
        GpuGauge.Value = gpuShown ?? 0;
        if (_running) GpuSpark.Push(gpuShown ?? 0);
        GpuTempChip.Text = MetricsSnapshot.F(m.GpuTempC, "0", " °C");
        GpuVramChip.Text = m.GpuMemUsedMb is float used && m.GpuMemTotalMb is float tot
            ? $"{used / 1024f:0.0} / {tot / 1024f:0.0} GB"
            : MetricsSnapshot.F(m.GpuMemUsedMb, "0", " MB");
        GpuStatusLine.Text = gs.State switch
        {
            EngineState.Running => $"RUNNING · {gs.StatusLine}",
            EngineState.Stopped => $"DONE · {gs.StatusLine}",
            EngineState.Error => gs.StatusLine,
            _ => "Idle — enabled, waiting for START"
        };
        LogEngineErrorOnce("GPU", gs);

        RamBig.Text = MetricsSnapshot.F(m.RamUsedPercent);
        RamGauge.Value = m.RamUsedPercent ?? 0;
        if (_running) RamSpark.Push(m.RamUsedPercent ?? 0);
        RamUsedChip.Text = m.RamUsedGb is float usedGb && m.RamTotalGb is float totGb
            ? $"{usedGb:0.0} / {totGb:0.#} GB"
            : "—";
        RamErrorsChip.Text = _ram.ErrorCount.ToString("N0");
        var rs = _ram.Snapshot();
        RamStatusLine.Text = rs.State switch
        {
            EngineState.Running => $"RUNNING · {rs.StatusLine}",
            EngineState.Stopped => $"DONE · {rs.StatusLine}",
            EngineState.Error => rs.StatusLine,
            _ => "Idle — enabled, waiting for START"
        };
        LogEngineErrorOnce("RAM", rs);

        var ds = _disk.Snapshot();
        double diskTotalMbps = ds.WorkPerSecond / (1024.0 * 1024.0);
        DiskBig.Text = diskTotalMbps < 10 ? diskTotalMbps.ToString("0.0") : diskTotalMbps.ToString("0");
        DiskGauge.Value = ds.State == EngineState.Running ? 100 : 0;
        if (_running) DiskSpark.Push(diskTotalMbps);
        DiskWrittenChip.Text = $"{ds.TotalWork / (1024.0 * 1024.0 * 1024.0):0.0} GB";
        DiskErrorsChip.Text = _disk.ErrorCount.ToString("N0");
        FansChip.Text = _lastSnapshot.FansRpm is float rpm ? $"{rpm:0} RPM" : "—";
        DiskStatusLine.Text = ds.State switch
        {
            EngineState.Running => $"RUNNING · {ds.StatusLine}",
            EngineState.Stopped => $"DONE · {ds.StatusLine}",
            EngineState.Error => ds.StatusLine,
            _ => "Idle — enabled, waiting for START"
        };
        LogEngineErrorOnce("DISK", ds);

        if (_running)
        {
            double elapsed = (DateTime.UtcNow - _runStartUtc).TotalSeconds;
            if (_duration.TotalSeconds == 0)
            {
                RunProgress.IsIndeterminate = false;
                RunProgress.Value = 0;
                TimeLine.Text = TimeSpan.FromSeconds(elapsed).ToString(@"hh\:mm\:ss") + " elapsed";
            }
            else
            {
                RunProgress.IsIndeterminate = false;
                RunProgress.Value = Math.Min(100, elapsed / _duration.TotalSeconds * 100);
                TimeLine.Text = TimeSpan.FromSeconds(elapsed).ToString(@"hh\:mm\:ss") + " / " +
                                TimeSpan.FromSeconds(_duration.TotalSeconds).ToString(@"hh\:mm\:ss");
                if (elapsed >= _duration.TotalSeconds) StopRun("completed");
            }
        }
    }

    // ---------------------------------------------------------------- UI events

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        switch (tag)
        {
            case "cpu": CpuEnabled.IsChecked = true; GpuEnabled.IsChecked = false; RamEnabled.IsChecked = false; DiskEnabled.IsChecked = false; break;
            case "gpu": CpuEnabled.IsChecked = false; GpuEnabled.IsChecked = true; RamEnabled.IsChecked = false; DiskEnabled.IsChecked = false; break;
            case "ram": CpuEnabled.IsChecked = false; GpuEnabled.IsChecked = false; RamEnabled.IsChecked = true; DiskEnabled.IsChecked = false; break;
            case "disk": CpuEnabled.IsChecked = false; GpuEnabled.IsChecked = false; RamEnabled.IsChecked = false; DiskEnabled.IsChecked = true; break;
            default: CpuEnabled.IsChecked = true; GpuEnabled.IsChecked = true; RamEnabled.IsChecked = true; DiskEnabled.IsChecked = true; break;
        }
    }

    private void EngineToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, CpuEnabled) && CpuCard is not null) CpuCard.Opacity = CpuEnabled.IsChecked == true ? 1 : 0.45;
        if (ReferenceEquals(sender, GpuEnabled) && GpuCard is not null) GpuCard.Opacity = GpuEnabled.IsChecked == true ? 1 : 0.45;
        if (ReferenceEquals(sender, RamEnabled) && RamCard is not null) RamCard.Opacity = RamEnabled.IsChecked == true ? 1 : 0.45;
        if (ReferenceEquals(sender, DiskEnabled) && DiskCard is not null) DiskCard.Opacity = DiskEnabled.IsChecked == true ? 1 : 0.45;
    }

    private void CpuThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuThreadsValue is not null) CpuThreadsValue.Text = ((int)e.NewValue).ToString();
    }

    private void CpuLoadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuLoadValue is not null) CpuLoadValue.Text = $"{(int)e.NewValue}%";
    }

    private void GpuLoadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GpuLoadValue is not null) GpuLoadValue.Text = $"{(int)e.NewValue}%";
    }

    private void RamGbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RamGbValue is not null) RamGbValue.Text = $"{(int)e.NewValue} GB";
    }

    private void DiskGbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DiskGbValue is not null) DiskGbValue.Text = $"{(int)e.NewValue} GB";
    }

    private void DiskLoadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DiskLoadValue is not null) DiskLoadValue.Text = $"{(int)e.NewValue}%";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Markdown report (*.md)|*.md|Text file (*.txt)|*.txt",
            FileName = $"StressLab-Report-{DateTime.Now:yyyyMMdd-HHmmss}.md"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, DetailedReport.BuildMarkdown(_telemetry));
                Log($"Report saved → {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Log($"Could not save report: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- infra

    internal bool SensorUp => _monitor.Connected;

    internal MetricsSnapshot? SensorSnapshot() => ReadMonitorSafe();

    internal int SensorFanControlCount() => _monitor.CountFanControls();

    internal void Log(string message)
    {
        LogList.Items.Add($"[{DateTime.Now:HH:mm:ss}]  {message}");
        while (LogList.Items.Count > 400) LogList.Items.RemoveAt(0);
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void LogSafe(string message)
    {
        Dispatcher.Invoke(() => Log(message));
    }

    internal async Task DemoRunForScreenshotAsync(string pngPath)
    {
        Dispatcher.Invoke(() =>
        {
            RamGbSlider.Value = Math.Min(4, RamGbSlider.Maximum);
            CpuEnabled.IsChecked = true;
            GpuEnabled.IsChecked = true;
            RamEnabled.IsChecked = true;
            DiskEnabled.IsChecked = true;
            DurationBox.SelectedIndex = 6;
            ThermalBannerText.Text = "Thermal watchdog armed — the run aborts automatically at the configured limits.";
            ThermalBanner.Visibility = Visibility.Visible;
        });
        await Dispatcher.InvokeAsync(StartRun);
        await Task.Delay(3000);
        await Dispatcher.InvokeAsync(() =>
        {
            ThermalBanner.Visibility = Visibility.Collapsed;
            UiTick();
            UpdateLayout();
        });
        // let a full render pass happen before capturing the frame
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.InvokeAsync(() => SaveScreenshot(pngPath));
        await Dispatcher.InvokeAsync(() => StopRun("demo run finished"));
    }

    internal void SaveScreenshot(string path)
    {
        var rtb = new RenderTargetBitmap(
            (int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private bool _cleanedUp;

    private void EmergencyCleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        _uiTimer.Stop();
        _monitorTimer.Stop();
        _running = false;
        App.Breadcrumb("cleanup: begin");

        // Orderly, bounded shutdown: flags first (engines notice within one burst),
        // then blocking stops (each join is timeout-bounded and typically <300 ms),
        // then the sensor driver — inline, so the process never tears down while a
        // native driver call is still in flight (that races into access violations).
        try { _cpu.RequestStop(); } catch { }
        try { _gpu.RequestStop(); } catch { }
        try { _ram.RequestStop(); } catch { }
        try { _disk.RequestStop(); } catch { }
        App.Breadcrumb("cleanup: engine flags set");
        try { _cpu.Stop(); } catch { }
        try { _gpu.Stop(); } catch { }
        try { _ram.Stop(); } catch { }
        try { _disk.Stop(); } catch { }
        App.Breadcrumb("cleanup: engines stopped");

        try
        {
            App.Breadcrumb("cleanup: force-max off begin");
            if (ForceMaxButton is not null) ForceMaxButton.IsChecked = false;
            DisengageForceMax();
            App.Breadcrumb("cleanup: force-max off done");
        }
        catch { }

        App.Breadcrumb("cleanup: monitor disposing");
        try { _monitor.Dispose(); } catch { }
        App.Breadcrumb("cleanup: monitor disposed");
        SetThreadExecutionState(ES_CONTINUOUS);
        App.Breadcrumb("cleanup: done");
    }

    /// <summary>Called by the smoke runner before Shutdown so every engine, the sensor
    /// driver and FORCE MAX are fully settled before the process exits.</summary>
    internal void PrepareForExit()
    {
        Dispatcher.Invoke(EmergencyCleanup);
    }
}
