using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace WorldPerformance
{
    // Epic Loot and Equipment and Quick Slots check every config file they watch for edits every two seconds, on the game's
    // main thread. While Windows takes a snapshot of the disk such a check waits, and the server froze for up to 4.7 s. A
    // background thread now checks the watched files twice a second and the periodic check reads its latest answers; the
    // mods' own checks after a reload or after writing a file still read the disk, and their answers win over older ones.
    // An edit to a config file is noticed up to half a second later.
    internal static class ConfigFileStats
    {
        private const int IntervalMilliseconds = 500;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Reading> Readings = new Dictionary<string, Reading>(StringComparer.Ordinal);
        private static Thread thread;

        private sealed class Reading
        {
            internal DateTime Written;
            internal long Length, Started;
        }

        internal static void Initialize(PerformancePatches harmony)
        {
            foreach (string id in new[] { PluginIds.EpicLoot, PluginIds.EquipmentAndQuickSlots })
            {
                if (!PluginIds.TryGet(id, out var plugin)) continue;
                var reloader = plugin.Instance.GetType().Assembly.GetType("Common.ConfigFileReloader");
                var stat = reloader == null ? null : AccessTools.Method(reloader, "Stat", new[] { typeof(string) });
                var update = reloader == null ? null : AccessTools.Method(reloader, "Update");
                if (stat?.ReturnType != typeof(ValueTuple<DateTime, long>) || update == null)
                    throw new InvalidOperationException(plugin.Metadata.Name + "'s config file checks changed.");
                harmony.Patch(update, transpiler: new HarmonyMethod(typeof(ConfigFileStats), nameof(UseLatest)));
                harmony.Patch(stat, prefix: new HarmonyMethod(typeof(ConfigFileStats), nameof(BeforeStat)),
                    postfix: new HarmonyMethod(typeof(ConfigFileStats), nameof(AfterStat)));
                if (thread != null) continue;
                thread = new Thread(Run) { IsBackground = true, Name = "Config file checks" };
                thread.Start();
            }
        }

        private static IEnumerable<CodeInstruction> UseLatest(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var stat = AccessTools.Method(original.DeclaringType, "Stat", new[] { typeof(string) });
            var code = instructions.ToList();
            var calls = code.Where(instruction => instruction.Calls(stat)).ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("The periodic config file check changed.");
            calls[0].operand = AccessTools.Method(typeof(ConfigFileStats), nameof(Latest));
            return code;
        }

        // The mods' own Stat: the file's last write time and length, or no time and -1 when it is missing or unreadable.
        private static ValueTuple<DateTime, long> Read(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? (file.LastWriteTimeUtc, file.Length) : (default(DateTime), -1L);
            }
            catch { return (default(DateTime), -1L); }
        }

        private static ValueTuple<DateTime, long> Latest(string path)
        {
            lock (Gate) if (Readings.TryGetValue(path, out var known)) return (known.Written, known.Length);
            long started = Stopwatch.GetTimestamp();
            var answer = Read(path);
            Store(path, answer, started);
            return answer;
        }

        private static void BeforeStat(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void AfterStat(string __0, ValueTuple<DateTime, long> __result, long __state) => Store(__0, __result, __state);

        // An answer replaces only one read before it began.
        private static void Store(string path, ValueTuple<DateTime, long> answer, long started)
        {
            lock (Gate)
            {
                if (Readings.TryGetValue(path, out var known) && known.Started > started) return;
                Readings[path] = new Reading { Written = answer.Item1, Length = answer.Item2, Started = started };
            }
        }

        private static void Run()
        {
            while (true)
            {
                Thread.Sleep(IntervalMilliseconds);
                string[] paths;
                lock (Gate) paths = Readings.Keys.ToArray();
                foreach (string path in paths)
                {
                    long started = Stopwatch.GetTimestamp();
                    Store(path, Read(path), started);
                }
            }
        }
    }
}
