using System.Runtime.InteropServices;

namespace StressLab.Monitoring;

/// <summary>
/// FORCE MAX power side: duplicates the active power scheme, pins the processor to
/// 100% min/max AC state with Aggressive boost mode, and activates the duplicate.
/// Restore() re-activates the original scheme and deletes the duplicate, leaving the
/// system exactly as it was found. All calls are best-effort; failures degrade to a
/// log message, never a crash.
/// </summary>
public static class PowerBooster
{
    private static Guid? _duplicatedScheme;
    private static Guid _originalScheme;

    // SUB_PROCESSOR (plain static — the P/Invoke calls take these by ref)
    private static Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static Guid ProcThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static Guid ProcThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    public static bool Engaged => _duplicatedScheme is not null;

    /// <summary>Activates a boosted copy of the current power scheme. Returns a note for the log.</summary>
    public static (bool ok, string note) Engage()
    {
        try
        {
            if (_duplicatedScheme is not null) return (true, "already engaged");

            uint err = PowerGetActiveScheme(IntPtr.Zero, out IntPtr activePtr);
            if (err != 0 || activePtr == IntPtr.Zero)
                return (false, $"could not read the active power scheme (error {err})");
            _originalScheme = Marshal.PtrToStructure<Guid>(activePtr);
            Marshal.FreeCoTaskMem(activePtr);

            Guid duplicate = Guid.NewGuid();
            IntPtr dupPtr = Marshal.AllocHGlobal(16);
            Marshal.StructureToPtr(duplicate, dupPtr, false);
            err = PowerDuplicateScheme(IntPtr.Zero, ref _originalScheme, out IntPtr newSchemePtr);
            if (err != 0)
            {
                Marshal.FreeHGlobal(dupPtr);
                return (false, $"could not duplicate the power scheme (error {err}) — running with the existing plan");
            }
            _duplicatedScheme = Marshal.PtrToStructure<Guid>(newSchemePtr);
            Marshal.FreeCoTaskMem(newSchemePtr);
            Marshal.FreeHGlobal(dupPtr);

            Guid scheme = _duplicatedScheme.Value;
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref ProcThrottleMin, 100);
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref ProcThrottleMax, 100);
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubProcessor, ref PerfBoostMode, 2); // Aggressive

            err = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (err != 0)
                return (false, $"could not activate the boosted scheme (error {err})");
            return (true, "min/max processor state 100% + Aggressive boost mode");
        }
        catch (Exception ex)
        {
            return (false, $"power boost unavailable: {ex.Message}");
        }
    }

    /// <summary>Restores the original scheme and deletes the boosted duplicate.</summary>
    public static void Restore()
    {
        try
        {
            if (_duplicatedScheme is null) return;
            Guid dup = _duplicatedScheme.Value;
            PowerSetActiveScheme(IntPtr.Zero, ref _originalScheme);
            PowerDeleteScheme(IntPtr.Zero, ref dup);
            _duplicatedScheme = null;
        }
        catch
        {
            // restore is best-effort; never crash over a power plan
        }
    }

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerDuplicateScheme(IntPtr RootPowerKey, ref Guid SourceSchemeGuid, out IntPtr DestinationSchemeGuid);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerDeleteScheme(IntPtr RootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerSetActiveScheme(IntPtr RootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);
}
