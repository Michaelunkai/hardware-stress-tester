using System.Diagnostics;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace StressLab.Monitoring;

/// <summary>
/// Live sensor polling via LibreHardwareMonitor, with graceful fallbacks:
/// CPU load falls back to a PerformanceCounter and RAM figures always come from
/// GlobalMemoryStatusEx, so the app still works without administrator rights
/// (though CPU/GPU temperatures usually require elevation).
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private Computer? _computer;
    private PerformanceCounter? _cpuCounter;

    public bool Opened { get; private set; }
    public bool Elevated { get; private set; }
    public string CpuName { get; private set; } = "";
    public string GpuName { get; private set; } = "";

    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void Open()
    {
        Elevated = IsAdmin();
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true, // fan controls + RPM live on the SuperIO chip
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false
        };
        _computer.Open();
        foreach (var hw in _computer.Hardware)
        {
            string type = hw.HardwareType.ToString();
            if (type == "Cpu" && CpuName.Length == 0) CpuName = hw.Name;
            if (type.StartsWith("Gpu", StringComparison.Ordinal) && GpuName.Length == 0) GpuName = hw.Name;
        }
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue();
        }
        catch
        {
            _cpuCounter = null;
        }
        Opened = true;
    }

    public MetricsSnapshot Read()
    {
        float? cpuTemp = null, cpuLoad = null, cpuPower = null, cpuClock = null;
        float? gpuTemp = null, gpuLoad = null, gpuMemUsed = null, gpuMemTotal = null;
        float? ramLoad = null, ramUsedGb = null, ramAvailGb = null;
        float? fansRpm = null;

        if (_computer is not null)
        {
            foreach (var hw in _computer.Hardware)
            {
                try { hw.Update(); } catch { /* sensor read failures are non-fatal */ }
                string type = hw.HardwareType.ToString();

                if (type == "Cpu")
                {
                    foreach (var s in hw.Sensors)
                    {
                        switch (s.SensorType)
                        {
                            case SensorType.Temperature
                                when s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                                  || s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                                  || s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase):
                                cpuTemp ??= s.Value;
                                break;
                            case SensorType.Load when s.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase):
                                cpuLoad ??= s.Value;
                                break;
                            case SensorType.Power when s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase):
                                cpuPower ??= s.Value;
                                break;
                            case SensorType.Clock when s.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase):
                                if (s.Value is float cv && (!cpuClock.HasValue || cv > cpuClock)) cpuClock = cv;
                                break;
                        }
                    }
                    if (cpuTemp is null)
                    {
                        float maxCore = float.NaN;
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Temperature
                                && s.Value is float v
                                && (float.IsNaN(maxCore) || v > maxCore))
                                maxCore = v;
                        }
                        if (!float.IsNaN(maxCore)) cpuTemp = maxCore;
                    }
                }
                else if (type.StartsWith("Gpu", StringComparison.Ordinal))
                {
                    float? anyLoad = null;
                    foreach (var s in hw.Sensors)
                    {
                        switch (s.SensorType)
                        {
                            case SensorType.Temperature
                                when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                  && !s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase):
                                gpuTemp ??= s.Value;
                                break;
                            case SensorType.Load when s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                                gpuLoad ??= s.Value;
                                break;
                            case SensorType.SmallData when s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase):
                                gpuMemUsed ??= s.Value;
                                break;
                            case SensorType.SmallData when s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase):
                                gpuMemTotal ??= s.Value;
                                break;
                        }
                        if (s.SensorType == SensorType.Load && anyLoad is null && s.Value is float lv && lv > 0f)
                            anyLoad = lv;
                    }
                    // Some GPUs (notably AMD APUs) expose no "Core" load sensor — fall back
                    // to the first positive load reading so the card still shows activity.
                    gpuLoad ??= anyLoad;
                }
                else if (type == "Motherboard" || type.Contains("SuperIO", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Fan && s.Value is float rpm && rpm > 0f
                            && (!fansRpm.HasValue || rpm > fansRpm))
                            fansRpm = rpm;
                    }
                    foreach (var sub in hw.SubHardware)
                    {
                        try { sub.Update(); } catch { }
                        foreach (var s in sub.Sensors)
                        {
                            if (s.SensorType == SensorType.Fan && s.Value is float subRpm && subRpm > 0f
                                && (!fansRpm.HasValue || subRpm > fansRpm))
                                fansRpm = subRpm;
                        }
                    }
                }
                else if (type == "Memory")
                {
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Load) ramLoad ??= s.Value;
                        else if (s.SensorType == SensorType.Data && s.Name.Contains("Used", StringComparison.OrdinalIgnoreCase)) ramUsedGb ??= s.Value;
                        else if (s.SensorType == SensorType.Data && s.Name.Contains("Available", StringComparison.OrdinalIgnoreCase)) ramAvailGb ??= s.Value;
                    }
                }
            }
        }

        if (cpuLoad is null && _cpuCounter is not null)
        {
            try { cpuLoad = _cpuCounter.NextValue(); } catch { }
        }

        var (totalBytes, availBytes) = SystemProbe.GetMemory();
        // Physically impossible readings (0 °C or below) mean "no sensor", not "cold".
        if (cpuTemp is float ct0 && ct0 <= 0) cpuTemp = null;
        if (gpuTemp is float gt0 && gt0 <= 0) gpuTemp = null;

        float? ramUsedPct = null, ramUsed = null, ramTotal = null;
        if (totalBytes > 0)
        {
            ramTotal = (float)(totalBytes / 1073741824.0);
            ramUsedPct = (float)((totalBytes - availBytes) * 100.0 / totalBytes);
            ramUsed = (float)((totalBytes - availBytes) / 1073741824.0);
        }
        else
        {
            if (ramUsedGb is float ug && ramAvailGb is float ag)
            {
                float tg = ug + ag;
                if (tg > 0)
                {
                    ramTotal = tg;
                    ramUsedPct = ug * 100f / tg;
                    ramUsed = ug;
                }
            }
        }

        return new MetricsSnapshot
        {
            CpuTempC = cpuTemp,
            CpuLoadPercent = Normalize(cpuLoad, 0f, 100f),
            CpuPowerW = Normalize(cpuPower, 0f, 4096f),
            CpuClockMhz = Normalize(cpuClock, 0f, 10000f),
            GpuTempC = gpuTemp,
            GpuLoadPercent = Normalize(gpuLoad, 0f, 100f),
            GpuMemUsedMb = Normalize(gpuMemUsed, 0f, 262144f),
            GpuMemTotalMb = Normalize(gpuMemTotal, 0f, 262144f),
            RamUsedPercent = Normalize(ramUsedPct, 0f, 100f),
            RamUsedGb = ramUsed,
            RamTotalGb = ramTotal,
            FansRpm = Normalize(fansRpm, 0f, 60000f)
        };
    }

    /// <summary>Pins every controllable fan to a fixed duty percentage (0-100).
    /// Returns the number of fan controls that were actually set.</summary>
    public int SetAllFanControls(float percent)
        => ApplyFanControls(c => c.SetSoftware(Math.Clamp(percent, 0f, 100f)));

    /// <summary>Returns every controllable fan to its automatic/BIOS curve.</summary>
    public int RestoreAllFanControls()
        => ApplyFanControls(c => c.SetDefault());

    /// <summary>Counts fan PWM controls without touching them.</summary>
    public int CountFanControls()
    {
        int count = 0;
        ForEachControl(_ => count++);
        return count;
    }

    private int ApplyFanControls(Action<LibreHardwareMonitor.Hardware.IControl> apply)
    {
        int count = 0;
        ForEachControl(control =>
        {
            try
            {
                apply(control);
                count++;
            }
            catch
            {
                // a single stubborn fan control must not stop the others
            }
        });
        return count;
    }

    private void ForEachControl(Action<LibreHardwareMonitor.Hardware.IControl> action)
    {
        if (_computer is null) return;
        void ScanHardware(IHardware hw)
        {
            try { hw.Update(); } catch { }
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Control && s.Control is not null)
                    action(s.Control);
            }
            foreach (var sub in hw.SubHardware) ScanHardware(sub);
        }
        foreach (var hw in _computer.Hardware) ScanHardware(hw);
    }

    private static float? Normalize(float? value, float min, float max)
        => value is null || float.IsNaN(value.Value) || value.Value < min || value.Value > max ? null : value;

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _cpuCounter = null;
        try { _computer?.Close(); } catch { }
        _computer = null;
    }
}
