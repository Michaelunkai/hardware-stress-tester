using StressLab.Engines;
using Xunit;

namespace StressLab.Tests;

public class DiskStressorTests
{
    [Fact]
    public void Engine_writes_and_verifies_with_zero_errors()
    {
        using var disk = new DiskStressor { TargetBytes = 512L << 20, LoadPercent = 100, VerifyEnabled = true };
        disk.Start();
        Thread.Sleep(1500);
        disk.Stop();
        var stats = disk.Snapshot();
        Assert.True(stats.TotalWork > 0,
            $"expected bytes exercised on disk — state={stats.State}, error={stats.ErrorText ?? "none"}");
        Assert.Equal(0, disk.ErrorCount);
    }

    [Fact]
    public void Target_computation_keeps_a_free_space_reserve()
    {
        // The engine clamps against real free space; on any machine the result must at
        // least reach the 512 MB floor and never exceed the request.
        long target = DiskStressor.ComputeTargetBytes(4L << 30);
        Assert.True(target >= 512L << 20, "must allow at least the 512 MB floor");
        Assert.True(target <= 4L << 30, "must never exceed the requested size");
    }
}
