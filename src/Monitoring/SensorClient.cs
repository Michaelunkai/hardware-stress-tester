using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace StressLab.Monitoring;

/// <summary>
/// Parent-side client for the sandboxed sensor host child process. The child owns all
/// LibreHardwareMonitor driver access; if it ever hangs or faults, the parent kills and
/// respawns it — the GUI can keep stressing regardless of what the driver does.
/// </summary>
public sealed class SensorClient : IDisposable
{
    private Process? _child;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly string _portFile = Path.Combine(Path.GetTempPath(), $"stresslab-sensors-{Guid.NewGuid():N}.port");
    private DateTime _lastSpawn = DateTime.MinValue;

    public bool Connected { get; private set; }
    public string CpuName { get; private set; } = "";
    public string GpuName { get; private set; } = "";

    public bool Start()
    {
        if (!EnsureAlive()) return false;
        string? names = Send("n");
        if (names is not null && names.StartsWith("N|", StringComparison.Ordinal))
        {
            var parts = names.Split('|');
            CpuName = parts.Length > 1 ? parts[1] : "";
            GpuName = parts.Length > 2 ? parts[2] : "";
        }
        return Connected;
    }

    private bool EnsureAlive()
    {
        if (Connected) return true;
        if ((DateTime.UtcNow - _lastSpawn).TotalSeconds < 2) return false;
        _lastSpawn = DateTime.UtcNow;

        try
        {
            try { File.Delete(_portFile); } catch { }

            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (exe.Length == 0) return false;

            var psi = new ProcessStartInfo(exe);
            psi.ArgumentList.Add("--sensorhost");
            psi.ArgumentList.Add(_portFile);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            _child = Process.Start(psi);

            for (int i = 0; i < 40 && !File.Exists(_portFile); i++) Thread.Sleep(100);
            if (_child is null || _child.HasExited || !File.Exists(_portFile))
            {
                Kill();
                return false;
            }

            int port = int.Parse(File.ReadAllText(_portFile).Trim(), CultureInfo.InvariantCulture);
            var tcp = new TcpClient();
            if (!tcp.ConnectAsync(IPAddress.Loopback, port).Wait(2000))
            {
                tcp.Dispose();
                Kill();
                return false;
            }

            _client = tcp;
            var stream = tcp.GetStream();
            stream.ReadTimeout = 10000;
            stream.WriteTimeout = 5000;
            _reader = new StreamReader(stream);
            _writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
            Connected = true;
            return true;
        }
        catch
        {
            Kill();
            return false;
        }
    }

    private string? Send(string command)
    {
        if (!Connected && !EnsureAlive()) return null;
        try
        {
            _writer!.WriteLine(command);
            string? resp = _reader!.ReadLine();
            if (resp is null) throw new IOException("sensor host closed the connection");
            return resp;
        }
        catch
        {
            Kill();
            return null;
        }
    }

    public MetricsSnapshot? Read()
    {
        string? resp = Send("r");
        if (resp is null || !resp.StartsWith("R|", StringComparison.Ordinal)) return null;

        var f = resp.Split('|');
        string? V(int i) => f.Length > i && f[i].Length > 0 ? f[i] : null;
        float? P(int i) => V(i) is string s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;

        return new MetricsSnapshot
        {
            CpuTempC = P(1),
            CpuLoadPercent = P(2),
            CpuPowerW = P(3),
            CpuClockMhz = P(4),
            GpuTempC = P(5),
            GpuLoadPercent = P(6),
            GpuMemUsedMb = P(7),
            GpuMemTotalMb = P(8),
            FansRpm = P(9),
            RamUsedPercent = P(10),
            RamUsedGb = P(11),
            RamTotalGb = P(12)
        };
    }

    public int CountFanControls()
    {
        string? resp = Send("c");
        return resp is not null && resp.StartsWith("C|", StringComparison.Ordinal)
            && int.TryParse(resp.AsSpan(2), out int n) ? n : 0;
    }

    public int SetAllFanControls(float percent)
    {
        string? resp = Send("s" + percent.ToString("0", CultureInfo.InvariantCulture));
        return resp is not null && resp.StartsWith("S|", StringComparison.Ordinal)
            && int.TryParse(resp.AsSpan(2), out int n) ? n : 0;
    }

    public int RestoreAllFanControls()
    {
        string? resp = Send("d");
        return resp is not null && resp.StartsWith("S|", StringComparison.Ordinal)
            && int.TryParse(resp.AsSpan(2), out int n) ? n : 0;
    }

    public void Kill()
    {
        Connected = false;
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _writer = null;
        _reader = null;
        _client = null;
        try
        {
            if (_child is { HasExited: false } child) child.Kill(entireProcessTree: true);
        }
        catch { }
        _child?.Dispose();
        _child = null;
    }

    public void Dispose()
    {
        Kill();
        try { File.Delete(_portFile); } catch { }
    }
}
