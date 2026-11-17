using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using HarmonyLib.Public.Patching;

namespace WorldPerformance
{
    // Progression Fix 1.21.2 adds its patches one at a time, and Harmony rebuilds and recompiles a method's whole wrapper
    // after each one: 1,552 rebuilds of 1,017 methods in its start, about 7.7 ms each in the hidden test client. A method
    // it patches again in its start now keeps the wrapper built for its first patch there until that start ends, and then
    // gets one wrapper with all its patches, built from the same patch list the last of those rebuilds would read. Its start
    // calls none of these methods in between on a client, where this runs. New callers own that boundary with contract 1;
    // the log watcher supports only older releases that predate the explicit scope. Harmony's internals remain checked.
    internal static class RepeatedPatchRebuilds
    {
        internal const string ProgressionFixId = "valheim.epiclootprogressionfix";
        private const string ProgressionFixStart = "Loading [EpicLoot Progression Fix 1.21.2]";
        private const string UpdatedProgressionFixStart = "Loading [EpicLoot Progression Fix 1.21.3]";
        private const string BountyProgressionFixStart = "Loading [EpicLoot Progression Fix 1.22.0]";
        private const string BountyRewardProgressionFixStart = "Loading [EpicLoot Progression Fix 1.22.1]";
        private static readonly System.Version Supported = new System.Version(2, 9, 0, 0);
        private static readonly Dictionary<MethodBase, MethodInfo> built = new Dictionary<MethodBase, MethodInfo>();
        private static readonly List<MethodBase> pending = new List<MethodBase>();
        private static readonly HashSet<MethodBase> pendingSet = new HashSet<MethodBase>();
        private static Func<MethodBase, PatchInfo, MethodInfo> updateWrapper;
        private static Action<MethodBase, MethodInfo> addReplacementOriginal;
        private static object locker;
        private static bool active, seen, flushing, done, initialized;
        private static BatchScope scope;

        // How many rebuilds waited for the end of Progression Fix's start; the isolated test client reads it.
        internal static int Deferred { get; private set; }
        internal static int Completed { get; private set; }

        internal static IDisposable Begin(int contractVersion)
        {
            if (contractVersion != 1) throw new NotSupportedException("Unsupported startup patch batch contract: " + contractVersion + ".");
            if (!initialized) throw new InvalidOperationException("Startup patch batching did not initialize.");
            if (active || scope != null) throw new InvalidOperationException("A startup patch batch is already active.");
            seen = active = true;
            return scope = new BatchScope();
        }

        private sealed class BatchScope : IDisposable
        {
            private bool disposed;
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { Flush(); Completed++; }
                finally { scope = null; }
            }
        }

        internal static void Start(Harmony harmony)
        {
            var version = typeof(Harmony).Assembly.GetName().Version;
            if (version != Supported)
                throw new InvalidOperationException("Harmony " + version + " is not the supported " + Supported + ".");
            var update = AccessTools.Method("HarmonyLib.PatchFunctions:UpdateWrapper", new[] { typeof(MethodBase), typeof(PatchInfo) });
            var add = AccessTools.Method(typeof(PatchManager), "AddReplacementOriginal", new[] { typeof(MethodBase), typeof(MethodInfo) });
            var lockField = AccessTools.Field(typeof(PatchProcessor), "locker");
            if (update == null || add == null || lockField == null) throw new InvalidOperationException("Harmony's wrapper rebuild changed.");
            updateWrapper = AccessTools.MethodDelegate<Func<MethodBase, PatchInfo, MethodInfo>>(update);
            addReplacementOriginal = AccessTools.MethodDelegate<Action<MethodBase, MethodInfo>>(add);
            locker = lockField.GetValue(null);
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(RepeatedPatchRebuilds), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(RepeatedPatchRebuilds), nameof(Postfix)));
            Logger.Listeners.Add(new StartWatcher());
            initialized = true;
        }

        private static bool Prefix(MethodBase original, ref MethodInfo __result)
        {
            if (!active || flushing || original == null || !built.TryGetValue(original, out var replacement)) return true;
            if (pendingSet.Add(original)) pending.Add(original);
            Deferred++;
            __result = replacement;
            return false;
        }

        private static void Postfix(MethodBase original, MethodInfo __result)
        {
            if (active && !flushing && original != null && __result != null && !built.ContainsKey(original)) built[original] = __result;
        }

        // Builds each waiting method's wrapper once, from its patch list as it stands now.
        private static void Flush()
        {
            active = false;
            flushing = true;
            Exception failure = null;
            try
            {
                lock (locker)
                {
                    foreach (var original in pending)
                    {
                        try { addReplacementOriginal(original, updateWrapper(original, original.ToPatchInfo())); }
                        catch (Exception error)
                        {
                            failure = error;
                            Plugin.Fallback("Progression Fix's patches of " + original.FullDescription() + " could not be built: " + error.Message);
                        }
                    }
                }
            }
            finally
            {
                flushing = false;
                pending.Clear();
                pendingSet.Clear();
                built.Clear();
            }
            if (failure != null && scope != null) throw new InvalidOperationException("Startup patch batch could not finish.", failure);
        }

        // The chainloader logs each plugin's start before it runs and its own end after the last one.
        private sealed class StartWatcher : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                if (done || scope != null || eventArgs.Source.SourceName != "BepInEx") return;
                string text = eventArgs.Data?.ToString();
                if (text == null) return;
                if (text.StartsWith("Loading [", StringComparison.Ordinal))
                {
                    Flush();
                    active = text.StartsWith(ProgressionFixStart, StringComparison.Ordinal) || text.StartsWith(UpdatedProgressionFixStart, StringComparison.Ordinal) || text.StartsWith(BountyProgressionFixStart, StringComparison.Ordinal) || text.StartsWith(BountyRewardProgressionFixStart, StringComparison.Ordinal);
                    seen |= active;
                }
                else if (text.StartsWith("Chainloader startup complete", StringComparison.Ordinal))
                {
                    Flush();
                    done = true;
                    if (!seen && Chainloader.PluginInfos.TryGetValue(ProgressionFixId, out var plugin))
                        Plugin.Fallback("Progression Fix " + plugin.Metadata.Version + " did not request the startup patch batch contract and is not a supported legacy release.");
                }
            }

            public void Dispose() { }
        }
    }
}
