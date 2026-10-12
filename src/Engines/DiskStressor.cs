using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace StressLab.Engines;

/// <summary>
/// DISK stress engine: rewrites a bounded scratch file (default 4 GB) in pseudo-random
/// block order at full sequential speed, then reads every block back and verifies its
/// XxHash3 checksum — memtest-style integrity checking for storage. The scratch file is
/// deleted automatically when the engine stops. Note: stress-testing storage writes real
/// data; the bounded region keeps wear proportional to run length.
/// </summary>
public sealed class DiskStressor : IStressEngine
{
    public const int BlockBytes = 1 << 20; // 1 MiB
    private const int PatternPoolBlocks = 16;
    private const long MinTargetBytes = 512L << 20;
    private const ulong FreeSpaceReserveBytes = 20L * 1024 * 1024 * 1024;

    private readonly object _lock = new();
    private EngineStats _stats = new();
    private FileStream? _file;
    private string _scratchPath = "";
    private byte[][] _patterns = Array.Empty<byte[]>();
    private ulong[] _patternHashes = Array.Empty<ulong>();
    private ulong[] _expectedHashes = Array.Empty<ulong>();
    private long _blockCount;
    private Thread? _thread;
    private volatile bool _stop;
    private int _generation;
    private long _bytesWritten;
    private long _bytesRead;
    private double _writeMbps;
    private double _readMbps;
    private long _errors;
    private long _rounds;
    private string _phase = "Idle";
    private double _phaseProgress;
    private long _startTimestamp = Stopwatch.GetTimestamp();

    public long TargetBytes { get; set; } = 4L << 30;
    public int LoadPercent { get; set; } = 100;
    public bool VerifyEnabled { get; set; } = true;

    public string Name => "DISK";
    public long ErrorCount => Interlocked.Read(ref _errors);

    public void Start()
    {
        Thread? previous;
        lock (_lock)
        {
            if (_stats.State == EngineState.Running) return;
            previous = _thread;
            _thread = null;
        }
        previous?.Join(3000);

        lock (_lock)
        {
            _stop = false;
            _errors = 0;
            _rounds = 0;
            _bytesWritten = 0;
            _bytesRead = 0;
            _startTimestamp = Stopwatch.GetTimestamp();
            _stats = new EngineStats { State = EngineState.Running, StatusLine = "Preparing scratch file…" };
            int generation = ++_generation;
            _thread = new Thread(() =>
            {
                try
                {
                    TestLoop(generation);
                }
                catch (Exception ex)
                {
                    SetError(ex);
                }
                finally
                {
                    CloseScratch();
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "StressLab-DISK"
            };
            _thread.Start();
        }
    }

    private void TestLoop(int generation)
    {
        OpenScratch();
        while (!_stop && generation == Volatile.Read(ref _generation))
        {
            _rounds++;
            _phase = $"Round {_rounds} — writing";
            WriteAllBlocks(generation);
            if (VerifyEnabled)
            {
                _phase = $"Round {_rounds} — verifying";
                VerifyAllBlocks(generation);
            }
            lock (_lock) _phaseProgress = 1.0;
            int sleep = Governor.ComputeSleepMs(LoadPercent, Governor.BurstMs);
            if (sleep > 0) Thread.Sleep(sleep);
        }
    }

    private void OpenScratch()
    {
        long target = ComputeTargetBytes(TargetBytes);
        string path = Path.Combine(Path.GetTempPath(), "StressLab-scratch.bin");
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // a stale scratch file that cannot be deleted must not stop the run
        }

        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            BlockBytes, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        file.SetLength(target);

        int blockCount = (int)(target / BlockBytes);
        var patterns = new byte[PatternPoolBlocks][];
        var patternHashes = new ulong[PatternPoolBlocks];
        ulong x = 0x9E3779B97F4A7C15UL;
        for (int p = 0; p < PatternPoolBlocks; p++)
        {
            var block = new byte[BlockBytes];
            Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(block.AsSpan());
            for (int i = 0; i < words.Length; i++)
            {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                words[i] = x;
            }
            patterns[p] = block;
            patternHashes[p] = XxHash3.HashToUInt64(block);
        }

        lock (_lock)
        {
            _file = file;
            _scratchPath = path;
            _patterns = patterns;
            _patternHashes = patternHashes;
            _expectedHashes = new ulong[blockCount];
            _blockCount = blockCount;
        }
    }

    private void WriteAllBlocks(int generation)
    {
        long blocks;
        lock (_lock) blocks = _blockCount;
        int[] order = ShuffledOrder(blocks, roundsSeed: (ulong)_rounds * 2 + 1);
        for (int k = 0; k < order.Length && Alive(generation); k++)
        {
            long idx = order[k];
            int pool = (int)((k + _rounds) % PatternPoolBlocks);
            var sw = Stopwatch.StartNew();
            WriteBlock(idx, pool);
            sw.Stop();
            lock (_lock)
            {
                _bytesWritten += BlockBytes;
                SmoothMbps(ref _writeMbps, BlockBytes, sw.Elapsed.TotalSeconds);
                _phaseProgress = (k + 1) / (double)blocks * 0.5;
            }
        }
    }

    private void WriteBlock(long index, int pool)
    {
        FileStream? file;
        byte[]? pattern;
        lock (_lock)
        {
            file = _file;
            pattern = _patterns.ElementAtOrDefault(pool);
        }
        if (file is null || pattern is null) return;
        file.Seek(index * BlockBytes, SeekOrigin.Begin);
        file.Write(pattern, 0, BlockBytes);
        lock (_lock) _expectedHashes[index] = _patternHashes[pool];
    }

    private void VerifyAllBlocks(int generation)
    {
        long blocks;
        lock (_lock) blocks = _blockCount;
        int[] order = ShuffledOrder(blocks, roundsSeed: (ulong)_rounds * 2 + 2);
        var buffer = new byte[BlockBytes];
        for (int k = 0; k < order.Length && Alive(generation); k++)
        {
            long idx = order[k];
            var sw = Stopwatch.StartNew();
            bool ok = VerifyBlock(idx, buffer);
            sw.Stop();
            lock (_lock)
            {
                _bytesRead += BlockBytes;
                SmoothMbps(ref _readMbps, BlockBytes, sw.Elapsed.TotalSeconds);
                _phaseProgress = 0.5 + (k + 1) / (double)blocks * 0.5;
                if (!ok) _errors++;
            }
        }
    }

    /// <summary>Fisher–Yates shuffle of 0..count-1 — guarantees every block is visited
    /// exactly once per phase (a modular permutation can silently skip blocks when the
    /// stride shares a factor with the block count).</summary>
    private int[] ShuffledOrder(long blocks, ulong roundsSeed)
    {
        var order = new int[blocks];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        ulong x = roundsSeed * 0x9E3779B97F4A7C15UL + 0xDEADBEEFUL;
        for (int i = order.Length - 1; i > 0; i--)
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            int j = (int)(x % (ulong)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }

    private bool VerifyBlock(long index, byte[] buffer)
    {
        FileStream? file;
        ulong expected;
        lock (_lock)
        {
            file = _file;
            expected = index < _expectedHashes.Length ? _expectedHashes[index] : 0;
        }
        if (file is null) return false;
        lock (file)
        {
            file.Seek(index * BlockBytes, SeekOrigin.Begin);
            int read = 0;
            while (read < BlockBytes)
            {
                int n = file.Read(buffer, read, BlockBytes - read);
                if (n <= 0) return false;
                read += n;
            }
        }
        if (!VerifyEnabled) return true;
        return XxHash3.HashToUInt64(buffer) == expected;
    }    private bool Alive(int generation) => !_stop && generation == Volatile.Read(ref _generation);

    private void CloseScratch()
    {
        FileStream? file;
        string path = "";
        lock (_lock)
        {
            file = _file;
            _file = null;
            path = _scratchPath;
            _scratchPath = "";
        }
        try { file?.Dispose(); } catch { }
        try { if (path.Length > 0 && File.Exists(path)) File.Delete(path); } catch { }
    }

    public void RequestStop()
    {
        _stop = true;
        Task.Run(Stop);
    }

    public void Stop()
    {
        _stop = true;
        Thread? thread;
        lock (_lock)
        {
            if (_stats.State != EngineState.Running) return;
            thread = _thread;
            _thread = null;
        }
        thread?.Join(8000);
        CloseScratch();
        lock (_lock)
        {
            RefreshStats(EngineState.Stopped);
        }
    }

    public EngineStats Snapshot()
    {
        lock (_lock)
        {
            if (_stats.State == EngineState.Running) RefreshStats(EngineState.Running);
            return new EngineStats
            {
                State = _stats.State,
                WorkPerSecond = _stats.WorkPerSecond,
                AuxWorkPerSecond = _stats.AuxWorkPerSecond,
                TotalWork = _stats.TotalWork,
                ElapsedSeconds = _stats.ElapsedSeconds,
                StatusLine = _stats.StatusLine,
                ErrorText = _stats.ErrorText
            };
        }
    }

    public void Dispose() => RequestStop();

    private void SetError(Exception ex)
    {
        lock (_lock)
        {
            _stats.State = EngineState.Error;
            _stats.ErrorText = ex.Message;
            _stats.StatusLine = "Error: " + ex.Message;
        }
    }

    private void RefreshStats(EngineState state)
    {
        double elapsed = Math.Max(0.001, (Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency);
        _stats.TotalWork = _bytesWritten + _bytesRead;
        _stats.ElapsedSeconds = elapsed;
        double mbps = _writeMbps + _readMbps;
        _stats.WorkPerSecond = mbps * 1024.0 * 1024.0;
        _stats.AuxWorkPerSecond = _readMbps * 1024.0 * 1024.0;
        _stats.State = state;
        string phaseText = state == EngineState.Running ? _phase : "Finished";
        _stats.StatusLine = VerifyEnabled
            ? $"{phaseText} · {_writeMbps:0} MB/s write · {_readMbps:0} MB/s read · {_errors:N0} errors"
            : $"{phaseText} · {_writeMbps:0} MB/s (verify off)";
    }

    private void SmoothMbps(ref double mbps, long bytes, double seconds)
    {
        if (seconds <= 0.0005) return;
        double instant = bytes / (seconds * 1024.0 * 1024.0);
        mbps = mbps <= 0 ? instant : mbps * 0.6 + instant * 0.4;
    }

    public static long ComputeTargetBytes(long requestedBytes)
    {
        long cap = MinTargetBytes;
        try
        {
            var drive = new DriveInfo(Path.GetTempPath());
            long usable = drive.AvailableFreeSpace - (long)FreeSpaceReserveBytes;
            cap = Math.Max(MinTargetBytes, Math.Min(requestedBytes, usable));
        }
        catch
        {
            cap = Math.Max(MinTargetBytes, requestedBytes);
        }
        return Math.Clamp(cap, MinTargetBytes, long.MaxValue);
    }
}
