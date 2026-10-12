using StressLab.Engines;
using StressLab.Monitoring;
using StressLab.Reporting;
using Xunit;

namespace StressLab.Tests;

public class GovernorTests
{
    [Theory]
    [InlineData(100, 25.0, 0)]
    [InlineData(50, 25.0, 25)]
    [InlineData(90, 25.0, 3)]
    [InlineData(10, 25.0, 225)]
    [InlineData(1, 25.0, 2475)]
    public void SleepMs_follows_duty_cycle(int load, double burstMs, int expected)
        => Assert.Equal(expected, Governor.ComputeSleepMs(load, burstMs));
}

public class CpuKernelTests
{
    [Fact]
    public void Sieve_finds_the_right_number_of_primes()
        => Assert.Equal(168, CpuStressor.SieveOfEratosthenes(1000).Length);

    [Fact]
    public void Segment_counter_counts_primes_below_100()
    {
        int[] basePrimes = CpuStressor.SieveOfEratosthenes(1000);
        Assert.Equal(25, CpuStressor.CountPrimesInSegment(0, 100, basePrimes));
    }

    [Fact]
    public void Segment_counter_matches_sieve_for_first_million()
    {
        int[] basePrimes = CpuStressor.SieveOfEratosthenes(1000);
        int inFirstMillion = CpuStressor.CountPrimesInSegment(2, 1_000_000 - 2, basePrimes);
        Assert.Equal(78498, inFirstMillion);
    }

    [Fact]
    public void Fma_kernel_produces_flops()
    {
        using var cpu = new CpuStressor { Kernel = CpuStressor.KernelKind.FmaFlops, Threads = 2, LoadPercent = 100 };
        cpu.Start();
        Thread.Sleep(300);
        cpu.Stop();
        var stats = cpu.Snapshot();
        Assert.True(stats.TotalWork > 0, "expected flops > 0");
        Assert.True(stats.WorkPerSecond > 0, "expected nonzero throughput");
    }

    [Fact]
    public void Integer_kernel_produces_ops()
    {
        using var cpu = new CpuStressor { Kernel = CpuStressor.KernelKind.IntegerMix, Threads = 2, LoadPercent = 100 };
        cpu.Start();
        Thread.Sleep(250);
        cpu.Stop();
        Assert.True(cpu.Snapshot().TotalWork > 0);
    }

    [Fact]
    public void Load_governor_reduces_throughput()
    {
        using var full = new CpuStressor { Kernel = CpuStressor.KernelKind.IntegerMix, Threads = 2, LoadPercent = 100 };
        full.Start();
        Thread.Sleep(500);
        full.Stop();
        var fullStats = full.Snapshot();

        Thread.Sleep(200);

        using var half = new CpuStressor { Kernel = CpuStressor.KernelKind.IntegerMix, Threads = 2, LoadPercent = 40 };
        half.Start();
        Thread.Sleep(500);
        half.Stop();
        var halfStats = half.Snapshot();

        Assert.True(halfStats.WorkPerSecond < fullStats.WorkPerSecond,
            $"expected governed throughput ({halfStats.WorkPerSecond:0}) < full throughput ({fullStats.WorkPerSecond:0})");
    }
}

public class RingBufferTests
{
    [Fact]
    public void Wraps_and_keeps_latest_values()
    {
        var buffer = new RingBuffer(3);
        foreach (var v in new[] { 1, 2, 3, 4, 5 }) buffer.Add(v);
        Assert.Equal(3, buffer.Count);
        Assert.Equal(3, buffer[0]);
        Assert.Equal(4, buffer[1]);
        Assert.Equal(5, buffer[2]);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var buffer = new RingBuffer(4);
        buffer.Add(9);
        buffer.Reset();
        Assert.Equal(0, buffer.Count);
    }
}

public class RamTests
{
    [Fact]
    public void Fill_then_verify_finds_no_errors()
    {
        var block = new byte[1 << 20];
        RamTester.Fill(block, 0, seed: 12345);
        Assert.Equal(0, RamTester.Verify(block, 0, seed: 12345));
    }

    [Fact]
    public void Verify_detects_injected_corruption()
    {
        var block = new byte[1 << 20];
        RamTester.Fill(block, 0, seed: 999);
        block[777_777] ^= 0x01;
        Assert.True(RamTester.Verify(block, 0, seed: 999) >= 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Constant_patterns_roundtrip(int mode)
    {
        var block = new byte[512 << 10];
        RamTester.Fill(block, mode, seed: 1);
        Assert.Equal(0, RamTester.Verify(block, mode, seed: 1));
    }

    [Fact]
    public void Target_computation_respects_safety_caps()
    {
        ulong total = 32UL * (1UL << 30);
        ulong available = 8UL * (1UL << 30);
        long request = 31L * (1L << 30);

        long target = RamTester.ComputeTargetBytes(request, total, available);
        Assert.True((ulong)target <= available - 1_500_000_000UL, "must keep a reserve vs available RAM");
        Assert.True(target <= total * 0.90, "must stay under 90% of physical RAM");

        long tiny = RamTester.ComputeTargetBytes(1024, total, available);
        Assert.Equal(64L << 20, tiny);
    }

    [Fact]
    public void Engine_exercises_memory_and_reports_zero_errors()
    {
        using var ram = new RamTester { TargetBytes = 96L << 20, VerifyEnabled = true };
        ram.Start();
        Thread.Sleep(700);
        ram.Stop();
        var stats = ram.Snapshot();
        Assert.True(stats.TotalWork > 0, "expected bytes exercised");
        Assert.Equal(0, ram.ErrorCount);
    }
}

public class GpuMathTests
{
    [Fact]
    public void Gflops_estimate_math_is_exact()
    {
        double expected = 100 * 1024.0 * 1024 * 512 * GpuStressor.FlopsPerIteration / 1.0 / 1e9;
        Assert.Equal(expected, GpuStressor.EstimateGflops(100, 1.0), 6);
    }

    [Fact]
    public void Gflops_estimate_is_zero_for_zero_time()
        => Assert.Equal(0, GpuStressor.EstimateGflops(1000, 0));
}

public class WatchdogTests
{
    private static MetricsSnapshot Snapshot(float? cpuTemp = null, float? gpuTemp = null)
        => new() { CpuTempC = cpuTemp, GpuTempC = gpuTemp };

    [Fact]
    public void Trips_when_cpu_temperature_reaches_limit()
    {
        string? trip = ThermalWatchdog.Check(Snapshot(cpuTemp: 92), 90, 85, cpuActive: true, gpuActive: false);
        Assert.NotNull(trip);
        Assert.Contains("CPU", trip);
    }

    [Fact]
    public void Trips_when_gpu_temperature_reaches_limit()
    {
        string? trip = ThermalWatchdog.Check(Snapshot(gpuTemp: 86), 90, 85, cpuActive: false, gpuActive: true);
        Assert.NotNull(trip);
        Assert.Contains("GPU", trip);
    }

    [Fact]
    public void Inactive_engine_never_trips()
        => Assert.Null(ThermalWatchdog.Check(Snapshot(cpuTemp: 99), 90, 85, cpuActive: false, gpuActive: false));

    [Fact]
    public void Missing_temperature_does_not_trip()
        => Assert.Null(ThermalWatchdog.Check(Snapshot(), 90, 85, cpuActive: true, gpuActive: true));

    [Fact]
    public void Below_limit_does_not_trip()
        => Assert.Null(ThermalWatchdog.Check(Snapshot(cpuTemp: 89, gpuTemp: 84), 90, 85, cpuActive: true, gpuActive: true));
}

public class ReportTests
{
    [Fact]
    public void Report_contains_all_sections_and_numbers()
    {
        var record = new SessionRecord
        {
            StartedAt = new DateTime(2026, 10, 12, 20, 0, 0),
            EndedAt = new DateTime(2026, 10, 12, 20, 5, 0),
            DurationSeconds = 300,
            CpuName = "Test CPU",
            GpuName = "Test GPU",
            RamTotalGb = 32,
            CpuEnabled = true,
            GpuEnabled = true,
            RamEnabled = true,
            CpuKernel = "Floating-point FMA (GFLOPS)",
            CpuThreads = 8,
            CpuLoad = 100,
            GpuLoad = 100,
            RamTargetGb = 8,
            RamVerify = true,
            RamErrors = 0,
            CpuTotalWork = 123456789,
            GpuTotalWork = 4242
        };
        record.CpuLoadMetric.Add(99.5);
        record.CpuTemp.Add(78);
        record.GpuTemp.Add(71);
        record.RamThroughput.Add(15000);
        record.Events.Add("[20:03:00] test event");

        string md = ReportBuilder.BuildMarkdown(record);

        Assert.Contains("StressLab", md);
        Assert.Contains("Test CPU", md);
        Assert.Contains("Test GPU", md);
        Assert.Contains("Floating-point FMA", md);
        Assert.Contains("78", md);
        Assert.Contains("123,456,789", md);
        Assert.Contains("test event", md);
        Assert.Contains("no memory errors", md);
    }
}
