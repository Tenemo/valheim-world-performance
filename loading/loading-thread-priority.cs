using System;
using System.Runtime.InteropServices;

namespace WorldPerformance
{
    // Every garbage collection stops Unity's asset loading thread and waits until that thread has really stopped. Between
    // loading screens the game's Low background loading priority keeps the thread at the lowest priority, and on a busy machine
    // such a thread waited up to 24 s for a processor with nothing to do, with the whole game waiting behind it.
    // The setting also decides how long a frame may spend finishing loaded assets, about 2 ms at Low and 10 ms at Normal, so
    // the setting stays and only the thread's own priority is held at normal. Unity lowers it again for each load, so each frame
    // checks it, which costs one call.
    internal static class LoadingThreadPriority
    {
        private const string ThreadName = "Loading.PreloadManager";
        private const int Normal = 0;
        private const uint SnapThreads = 4, QueryAndSetLimited = 0x0800 | 0x0400;
        private static IntPtr thread;
        private static bool searched;
        private static int raises;

        // Off leaves the thread to Unity; the isolated client switches it for paired measurements.
        internal static bool Holding { get; set; } = true;
        internal static string Failure { get; private set; }

        internal static string Report() =>
            (thread == IntPtr.Zero ? "not found" : "priority " + GetThreadPriority(thread)) + ", raised " + raises + " times" + (Holding ? "" : ", not holding");

        internal static void Update()
        {
            if (!Holding || Failure != null) return;
            if (!searched)
            {
                searched = true;
                Find();
            }
            if (thread != IntPtr.Zero && GetThreadPriority(thread) < Normal && SetThreadPriority(thread, Normal)) raises++;
        }

        private static void Find()
        {
            try
            {
                var snapshot = CreateToolhelp32Snapshot(SnapThreads, 0);
                if (snapshot == new IntPtr(-1)) throw new InvalidOperationException("no thread list");
                try
                {
                    var entry = new ThreadEntry { Size = (uint)Marshal.SizeOf(typeof(ThreadEntry)) };
                    uint process = GetCurrentProcessId();
                    for (bool more = Thread32First(snapshot, ref entry); more && thread == IntPtr.Zero; more = Thread32Next(snapshot, ref entry))
                    {
                        if (entry.Owner != process) continue;
                        var candidate = OpenThread(QueryAndSetLimited, false, entry.Id);
                        if (candidate == IntPtr.Zero) continue;
                        if (Named(candidate)) thread = candidate;
                        else CloseHandle(candidate);
                    }
                }
                finally { CloseHandle(snapshot); }
                if (thread == IntPtr.Zero) throw new InvalidOperationException(ThreadName + " is not running");
            }
            catch (Exception error) when (error is InvalidOperationException || error is DllNotFoundException || error is EntryPointNotFoundException)
            {
                Failure = error.Message;
                Plugin.Fallback("The asset loading thread keeps its own priority: " + error.Message);
            }
        }

        private static bool Named(IntPtr candidate)
        {
            if (GetThreadDescription(candidate, out var text) < 0 || text == IntPtr.Zero) return false;
            string name = Marshal.PtrToStringUni(text);
            LocalFree(text);
            return name == ThreadName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ThreadEntry
        {
            internal uint Size, Usage, Id, Owner;
            internal int BasePriority, DeltaPriority;
            internal uint Flags;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
        [DllImport("kernel32.dll")] private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll")] private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern int GetThreadPriority(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    }
}
