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
    // thread writes those bytes whole beside the old file before they take its place. Stopping the server and quitting wait
    // for the last of them.
    internal static class BountyLedgerWrites
    {
        private const int DrainMilliseconds = 60000;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, byte[]> Waiting = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static Thread thread;
        private static bool writing;

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
                    lock (Gate) { Waiting[path] = ToArray(); Monitor.PulseAll(Gate); }
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
                    writing = false;
                    Monitor.PulseAll(Gate);
                    while (Waiting.Count == 0) Monitor.Wait(Gate);
                    var next = Waiting.First();
                    Waiting.Remove(next.Key);
                    path = next.Key; bytes = next.Value; writing = true;
                }
                try
                {
                    string temporary = path + ".tmp";
                    File.WriteAllBytes(temporary, bytes);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                catch (Exception error) { Plugin.Log.LogError("Could not save the bounty ledger (" + path + "): " + error.Message); }
            }
        }

        private static void Drain()
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(DrainMilliseconds);
            lock (Gate)
                while (Waiting.Count > 0 || writing)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0 || !Monitor.Wait(Gate, left))
                    {
                        Plugin.Log.LogError("The bounty ledger was still being written when the game stopped.");
                        return;
                    }
                }
        }
    }
}
