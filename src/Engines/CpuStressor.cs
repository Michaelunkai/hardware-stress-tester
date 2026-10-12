using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace StressLab.Engines;

public sealed class CpuStressor : IStressEngine
{
    public enum KernelKind
    {
        FmaFlops = 0,
        IntegerMix = 1,
        PrimeSieve = 2
    }

    private readonly object _lock = new();
    private EngineStats _stats = new();
    private Thread[]? _workers;
    private volatile bool _stop;
    private int _generation;
    private long _flops;
    private long _intOps;
    private long _primesFound;
    private long _scanned;
    private long _startTimestamp = Stopwatch.GetTimestamp();

    public KernelKind Kernel { get; set; } = KernelKind.FmaFlops;
    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount);
    public int LoadPercent { get; set; } = 100;

    public string Name => "CPU";

    // ---------------------------------------------------------------- lifecycle

    public void Start()
    {
        Thread[]? previous;
        lock (_lock)
        {
            if (_stats.State == EngineState.Running) return;
            previous = _workers;
            _workers = null;
        }
        // A previous generation dies within ~one burst (25 ms); join it outside the
        // stats lock so no UI snapshot can ever stall behind this wait.
        if (previous != null)
            foreach (var t in previous) t.Join(1500);

        lock (_lock)
        {
            _stop = false;
            _flops = _intOps = _primesFound = _scanned = 0;
            _startTimestamp = Stopwatch.GetTimestamp();
            _stats = new EngineStats { State = EngineState.Running, StatusLine = "Warming up…" };

            var kernel = Kernel;
            var threads = Math.Max(1, Threads);
            var load = Math.Clamp(LoadPercent, 1, 100);
            int generation = ++_generation;
            _workers = new Thread[threads];
            for (int i = 0; i < threads; i++)
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        switch (kernel)
                        {
                            case KernelKind.FmaFlops: FmaWorker(load, generation); break;
                            case KernelKind.IntegerMix: IntegerWorker(load, generation); break;
                            default: PrimeWorker(load, generation); break;
                        }
                    }
                    catch (Exception ex)
                    {
                        SetError(ex);
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    Name = $"StressLab-CPU-{generation}-{i}"
                };
                _workers[i] = t;
                t.Start();
            }
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
        Thread[]? workers;
        lock (_lock)
        {
            if (_stats.State != EngineState.Running) return;
            workers = _workers;
            _workers = null;
        }
        if (workers != null)
            foreach (var t in workers) t.Join(3000);
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
                TotalWork = _stats.TotalWork,
                ElapsedSeconds = _stats.ElapsedSeconds,
                StatusLine = _stats.StatusLine,
                ErrorText = _stats.ErrorText
            };
        }
    }

    public void Dispose() => RequestStop();

    private bool Alive(int generation) => !_stop && generation == Volatile.Read(ref _generation);

    private void SetError(Exception ex)
    {
        lock (_lock)
        {
            _stats.State = EngineState.Error;
            _stats.ErrorText = ex.Message;
            _stats.StatusLine = "Error: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- stats

    private void RefreshStats(EngineState state)
    {
        double elapsed = Math.Max(0.001, (Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency);
        long work = Kernel switch
        {
            KernelKind.FmaFlops => _flops,
            KernelKind.IntegerMix => _intOps,
            _ => _scanned
        };
        _stats.TotalWork = Kernel == KernelKind.PrimeSieve ? _primesFound : work;
        _stats.ElapsedSeconds = elapsed;
        _stats.WorkPerSecond = elapsed > 0.2 ? work / elapsed : 0;
        _stats.State = state;
        _stats.StatusLine = Kernel switch
        {
            KernelKind.FmaFlops => $"{_stats.WorkPerSecond / 1e9:0.00} GFLOPS (FP64, est.)",
            KernelKind.IntegerMix => $"{_stats.WorkPerSecond / 1e6:0.0} M mix-ops/s",
            _ => $"{_primesFound:N0} primes found · {_stats.WorkPerSecond / 1e6:0.0} M candidates/s"
        };
    }

    // ---------------------------------------------------------------- kernels

    private void FmaWorker(int load, int generation)
    {
        const int n = 2048;
        var a = new double[n];
        var b = new double[n];
        for (int i = 0; i < n; i++) { a[i] = 1.0 + i * 0.001; b[i] = 0.999; }
        double sink = 0;
        long localFlops = 0;
        long burstTicks = (long)(Stopwatch.Frequency * Governor.BurstMs / 1000.0);

        // SIMD paths push far more FLOPs (and heat) than scalar JIT code; the generic
        // vector APIs carry software fallbacks, so every machine gets the widest path
        // its CPU supports.
        bool has512 = Vector512.IsHardwareAccelerated;
        bool has256 = Vector256.IsHardwareAccelerated;
        ref double aRef = ref MemoryMarshal.GetArrayDataReference(a);
        ref double bRef = ref MemoryMarshal.GetArrayDataReference(b);

        while (Alive(generation))
        {
            long burstEnd = Stopwatch.GetTimestamp() + burstTicks;
            do
            {
                for (int r = 0; r < 16; r++)
                {
                    if (has512)
                    {
                        for (int i = 0; i < n; i += 8)
                        {
                            var va = Vector512.LoadUnsafe(ref aRef, (nuint)i);
                            var vb = Vector512.LoadUnsafe(ref bRef, (nuint)i);
                            Vector512.FusedMultiplyAdd(va, vb, Vector512.Create(1.0000001))
                                     .StoreUnsafe(ref aRef, (nuint)i);
                        }
                    }
                    else if (has256)
                    {
                        for (int i = 0; i < n; i += 4)
                        {
                            var va = Vector256.LoadUnsafe(ref aRef, (nuint)i);
                            var vb = Vector256.LoadUnsafe(ref bRef, (nuint)i);
                            Vector256.FusedMultiplyAdd(va, vb, Vector256.Create(1.0000001))
                                     .StoreUnsafe(ref aRef, (nuint)i);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < n; i += 8)
                        {
                            double v0 = a[i], v1 = a[i + 1], v2 = a[i + 2], v3 = a[i + 3];
                            double v4 = a[i + 4], v5 = a[i + 5], v6 = a[i + 6], v7 = a[i + 7];
                            v0 = v0 * b[i] + 1.0000001; v1 = v1 * b[i + 1] + 1.0000001;
                            v2 = v2 * b[i + 2] + 1.0000001; v3 = v3 * b[i + 3] + 1.0000001;
                            v4 = v4 * b[i + 4] + 1.0000001; v5 = v5 * b[i + 5] + 1.0000001;
                            v6 = v6 * b[i + 6] + 1.0000001; v7 = v7 * b[i + 7] + 1.0000001;
                            a[i] = v0; a[i + 1] = v1; a[i + 2] = v2; a[i + 3] = v3;
                            a[i + 4] = v4; a[i + 5] = v5; a[i + 6] = v6; a[i + 7] = v7;
                        }
                    }
                }
                localFlops += 16L * n * 2; // 16 rounds × n FMA lanes × 2 flops
            }
            while (Alive(generation) && Stopwatch.GetTimestamp() < burstEnd);
            sink += a[0];
            Interlocked.Add(ref _flops, localFlops);
            localFlops = 0;
            int sleep = Governor.ComputeSleepMs(load, Governor.BurstMs);
            if (sleep > 0) Thread.Sleep(sleep);
        }
        if (sink == double.NegativeInfinity) Console.Write(sink); // keep the accumulator alive
    }

    private void IntegerWorker(int load, int generation)
    {
        ulong x = (ulong)Environment.CurrentManagedThreadId * 0x9E3779B97F4A7C15UL + 0x1234567UL;
        long localOps = 0;
        long burstTicks = (long)(Stopwatch.Frequency * Governor.BurstMs / 1000.0);
        while (Alive(generation))
        {
            long burstEnd = Stopwatch.GetTimestamp() + burstTicks;
            do
            {
                for (int r = 0; r < 4096; r++)
                {
                    x ^= x << 13;
                    x ^= x >> 7;
                    x ^= x << 17;
                    x *= 0x9E3779B97F4A7C15UL;
                    x ^= x >> 29;
                }
                localOps += 4096;
            }
            while (Alive(generation) && Stopwatch.GetTimestamp() < burstEnd);
            Interlocked.Add(ref _intOps, localOps);
            localOps = 0;
            int sleep = Governor.ComputeSleepMs(load, Governor.BurstMs);
            if (sleep > 0) Thread.Sleep(sleep);
        }
        GC.KeepAlive(x);
    }

    private void PrimeWorker(int load, int generation)
    {
        int[] basePrimes = SieveOfEratosthenes(1_000_000);
        const int segmentLength = 1_000_000;
        long segmentStart = 2;
        long localPrimes = 0, localScanned = 0;
        long burstTicks = (long)(Stopwatch.Frequency * Governor.BurstMs / 1000.0);
        while (Alive(generation))
        {
            long burstEnd = Stopwatch.GetTimestamp() + burstTicks;
            do
            {
                localPrimes += CountPrimesInSegment(segmentStart, segmentLength, basePrimes);
                localScanned += segmentLength;
                segmentStart += segmentLength;
            }
            while (Alive(generation) && Stopwatch.GetTimestamp() < burstEnd);
            Interlocked.Add(ref _primesFound, localPrimes);
            Interlocked.Add(ref _scanned, localScanned);
            localPrimes = 0;
            localScanned = 0;
            int sleep = Governor.ComputeSleepMs(load, Governor.BurstMs);
            if (sleep > 0) Thread.Sleep(sleep);
        }
    }

    // ---------------------------------------------------------------- testable math

    public static int[] SieveOfEratosthenes(int limit)
    {
        if (limit < 2) return Array.Empty<int>();
        var composite = new bool[limit + 1];
        var primes = new List<int>();
        for (int i = 2; i <= limit; i++)
        {
            if (composite[i]) continue;
            primes.Add(i);
            for (long m = (long)i * i; m <= limit; m += i) composite[m] = true;
        }
        return primes.ToArray();
    }

    public static int CountPrimesInSegment(long segmentStart, int length, int[] basePrimes)
    {
        var composite = new bool[length];
        long hi = segmentStart + length;
        foreach (int p in basePrimes)
        {
            long pp = (long)p * p;
            if (pp >= hi) break;
            long start = Math.Max(pp, (segmentStart + p - 1) / p * p);
            for (long m = start; m < hi; m += p) composite[m - segmentStart] = true;
        }
        int count = 0;
        for (int i = 0; i < length; i++)
            if (!composite[i] && segmentStart + i >= 2) count++;
        return count;
    }
}
