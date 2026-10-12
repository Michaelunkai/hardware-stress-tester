using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using LibreHardwareMonitor.Hardware;

namespace StressLab.Monitoring;

/// <summary>
/// Child-process sensor service (`StressLab.exe --sensorhost <portfile>`).
/// Owns the LibreHardwareMonitor instance — including the motherboard/SuperIO driver
/// access that can hang or fault on some boards — so the GUI process can never die
/// from a driver problem. Speaks a tiny line protocol over a localhost TCP socket:
///   r      → R|cpuTemp|cpuLoad|cpuPower|cpuClock|gpuTemp|gpuLoad|gpuVramUsedMb|gpuVramTotalMb|fansRpm|ramPct|ramUsedGb|ramTotalGb
///   n      → N|cpuName|gpuName
///   c      → C|count
///   s&lt;pct&gt; → S|count   (pin fans to pct)
///   d      → S|count   (restore automatic)
///   q      → exit
/// Empty fields mean "sensor unavailable".
/// </summary>
public static class SensorHostService
{
    public static void Run(string portFilePath)
    {
        var monitor = new HardwareMonitor();
        try { monitor.Open(); } catch { /* the child still serves what it can */ }

        // SuperIO reads can take seconds on some boards — poll in the background and
        // serve the latest cached snapshot instantly, so a slow read never times out
        // the parent or stalls the protocol.
        var cache = new MetricsSnapshot();
        var cacheLock = new object();
        var pollThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var m = monitor.Read();
                    lock (cacheLock) cache = m;
                }
                catch { }
                Thread.Sleep(1000);
            }
        })
        { IsBackground = true, Name = "StressLab-SensorPoll" };
        pollThread.Start();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        File.WriteAllText(portFilePath, port.ToString());

        using var mre = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; mre.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => mre.Set();

        try
        {
            while (!mre.IsSet)
            {
                TcpClient client;
                try
                {
                    if (!listener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    break;
                }

                try
                {
                    ServeClient(client, monitor, () => { lock (cacheLock) return cache; });
                }
                catch
                {
                    // client died or timed out — accept the next one
                }
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { monitor.Dispose(); } catch { }
            try { File.Delete(portFilePath); } catch { }
        }
    }

    private static void ServeClient(TcpClient client, HardwareMonitor monitor, Func<MetricsSnapshot> cacheGet)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

        while (true)
        {
            string? line = reader.ReadLine();
            if (line is null) return;
            line = line.Trim();

            switch (line[0])
            {
                case 'r':
                {
                    var m = cacheGet();
                    writer.WriteLine(string.Join('|',
                        "R",
                        F(m.CpuTempC), F(m.CpuLoadPercent), F(m.CpuPowerW), F(m.CpuClockMhz),
                        F(m.GpuTempC), F(m.GpuLoadPercent), F(m.GpuMemUsedMb), F(m.GpuMemTotalMb),
                        F(m.FansRpm), F(m.RamUsedPercent), F(m.RamUsedGb, "0.00"), F(m.RamTotalGb, "0.00")));
                    break;
                }
                case 'n':
                    writer.WriteLine($"N|{Esc(monitor.CpuName)}|{Esc(monitor.GpuName)}");
                    break;
                case 'c':
                    writer.WriteLine($"C|{monitor.CountFanControls()}");
                    break;
                case 's' when float.TryParse(line.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct):
                    writer.WriteLine($"S|{monitor.SetAllFanControls(pct)}");
                    break;
                case 'd':
                    writer.WriteLine($"S|{monitor.RestoreAllFanControls()}");
                    break;
                case 'q':
                    return;
            }
        }
    }

    private static string F(float? v, string format = "0.000")
        => v is null || float.IsNaN(v.Value) ? "" : v.Value.ToString(format, CultureInfo.InvariantCulture);

    private static string Esc(string s) => s.Replace("|", " ");
}
