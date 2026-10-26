using System.Runtime.InteropServices;
using Dikte.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Dikte;

/// <summary>What Windows knows about call apps: which ones are using a microphone now, and an app's top process.</summary>
static class Calls
{
    /// <summary>The processes recording from a microphone right now, Dikte left out: a call app among them is in a call.</summary>
    public static List<(string Name, int Pid)> UsingMicrophone()
    {
        var found = new List<(string, int)>();
        int self = Environment.ProcessId;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                int pid = (int)session.GetProcessID;
                if (pid == 0 || pid == self) continue;
                if (Snapshot.NameOf(pid) is { } name) found.Add((name, pid));
            }
        }
        return found;
    }

    /// <summary>
    /// The process whose tree holds all of a call app's sound: the topmost one of its name. Starting from the process
    /// using the microphone when there is one (the call's), else from any process of the app. Null when it is not running.
    /// </summary>
    public static int? RootProcess(string app, int? inCall)
    {
        var all = Snapshot.Take();
        int? Root(int pid)
        {
            for (int hops = 0; hops < 32 && all.TryGetValue(pid, out var p); hops++)
            {
                // The parent is the same app (Electron's helpers are children of the main process): go up.
                if (all.TryGetValue(p.Parent, out var parent) && parent.Parent != pid && string.Equals(parent.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                    pid = p.Parent;
                else return pid;
            }
            return null;
        }
        if (inCall is { } callPid && Root(callPid) is { } root) return root;
        foreach (var (id, p) in all)
            if (MeetingNotes.IsApp(p.Name, app) && Root(id) is { } r) return r;
        return null;
    }

    /// <summary>Every process with its parent, from one Toolhelp snapshot.</summary>
    static class Snapshot
    {
        const uint SnapProcess = 0x2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        // DllImport, not LibraryImport: the entry carries a fixed-length string the source generator cannot marshal.
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        /// <summary>Process id → its parent's id and its name without ".exe".</summary>
        public static Dictionary<int, (int Parent, string Name)> Take()
        {
            var all = new Dictionary<int, (int, string)>();
            var snap = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snap == new IntPtr(-1)) return all;
            try
            {
                var e = new ProcessEntry { dwSize = (uint)Marshal.SizeOf<ProcessEntry>() };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    all[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, Path.GetFileNameWithoutExtension(e.szExeFile));
            }
            finally { CloseHandle(snap); }
            return all;
        }

        public static string? NameOf(int pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return p.ProcessName;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
        }
    }
}
