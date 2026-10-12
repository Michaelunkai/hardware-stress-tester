using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace StressLab.Monitoring;

public sealed record SystemInfo(string CpuName, int LogicalCores, string GpuName, double RamTotalGb);

public static class SystemProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    public static (ulong total, ulong available) GetMemory()
    {
        try
        {
            var status = new MemoryStatusEx();
            return GlobalMemoryStatusEx(status) ? (status.ullTotalPhys, status.ullAvailPhys) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static string GetCpuName()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", null) as string ?? "CPU";
        }
        catch
        {
            return "CPU";
        }
    }

    public static SystemInfo Probe(string? gpuName = null)
    {
        var (total, _) = GetMemory();
        return new SystemInfo(GetCpuName(), Environment.ProcessorCount, gpuName ?? "GPU", total / 1073741824.0);
    }
}
