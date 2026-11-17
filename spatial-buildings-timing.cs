using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // Reports for the hidden test client, and the per-frame budget that times and limits main-thread work.
    internal static partial class SpatialBuildings
    {
        // For the isolated client: what the renderer recorded for a natural object, its groups and parts.
        internal static string Describe(ZNetView view, Vector3 camera)
        {
            if (!natural.TryGetValue(view, out var member)) return "not a natural member";
            var text = new System.Text.StringBuilder();
            text.Append("owned " + member.Owned + ", away " + member.Away + ", planted " + member.Planted + ", dirty " + member.Dirty + ", signature " + member.Signature + "\n");
            for (int index = 0; index < member.Details.Length; index++)
            {
                var detail = member.Details[index];
                text.Append("detail " + index + ": group " + (detail.Group ? detail.Group.name + " enabled " + detail.Group.enabled + " size " + detail.Group.size + " scale " + detail.Group.transform.lossyScale : "gone") +
                    ", recorded size " + detail.Size + ", reference " + detail.Reference + (detail.Group ? " now " + detail.Group.transform.TransformPoint(detail.Group.localReferencePoint) : "") +
                    ", thresholds " + string.Join("/", detail.Thresholds) + ", level " + detail.Level + ", computed " + Level(detail, camera, out _) + ", owned " + detail.Owned + ", merging " + detail.Merging +
                    ", active " + member.Active.Contains(detail) + ", fades " + detail.Fades + "\n");
            }
            foreach (var part in member.Parts)
            {
                var renderer = member.Renderers[part.Renderer];
                text.Append("part " + (renderer ? renderer.name : "gone") + " submesh " + part.Submesh + ": detail " + part.Detail + ", levels " + part.Levels + ", material " + (part.Material ? part.Material.name : "none") +
                    ", merged " + member.Merged.Contains(part) + ", shown " + Shown(member, part) + ", shadows " + part.Key.Shadows + ", forced off " + (renderer && renderer.forceRenderingOff) + "\n");
            }
            return text.ToString();
        }

        internal static bool Settled => !Running || pending.Count == 0 && pendingNatural.Count == 0 && active.Count == 0 && builds.Count == 0 && received.Count == 0;

        // Merged members the sky depth camera draws at other detail levels than the main camera, counted when asked.
        internal static int SkyLevels => skyCamera ? Members().Count(member => member.Owned && !member.Away && OtherSkyLevels(member)) : 0;

        // Frames with renderer work, its main-thread time in stopwatch ticks, its worst frame, the job results applied, the
        // processor cycles counted and the last frame whose counted work saw the collector's events.
        internal static long[] Timing() => new[] { frames, mainTicks, maximumTicks, jobTicks, (long)mainCycles, collectorFrame };

        internal static bool Owns(GameObject root)
        {
            if (!Running || !root) return false;
            Member member = null;
            if (root.TryGetComponent<WearNTear>(out var wear)) pieces.TryGetValue(wear, out member);
            else if (root.TryGetComponent<ZNetView>(out var view)) natural.TryGetValue(view, out member);
            return member != null && member.Owned && !member.Away;
        }

        internal static string Report()
        {
            double Milliseconds(long ticks) => Math.Round(ticks * 1000d / Stopwatch.Frequency, 3);
            var batches = cells.Values.SelectMany(cell => cell.Batches).Where(batch => batch.Mesh).ToList();
            return "Spatial building renderer: " + (Running ? Native ? "native (switched for measurement)" : "running" : "stopped") +
                "; pieces " + pieces.Count + " (" + pieces.Values.Count(member => member.Owned && !member.Away) + " merged, " + pending.Count + " registering)" +
                "; natural objects " + natural.Count + " (" + natural.Values.Count(member => member.Owned && !member.Away) + " merged, " + pendingNatural.Count + " registering)" +
                "; " + near.Count + " members drawn natively near a detail level change or while a tree moves, " + SkyLevels +
                " drawn at other detail levels for the sky depth camera; cells " + cells.Count +
                "; meshes " + batches.Count + " with " + batches.Sum(batch => (long)batch.Mesh.vertexCount) + " vertices in " +
                Math.Round(batches.Sum(GraphicsBytes) / 1048576d, 1) + " MB of graphics memory (new meshes wait from " + Math.Round(MemoryLimit / 1048576d) + " MB), " +
                Math.Round(batches.Sum(batch => (long)(batch.Total - batch.Indices) * (batch.Mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4)) / 1048576d, 1) +
                " MB of it in drawn regions, whose drawn ranges hold " +
                Math.Round(100d * batches.Sum(batch => (long)Math.Max(0, batch.SpanEnd - batch.SpanFirst)) / Math.Max(1L, batches.Sum(batch => (long)batch.Indices)), 1) + "% of their indices; slots " +
                batches.Sum(batch => batch.Live.Count(slot => !slot.member.Removed && slot.member.Version == slot.version)) +
                "; source meshes " + sources.Count + " (" + sources.Values.Count(source => source.Readable) + " ready, " + sources.Values.Count(source => source.Failed) + " unreadable)" +
                "; rebuilds " + rebuilds + ", swaps " + swaps + ", compactions " + compacted + (drawnWhole > 0 ? ", " + drawnWhole + " drawn whole beyond their drawn regions" : "") +
                "; shadowed lights drawing their merged slots " + shadowLights.Count + " (" + lightsTaken + " taken over; " +
                shadowLights.Sum(state => state.Ranges.Count(range => range.Submesh < 0)) + " of the " + shadowLights.Sum(state => state.Ranges.Count) +
                " merged meshes they reach draw whole drawn regions, " + shadowLights.Sum(state => state.Ranges.Count(range => range.Whole)) + " of them without room for a copy)" +
                ", merged detail level switches " + switches + ", member detail-level evaluations " +
                evaluations + "; handed back " + Describe(handedBack) +
                "; excluded " + Describe(excluded) + "; natural objects excluded " + Describe(naturalExcluded) +
                "; main thread " + (frames == 0 ? 0 : Milliseconds(mainTicks / frames)) + " ms a frame on average, at most " + Milliseconds(maximumTicks) +
                " ms; work budget " + Math.Round(budgetMilliseconds, 2) + " ms a frame, " + waitedFrames + " frames waiting after long ones; applying job results " + Milliseconds(jobTicks) + " ms, slowest " + Milliseconds(slowestApply) +
                " ms with " + slowestApplyVertices + " vertices; slowest registration " +
                Milliseconds(slowestRegistration) + " ms (" + (slowestPrefab ?? "none") + "); by phase " + string.Join(", ", phaseNames.Select((name, index) =>
                    name + " " + Milliseconds(phaseTicks[index]) + " ms (worst " + Milliseconds(phaseMaximum[index]) + " ms" + (phaseCollected[index] ? " with a garbage collection" : "") + ")")) +
                (Failure != null ? "; failed: " + Failure : "");
        }

        // A merged mesh's vertex and index buffers; their processor copies are released once uploaded.
        private static long GraphicsBytes(Batch batch)
        {
            var mesh = batch.Mesh;
            long bytes = 0;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++) bytes += (long)mesh.GetVertexBufferStride(stream) * mesh.vertexCount;
            return bytes + (long)batch.Total * (mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4);
        }

        private static string Describe(Dictionary<string, long> counts) => counts.Count == 0 ? "none" :
            string.Join(", ", counts.OrderByDescending(entry => entry.Value).Select(entry => entry.Key + " " + entry.Value));

        private static void Count(Dictionary<string, long> counts, string reason) => counts[reason] = counts.TryGetValue(reason, out long value) ? value + 1 : 1;

        // Main-thread time the renderer spends in hooks and its frame work, in total, in its worst frame and by phase.
        private const int Registration = 0, DetailChecks = 1, Applies = 2, Planning = 3, Requests = 4, Readbacks = 5, Uploads = 6, SkyDepth = 7, VisualHooks = 8,
            PropertyHooks = 9, RemovalHooks = 10, ShadowedLights = 11, SkyPreparation = 12, Arrivals = 13, Removals = 14;
        private static readonly string[] phaseNames = { "registration", "detail checks", "applies and swaps", "planning and job starts", "readback requests", "readbacks",
            "slot uploads", "sky depth", "visual hooks", "property hooks", "removal hooks", "shadowed lights", "sky preparation", "arrivals", "removals" };
        private static readonly long[] phaseTicks = new long[phaseNames.Length], phaseMaximum = new long[phaseNames.Length];
        // Whether a garbage collection ran during each phase's worst span.
        private static readonly bool[] phaseCollected = new bool[phaseNames.Length];

        private struct Timed : IDisposable
        {
            private readonly long start, events;
            private readonly ulong cycles;
            private readonly int phase, collections;
            internal Timed(int phase)
            {
                this.phase = phase;
                collections = GC.CollectionCount(0);
                cycles = 0;
                events = 0;
                if (CountCycles)
                {
                    QueryThreadCycleTime(GetCurrentThread(), out cycles);
                    if (CollectorEvents != IntPtr.Zero) events = Marshal.ReadInt64(CollectorEvents);
                }
                start = Stopwatch.GetTimestamp();
            }
            public void Dispose()
            {
                long spent = Stopwatch.GetTimestamp() - start;
                if (CountCycles && QueryThreadCycleTime(GetCurrentThread(), out ulong now))
                {
                    mainCycles += now - cycles;
                    if (CollectorEvents != IntPtr.Zero && Marshal.ReadInt64(CollectorEvents) != events) collectorFrame = Time.frameCount;
                }
                FrameStarted();
                frameTicks += spent;
                mainTicks += spent;
                if (frameTicks > maximumTicks) maximumTicks = frameTicks;
                phaseTicks[phase] += spent;
                if (spent > phaseMaximum[phase]) { phaseMaximum[phase] = spent; phaseCollected[phase] = GC.CollectionCount(0) != collections; }
            }
        }

        // Starts a frame's accounting with its first work. What the renderer did after LateUpdate in the frame before, in the
        // cameras' culling and in hooks, is what later frames' budgets reserve for it, as the most recently seen, decaying.
        private static void FrameStarted()
        {
            if (mainFrame == Time.frameCount) return;
            if (mainFrame == lateFrame) afterReserve = Math.Max(frameTicks - lateTicks, (long)(afterReserve * ReserveDecay));
            mainFrame = Time.frameCount;
            frameTicks = 0;
            frames++;
        }

        [DllImport("kernel32.dll")]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        private static long Ticks(float milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000d);

        // Whether a small budgeted step may start: while the frame's budget lasts.
        private static bool Open() => Stopwatch.GetTimestamp() <= Math.Min(budgetEnd, capEnd);

        // Whether a large budgeted step expected to take the given ticks may start: one that fits in what is left of the
        // frame's budget, or, once a frame, one beyond it that fits within the most the renderer may spend in the frame or a
        // long one in a frame with a long step's share of that left.
        private static bool Admit(long expected)
        {
            long now = Stopwatch.GetTimestamp(), left = capEnd - now;
            if (now + expected <= budgetEnd && expected <= left) return true;
            if (overrun || left < Math.Min(expected, Ticks((FrameMaximum - MinimumReserve) * LongShare))) return false;
            return overrun = true;
        }

        // The most a kind of budgeted step has recently taken per unit of its work, beyond a fixed part counted in units,
        // which tells whether the next one fits. Spans with a garbage collection are left out, and the figure decays with each
        // step, so a slow step is soon forgotten.
        private sealed class StepCost
        {
            private readonly int fixedUnits;
            private double rate;
            internal StepCost(double microseconds, int fixedUnits) { rate = microseconds * Stopwatch.Frequency / 1e6; this.fixedUnits = fixedUnits; }
            internal long Expected(int units) => (long)(rate * (units + fixedUnits));
            internal void Spent(long ticks, int units, bool collected)
            {
                if (!collected) rate = Math.Max(rate * StepDecay, (double)ticks / (units + fixedUnits));
            }
        }

        // Applying a mesh by its vertices, starting a build by its slots, a readable copy by its vertices, finding the lights,
        // dropping void detail-level checks by the queue's entries and making a new mesh's objects.
        private static readonly StepCost applyCost = new StepCost(.025, 8192), buildCost = new StepCost(5, 16), copyCost = new StepCost(.02, 4096),
            findCost = new StepCost(500, 0), cleanupCost = new StepCost(.05, 0), createCost = new StepCost(500, 0);

        private readonly struct Step
        {
            private readonly long start;
            private readonly int collections;
            private Step(long start, int collections) { this.start = start; this.collections = collections; }
            internal static Step Start() => new Step(Stopwatch.GetTimestamp(), GC.CollectionCount(0));
            internal void Done(StepCost cost, int units) => cost.Spent(Stopwatch.GetTimestamp() - start, units, GC.CollectionCount(0) != collections);
        }
    }
}
