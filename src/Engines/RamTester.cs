using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StressLab.Engines;

/// <summary>
/// RAM stress engine: allocates a user-chosen amount of physical memory in large blocks,
/// then continuously writes deterministic bit patterns into them and verifies every word
/// read-back. Any mismatch is counted as an error with its address recorded. This is a
/// true memory integrity stress (memtest-style), not just an allocation hog.
/// Stop is instant: the flag is set immediately and cleanup (join + free + GC) happens
/// on a background thread, never on the UI thread.
/// </summary>
public sealed class RamTester : IStressEngine
{
    private const long BytesPerBlockDefault = 256L << 20;
    private const long BytesPerBlockMin = 16L << 20;
    private const long MinTargetBytes = 64L << 20;

    private readonly object _lock = new();
    private EngineStats _stats = new();
    private readonly List<byte[]> _blocks = new();
    private readonly List<long> _errorAddresses = new();
    private Thread? _thread;
    private volatile bool _stop;
    private int _generation;
    private long _errors;
    private long _rounds;
    private long _bytesWritten;
    private long _bytesVerified;
    private double _writeMbps;
    private double _verifyMbps;
    private double _phaseProgress;
    private string _phase = "Idle";
    private long _startTimestamp = Stopwatch.GetTimestamp();

    public long TargetBytes { get; set; } = 1L << 30;
    public bool VerifyEnabled { get; set; } = true;

    public string Name => "RAM";
    public long ErrorCount => Interlocked.Read(ref _errors);
    public long AllocatedBytes
    {
        get
        {
            lock (_lock) return _blocks.Sum(b => (long)b.Length);
        }
    }

    public void Start()
    {
        Thread? previous;
        lock (_lock)
        {
            if (_stats.State == EngineState.Running) return;
            previous = _thread;
            _thread = null;
        }
        // The previous worker notices the generation change within ~one block pass
        // (tens of ms); join it outside the stats lock.
        previous?.Join(3000);

        lock (_lock)
        {
            _stop = false;
            _errors = 0;
            _rounds = 0;
            _bytesWritten = 0;
            _bytesVerified = 0;
            _errorAddresses.Clear();
            _startTimestamp = Stopwatch.GetTimestamp();
            _stats = new EngineStats { State = EngineState.Running, StatusLine = "Allocating…" };
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
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "StressLab-RAM"
            };
            _thread.Start();
        }
    }

    private void TestLoop(int generation)
    {
        AllocateBlocks();
        int mode = 0;
        while (!_stop && generation == Volatile.Read(ref _generation))
        {
            _rounds++;
            ulong seed = (ulong)_rounds * 0x9E3779B97F4A7C15UL + 0xDEADBEEFUL;

            _phase = $"Round {_rounds} — writing";
            for (int b = 0; b < SafeBlockCount() && Alive(generation); b++)
            {
                byte[]? block = SafeBlock(b);
                if (block is null) break;
                var sw = Stopwatch.StartNew();
                Fill(block, mode, seed + (ulong)b * 0x2545F4914F6CDD1DUL);
                sw.Stop();
                lock (_lock)
                {
                    _bytesWritten += block.Length;
                    SmoothMbps(ref _writeMbps, block.Length, sw.Elapsed.TotalSeconds);
                    _phaseProgress = (b + 1) / (double)Math.Max(1, SafeBlockCount()) * 0.5;
                }
            }

            if (VerifyEnabled)
            {
                _phase = $"Round {_rounds} — verifying";
                for (int b = 0; b < SafeBlockCount() && Alive(generation); b++)
                {
                    byte[]? block = SafeBlock(b);
                    if (block is null) break;
                    long blockOffset = (long)b * block.Length;
                    var sw = Stopwatch.StartNew();
                    long errs = Verify(block, mode, seed + (ulong)b * 0x2545F4914F6CDD1DUL);
                    sw.Stop();
                    lock (_lock)
                    {
                        _bytesVerified += block.Length;
                        SmoothMbps(ref _verifyMbps, block.Length, sw.Elapsed.TotalSeconds);
                        if (errs > 0)
                        {
                            _errors += errs;
                            if (_errorAddresses.Count < 16) _errorAddresses.Add(blockOffset);
                        }
                        _phaseProgress = 0.5 + (b + 1) / (double)Math.Max(1, SafeBlockCount()) * 0.5;
                    }
                }
            }

            lock (_lock) _phaseProgress = 1.0;
            mode = (mode + 1) % PatternCount;
        }
    }

    private bool Alive(int generation) => !_stop && generation == Volatile.Read(ref _generation);

    private int SafeBlockCount()
    {
        lock (_lock) return _blocks.Count;
    }

    private byte[]? SafeBlock(int index)
    {
        lock (_lock) return index < _blocks.Count ? _blocks[index] : null;
    }

    private void AllocateBlocks()
    {
        lock (_lock)
        {
            _blocks.Clear();
            long target = Math.Max(MinTargetBytes, TargetBytes);
            int blockBytes = (int)Math.Clamp(target / 4, BytesPerBlockMin, BytesPerBlockDefault);
            blockBytes -= blockBytes % 64;
            long remaining = target;
            while (remaining > 0 && !_stop)
            {
                int alloc = (int)Math.Min(blockBytes, remaining);
                alloc -= alloc % 64;
                try
                {
                    _blocks.Add(new byte[alloc]);
                }
                catch (OutOfMemoryException)
                {
                    break;
                }
                remaining -= alloc;
            }
            if (_blocks.Count == 0)
                throw new InvalidOperationException("Could not allocate any RAM test blocks.");
        }
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
        int generation;
        lock (_lock)
        {
            if (_stats.State != EngineState.Running) return;
            thread = _thread;
            _thread = null;
            generation = _generation;
        }
        thread?.Join(8000);

        // Free the test memory on this background path — never on the UI thread.
        lock (_lock)
        {
            if (generation == Volatile.Read(ref _generation)) _blocks.Clear();
            RefreshStats(EngineState.Stopped);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
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
        _stats.TotalWork = _bytesWritten + _bytesVerified;
        _stats.ElapsedSeconds = elapsed;
        double mbps = _writeMbps + _verifyMbps;
        _stats.WorkPerSecond = mbps * 1024.0 * 1024.0;
        _stats.State = state;
        string phaseText = state == EngineState.Running ? _phase : "Finished";
        _stats.StatusLine = VerifyEnabled
            ? $"{phaseText} · {_writeMbps:0} MB/s write · {_verifyMbps:0} MB/s verify · {_errors:N0} errors"
            : $"{phaseText} · {_writeMbps:0} MB/s (verify off)";
    }

    private void SmoothMbps(ref double mbps, long bytes, double seconds)
    {
        if (seconds <= 0.0005) return;
        double instant = bytes / (seconds * 1024.0 * 1024.0);
        mbps = mbps <= 0 ? instant : mbps * 0.6 + instant * 0.4;
    }

    // ---------------------------------------------------------------- pattern math

    public const int PatternCount = 5;

    public static void Fill(byte[] block, int mode, ulong seed)
    {
        Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(block.AsSpan());
        switch (mode)
        {
            case 0:
            {
                ulong x = seed | 1UL;
                for (int i = 0; i < words.Length; i++)
                {
                    x ^= x << 13;
                    x ^= x >> 7;
                    x ^= x << 17;
                    words[i] = x;
                }
                break;
            }
            case 1: words.Fill(0xAAAAAAAAAAAAAAAAUL); break;
            case 2: words.Fill(0x5555555555555555UL); break;
            case 3: words.Fill(0xFFFFFFFFFFFFFFFFUL); break;
            default: words.Fill(0UL); break;
        }
    }

    public static long Verify(byte[] block, int mode, ulong seed)
    {
        Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(block.AsSpan());
        long errors = 0;
        switch (mode)
        {
            case 0:
            {
                ulong x = seed | 1UL;
                for (int i = 0; i < words.Length; i++)
                {
                    x ^= x << 13;
                    x ^= x >> 7;
                    x ^= x << 17;
                    if (words[i] != x) errors++;
                }
                break;
            }
            case 1:
                for (int i = 0; i < words.Length; i++) if (words[i] != 0xAAAAAAAAAAAAAAAAUL) errors++;
                break;
            case 2:
                for (int i = 0; i < words.Length; i++) if (words[i] != 0x5555555555555555UL) errors++;
                break;
            case 3:
                for (int i = 0; i < words.Length; i++) if (words[i] != 0xFFFFFFFFFFFFFFFFUL) errors++;
                break;
            default:
                for (int i = 0; i < words.Length; i++) if (words[i] != 0UL) errors++;
                break;
        }
        return errors;
    }

    public static long ComputeTargetBytes(long requestedBytes, ulong totalPhysical, ulong availablePhysical)
    {
        const double maxPhysicalFraction = 0.90;
        const ulong reserveBytes = 1_500_000_000;
        long cap = (long)Math.Min(
            totalPhysical * maxPhysicalFraction,
            Math.Max(0, (double)availablePhysical - reserveBytes));
        cap = Math.Max(cap, MinTargetBytes);
        return Math.Clamp(requestedBytes, MinTargetBytes, cap);
    }
}
