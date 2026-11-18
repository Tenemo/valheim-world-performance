using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Epic Loot writes its bounty ledger with every world save, on the game's main thread; while Windows took a snapshot of the
    // disk, creating the file held up the server for 2.6 s. Epic Loot now writes the ledger into memory, and a background
    // thread writes those bytes whole beside the old file before they take its place. A write that fails is retried every
    // second for 15 s, past the disk snapshots that held the server for up to 4.7 s, and then dropped with an error, so a
    // ledger that can never be written does not hold up stopping, logging out or quitting. Those wait for the last write.
    internal static class BountyLedgerWrites
    {
        private const int DrainMilliseconds = 60000;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, byte[]> Waiting = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static Thread thread;
        private static readonly Dictionary<string, DateTime> retryAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        // When the waiting snapshot's first write failed.
        private static readonly Dictionary<string, DateTime> failingSince = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(15);

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            var bounties = plugin.Instance.GetType().Assembly.GetType("EpicLoot.Adventure.BountyManagmentSystem");
            var save = bounties == null ? null : AccessTools.Method(bounties, "SaveBounties");
            var shutdown = AccessTools.Method(typeof(ZNet), "Shutdown");
            if (save == null || shutdown == null) throw new InvalidOperationException("Epic Loot's bounty ledger save changed.");
            harmony.Patch(save, transpiler: new HarmonyMethod(typeof(BountyLedgerWrites), nameof(WriteLater)));
            harmony.Patch(shutdown, postfix: new HarmonyMethod(typeof(BountyLedgerWrites), nameof(Drain)));
            Application.quitting += Drain;
            thread = new Thread(Run) { IsBackground = true, Name = "Bounty ledger writes" };
            thread.Start();
        }

        private static IEnumerable<CodeInstruction> WriteLater(IEnumerable<CodeInstruction> instructions)
        {
            var create = AccessTools.Method(typeof(File), nameof(File.Create), new[] { typeof(string) });
            var code = instructions.ToList();
            var calls = code.Where(instruction => instruction.Calls(create)).ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("Epic Loot's bounty ledger file write changed.");
            calls[0].operand = AccessTools.Method(typeof(BountyLedgerWrites), nameof(Open));
            return code;
        }

        private static Stream Open(string path) => new Ledger(path);

        // Collects what Epic Loot writes and hands it over when Epic Loot closes the file.
        private sealed class Ledger : MemoryStream
        {
            private readonly string path;
            private bool handed;

            internal Ledger(string path) => this.path = path;

            protected override void Dispose(bool disposing)
            {
                if (disposing && !handed)
                {
                    handed = true;
                    lock (Gate) { Waiting[path] = ToArray(); retryAt.Remove(path); failingSince.Remove(path); Monitor.PulseAll(Gate); }
                }
                base.Dispose(disposing);
            }
        }

        private static void Run()
        {
            while (true)
            {
                string path; byte[] bytes;
                lock (Gate)
                {
                    while (true)
                    {
                        while (Waiting.Count == 0) Monitor.Wait(Gate);
                        var now = DateTime.UtcNow;
                        var next = Waiting.FirstOrDefault(entry => !retryAt.TryGetValue(entry.Key, out var due) || due <= now);
                        if (next.Key != null) { path = next.Key; bytes = next.Value; break; }
                        Monitor.Wait(Gate, TimeSpan.FromSeconds(1));
                    }
                }
                try
                {
                    string temporary = path + ".tmp";
                    File.WriteAllBytes(temporary, bytes);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    lock (Gate)
                    {
                        if (ReferenceEquals(Waiting[path], bytes)) Waiting.Remove(path);
                        retryAt.Remove(path);
                        failingSince.Remove(path);
                        Monitor.PulseAll(Gate);
                    }
                }
                catch (Exception error)
                {
                    lock (Gate)
                    {
                        if (!ReferenceEquals(Waiting[path], bytes)) continue;
                        var now = DateTime.UtcNow;
                        if (!failingSince.TryGetValue(path, out var since))
                        {
                            failingSince[path] = since = now;
                            Plugin.Log.LogError("Retrying bounty ledger save (" + path + "): " + error.Message);
                        }
                        if (now - since >= GiveUpAfter)
                        {
                            Plugin.Log.LogError("Bounty ledger save abandoned after " + GiveUpAfter.TotalSeconds + " s (" + path + "): " + error.Message);
                            Waiting.Remove(path);
                            retryAt.Remove(path);
                            failingSince.Remove(path);
                            Monitor.PulseAll(Gate);
                            continue;
                        }
                        retryAt[path] = now.AddSeconds(1);
                    }
                }
            }
        }

        private static void Drain()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(DrainMilliseconds);
            lock (Gate)
                while (Waiting.Count > 0)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0 || !Monitor.Wait(Gate, left))
                    {
                        Plugin.Log.LogError("Bounty ledger writes did not finish before shutdown: " + string.Join(", ", Waiting.Keys));
                        return;
                    }
                }
        }
    }
}
