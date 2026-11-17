using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // Mono's file watcher, which mods use to reload their config files when they change, lists every watched folder every
    // 750 ms on a thread of its own, once for each watcher. The pack's 28 watchers, 20 of them over the whole config folder
    // and its subfolders, made 1.5 to 1.8 MB of garbage a second in the hidden client, about a quarter of what Tenemo's game
    // allocates, and so about one garbage collection in four. Within one polling pass the watchers now share each folder's
    // listing, existence checks and file times, which the first watcher of the pass reads as before; a change made during
    // a pass reaches every watcher by the next one, at most 750 ms later. Answers are kept for half a second at most.
    internal static class ConfigWatcherListings
    {
        // The isolated test client sets false to compare the native polling.
        internal static bool Sharing = true;
        private const int KeptMilliseconds = 500;
        private static readonly Type watcher = typeof(FileSystemWatcher).Assembly.GetType("System.IO.DefaultWatcher");
        [ThreadStatic] private static Pass pass;

        // The answers of one polling pass on one thread.
        private sealed class Pass
        {
            internal readonly HashSet<object> Seen = new HashSet<object>();
            internal readonly Dictionary<string, bool> Folders = new Dictionary<string, bool>();
            internal readonly Dictionary<string, bool> Files = new Dictionary<string, bool>();
            internal readonly Dictionary<string, string[]> Subfolders = new Dictionary<string, string[]>();
            internal readonly Dictionary<(string, string), string[]> Entries = new Dictionary<(string, string), string[]>();
            internal readonly Dictionary<string, DateTime> Created = new Dictionary<string, DateTime>();
            internal readonly Dictionary<string, DateTime> Written = new Dictionary<string, DateTime>();
            internal int Started;

            internal void Clear()
            {
                Seen.Clear(); Folders.Clear(); Files.Clear(); Subfolders.Clear(); Entries.Clear(); Created.Clear(); Written.Clear();
                Started = Environment.TickCount;
            }
        }

        private static Pass Current()
        {
            if (!Sharing) return null;
            if (pass == null) { pass = new Pass(); pass.Clear(); }
            else if (Environment.TickCount - pass.Started > KeptMilliseconds) pass.Clear();
            return pass;
        }

        // A Mono without this watcher, or with other members, keeps its own polling.
        private static bool Available()
        {
            if (watcher != null && AccessTools.DeclaredMethod(watcher, "UpdateDataAndDispatch") != null &&
                AccessTools.DeclaredMethod(watcher, "DoFiles") != null && AccessTools.DeclaredMethod(watcher, "IterateAndModifyFilesData") != null) return true;
            Plugin.Fallback("Mono's file watcher changed, so config folders are listed once per watcher.");
            return false;
        }

        // A watcher polled twice begins a new pass.
        [HarmonyPatch]
        private static class Polling
        {
            private static bool Prepare() => Available();

            private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(watcher, "UpdateDataAndDispatch");

            private static void Prefix(object data)
            {
                var current = Current();
                if (current != null && !current.Seen.Add(data)) { current.Clear(); current.Seen.Add(data); }
            }
        }

        [HarmonyPatch]
        private static class Folders
        {
            private static bool Prepare() => Available();

            private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(watcher, "DoFiles");

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Replace(instructions,
                (AccessTools.Method(typeof(Directory), nameof(Directory.Exists), new[] { typeof(string) }), nameof(FolderExists)),
                (AccessTools.Method(typeof(File), nameof(File.Exists), new[] { typeof(string) }), nameof(FileExists)),
                (AccessTools.Method(typeof(Directory), nameof(Directory.GetDirectories), new[] { typeof(string) }), nameof(GetSubfolders)),
                (AccessTools.Method(typeof(Directory), nameof(Directory.GetFileSystemEntries), new[] { typeof(string), typeof(string) }), nameof(GetEntries)));
        }

        [HarmonyPatch]
        private static class Times
        {
            private static bool Prepare() => Available();

            private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(watcher, "IterateAndModifyFilesData");

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Replace(instructions,
                (AccessTools.Method(typeof(File), nameof(File.GetCreationTime), new[] { typeof(string) }), nameof(GetCreated)),
                (AccessTools.Method(typeof(File), nameof(File.GetLastWriteTime), new[] { typeof(string) }), nameof(GetWritten)));
        }

        // Every call to each named method goes to the shared version; a method the game no longer calls fails the patch.
        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, params (MethodInfo native, string shared)[] calls)
        {
            var code = instructions.ToList();
            foreach (var (native, shared) in calls)
            {
                var found = code.Where(instruction => instruction.Calls(native)).ToList();
                if (found.Count == 0) throw new InvalidOperationException("Mono's file watcher no longer calls " + native.Name + ".");
                foreach (var instruction in found)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(ConfigWatcherListings), shared);
                }
            }
            return code;
        }

        private static bool FolderExists(string path)
        {
            var current = Current();
            if (current == null) return Directory.Exists(path);
            if (!current.Folders.TryGetValue(path, out bool exists)) current.Folders[path] = exists = Directory.Exists(path);
            return exists;
        }

        private static bool FileExists(string path)
        {
            var current = Current();
            if (current == null) return File.Exists(path);
            if (!current.Files.TryGetValue(path, out bool exists)) current.Files[path] = exists = File.Exists(path);
            return exists;
        }

        private static string[] GetSubfolders(string path)
        {
            var current = Current();
            if (current == null) return Directory.GetDirectories(path);
            if (!current.Subfolders.TryGetValue(path, out var folders)) current.Subfolders[path] = folders = Directory.GetDirectories(path);
            return folders;
        }

        private static string[] GetEntries(string path, string pattern)
        {
            var current = Current();
            if (current == null) return Directory.GetFileSystemEntries(path, pattern);
            if (!current.Entries.TryGetValue((path, pattern), out var entries)) current.Entries[(path, pattern)] = entries = Directory.GetFileSystemEntries(path, pattern);
            return entries;
        }

        private static DateTime GetCreated(string path)
        {
            var current = Current();
            if (current == null) return File.GetCreationTime(path);
            if (!current.Created.TryGetValue(path, out var time)) current.Created[path] = time = File.GetCreationTime(path);
            return time;
        }

        private static DateTime GetWritten(string path)
        {
            var current = Current();
            if (current == null) return File.GetLastWriteTime(path);
            if (!current.Written.TryGetValue(path, out var time)) current.Written[path] = time = File.GetLastWriteTime(path);
            return time;
        }
    }
}
