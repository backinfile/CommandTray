using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CommandTray.Services;

internal static class OwnedProcessTree
{
    internal sealed record Identity(int ProcessId, long StartTimeUtcTicks, int Depth);

    public static IReadOnlyList<Identity> Capture(int rootProcessId)
    {
        var parents = SnapshotParents();
        var depths = new Dictionary<int, int> { [rootProcessId] = 0 };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (pid, parent) in parents)
            {
                if (!depths.ContainsKey(pid) && depths.TryGetValue(parent, out var depth))
                {
                    depths[pid] = depth + 1;
                    changed = true;
                }
            }
        }

        var result = new List<Identity>();
        foreach (var (pid, depth) in depths.OrderByDescending(x => x.Value))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                result.Add(new Identity(pid, process.StartTime.ToUniversalTime().Ticks, depth));
            }
            catch { /* process exited during the snapshot */ }
        }
        return result;
    }

    public static void TerminateStillMatching(IEnumerable<Identity> identities)
    {
        foreach (var identity in identities.OrderByDescending(x => x.Depth))
        {
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks) continue;
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static Dictionary<int, int> SnapshotParents()
    {
        using var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot.IsInvalid) return [];
        var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        var result = new Dictionary<int, int>();
        if (!Process32First(snapshot, ref entry)) return result;
        do { result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
        while (Process32Next(snapshot, ref entry));
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private sealed class SnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SnapshotHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(SnapshotHandle snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(SnapshotHandle snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
