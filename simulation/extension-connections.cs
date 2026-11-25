using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace WorldPerformance
{
    // While a crafting station's window is open, the game restarts the connection beam of every extension in reach each
    // physics step, and hovering an extension or holding a station extension's building ghost near its station restarts
    // it each frame. Each restart cancels the beam's pending StopConnectionEffect call and schedules a new one, and the
    // engine finds the call to cancel by searching every pending delayed call in the game, about 80 us for each extension
    // in each step at a large base. Here a restart keeps the beam's stop time instead, and a check placed just before the engine's
    // own delayed calls, in each physics step and each frame, stops each beam whose time has come with the game's own
    // StopConnectionEffect, so a beam goes in the same step or frame as natively. Creating and placing the beam, and every
    // direct call to StopConnectionEffect, stay the game's own.
    internal static class ExtensionConnections
    {
        private struct FixedStepStops { }
        private struct FrameStops { }

        private const string StopName = "StopConnectionEffect";
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".extension-connections");
        private static readonly PlayerLoopSystem.UpdateFunction stopDue = StopDue;
        // Each beam's stop time in the game's time, as the engine schedules a delayed call.
        private static readonly Dictionary<StationExtension, double> stops = new Dictionary<StationExtension, double>();
        private static readonly List<StationExtension> due = new List<StationExtension>();
        private static bool keeping = true, scheduledNatively;
        internal static bool Running { get; private set; }
        internal static long Restarts, Stops;
        internal static int Pending => stops.Count;

        // Off schedules the engine's delayed calls as natively; the isolated client switches it for paired measurements.
        internal static bool Keeping
        {
            get => keeping;
            set
            {
                if (keeping == value) return;
                keeping = value;
                if (value) return;
                // The kept stop times become the engine's own delayed calls.
                double now = Time.timeAsDouble;
                foreach (var pair in stops)
                    if (pair.Key) pair.Key.Invoke(StopName, (float)Math.Max(0, pair.Value - now));
                stops.Clear();
                scheduledNatively = true;
            }
        }

        internal static void Start()
        {
            if (Running) return;
            var start = AccessTools.Method(typeof(StationExtension), nameof(StationExtension.StartConnectionEffect), new[] { typeof(Vector3), typeof(float) });
            patches.Patch(start, transpiler: new HarmonyMethod(typeof(ExtensionConnections), nameof(KeepStopTimes)));
            Place(true);
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchOwner();
            Keeping = false;
            keeping = true;
            Place(false);
        }

        // The restart's CancelInvoke and Invoke of StopConnectionEffect become Cancel and Schedule below.
        private static IEnumerable<CodeInstruction> KeepStopTimes(IEnumerable<CodeInstruction> instructions)
        {
            var cancel = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.CancelInvoke), new[] { typeof(string) });
            var invoke = AccessTools.Method(typeof(MonoBehaviour), nameof(MonoBehaviour.Invoke), new[] { typeof(string), typeof(float) });
            var code = Transpilers.CallInstead(instructions, instruction => instruction.Calls(cancel), AccessTools.Method(typeof(ExtensionConnections), nameof(Cancel)), 1,
                "StationExtension.StartConnectionEffect no longer cancels its stop.");
            return Transpilers.CallInstead(code, instruction => instruction.Calls(invoke), AccessTools.Method(typeof(ExtensionConnections), nameof(Schedule)), 1,
                "StationExtension.StartConnectionEffect no longer schedules its stop.");
        }

        private static void Cancel(MonoBehaviour behaviour, string name)
        {
            // A beam restarted after the isolated client switched this off and on may still have the engine's call pending.
            if (!keeping || name != StopName || !(behaviour is StationExtension extension) || scheduledNatively && !stops.ContainsKey(extension))
                behaviour.CancelInvoke(name);
        }

        private static void Schedule(MonoBehaviour behaviour, string name, float time)
        {
            if (!keeping || name != StopName || !(behaviour is StationExtension extension)) { behaviour.Invoke(name, time); return; }
            stops[extension] = Time.timeAsDouble + time;
            Restarts++;
        }

        // Just before the engine's delayed calls, which run the scheduled stops once the game's time reaches them.
        private static void StopDue()
        {
            if (stops.Count == 0) return;
            double now = Time.timeAsDouble;
            foreach (var pair in stops)
                if (!pair.Key || pair.Value <= now) due.Add(pair.Key);
            if (due.Count == 0) return;
            try
            {
                foreach (var extension in due)
                {
                    stops.Remove(extension);
                    if (!extension) continue;
                    Stops++;
                    extension.StopConnectionEffect();
                }
            }
            catch (Exception error)
            {
                Plugin.Fallback("ExtensionConnections stopped after an error: " + error.GetBaseException().Message);
                Stop();
            }
            finally { due.Clear(); }
        }

        // Puts the checks before the physics step's and the frame's delayed calls, or takes them out, in the loop as other
        // mods have left it.
        private static void Place(bool placed)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var phases = loop.subSystemList;
            void Put(Type phase, Type delayed, Type mark)
            {
                int index = Array.FindIndex(phases, system => system.type == phase);
                if (index < 0) throw new InvalidOperationException("The player loop has no " + phase.Name + " phase.");
                var list = (phases[index].subSystemList ?? new PlayerLoopSystem[0]).Where(system => system.type != mark).ToList();
                if (placed)
                {
                    int position = list.FindIndex(system => system.type == delayed);
                    if (position < 0) throw new InvalidOperationException("The player loop has no " + delayed.Name + ".");
                    list.Insert(position, new PlayerLoopSystem { type = mark, updateDelegate = stopDue });
                }
                phases[index].subSystemList = list.ToArray();
            }
            Put(typeof(FixedUpdate), typeof(FixedUpdate.ScriptRunDelayedFixedFrameRate), typeof(FixedStepStops));
            Put(typeof(Update), typeof(Update.ScriptRunDelayedDynamicFrameRate), typeof(FrameStops));
            PlayerLoop.SetPlayerLoop(loop);
        }
    }
}
