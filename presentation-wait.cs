using System;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;

namespace WorldPerformance
{
    // The engine's first system of a frame holds the main thread until the frame it just handed to the render thread has been
    // presented, so the two threads mostly take turns: 3.9 ms of a 21.9 ms frame at Tenemo's base, more in fixed steps that fit
    // into each frame. Just before the frame's rendering, the same system waits only for the frame before and the threads
    // overlap. The system also advances the game's time, so rendering, its camera callbacks and end-of-frame coroutines read
    // the next frame's number and time; simulation reads them as before.
    //
    // A frame's input is read before that later wait, so any time still spent in it is added latency. The wait moves only
    // while the main thread is the frame's limit: when it takes less than half of most frames at the start of the frame, and
    // goes back when it takes more than an eighth of most frames in its later place, which the render thread, the graphics
    // card, vsync or a frame limit cause. It then stays at the start for half a minute, so a frame rate held by vsync never
    // keeps it switching. The isolated client holds it at the start for paired measurements.
    internal static class PresentationWait
    {
        private struct WaitStarted { }
        private struct WaitEnded { }

        private const int WindowFrames = 30, RetrySeconds = 30;

        private static readonly PlayerLoopSystem.UpdateFunction started = Started, ended = Ended;
        private static long waitStart, lastEnd, retryAt;
        private static int frames, votes;
        internal static bool Running { get; private set; }
        internal static bool Moving { get; private set; } = true;
        internal static bool Moved { get; private set; }
        internal static long Moves { get; private set; }
        internal static long Returns { get; private set; }
        internal static long WaitTicks { get; private set; }
        internal static string Failure { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || SystemInfo.renderingThreadingMode != RenderingThreadingMode.LegacyJobified)
                    throw new NotSupportedException("it needs Direct3D 11 with legacy graphics jobs, not " + SystemInfo.graphicsDeviceType + " with " + SystemInfo.renderingThreadingMode + ".");
                Place(false, true);
                Running = true;
            }
            catch (Exception error) { Fail(error); }
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            try { Place(false, false); }
            catch (Exception error) { Fail(error); }
        }

        internal static void SetMoving(bool moving)
        {
            Moving = moving;
            frames = votes = 0;
            retryAt = 0;
            if (!moving && Moved && Running) Move(false);
        }

        private static void Fail(Exception error)
        {
            Failure = error.Message;
            Moving = false;
            Plugin.Log.LogWarning("The presentation wait stays at the start of the frame: " + error.Message);
        }

        private static void Started() => waitStart = Stopwatch.GetTimestamp();

        private static void Ended()
        {
            long now = Stopwatch.GetTimestamp(), wait = now - waitStart, interval = now - lastEnd;
            lastEnd = now;
            WaitTicks += wait;
            if (!Moving) return;
            if (Moved ? wait * 8 > interval : wait * 2 < interval) votes++;
            if (++frames < WindowFrames) return;
            bool change = votes * 2 > frames;
            frames = votes = 0;
            if (!change || Loading()) return;
            if (Moved)
            {
                Move(false);
                retryAt = now + RetrySeconds * Stopwatch.Frequency;
            }
            else if (now >= retryAt) Move(true);
        }

        // A loading screen's frames say nothing about play, so a trip leaves the wait where it stands.
        private static bool Loading()
        {
            var screen = Hud.instance ? Hud.instance.m_loadingScreen : null;
            return screen && screen.gameObject.activeSelf;
        }

        private static void Move(bool moved)
        {
            try
            {
                Place(moved, true);
                if (moved) Moves++;
                else Returns++;
            }
            catch (Exception error) { Fail(error); }
        }

        // Takes the wait and its marks from wherever they are and puts them back as asked, in the loop as other mods have left it.
        private static void Place(bool moved, bool marked)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var phases = loop.subSystemList;
            PlayerLoopSystem? wait = null;
            for (int phase = 0; phase < phases.Length; phase++)
            {
                var systems = phases[phase].subSystemList;
                if (systems == null) continue;
                int found = Array.FindIndex(systems, system => system.type == typeof(TimeUpdate.WaitForLastPresentationAndUpdateTime));
                if (found >= 0) wait = systems[found];
                phases[phase].subSystemList = systems.Where(system => system.type != typeof(TimeUpdate.WaitForLastPresentationAndUpdateTime)
                    && system.type != typeof(WaitStarted) && system.type != typeof(WaitEnded)).ToArray();
            }
            if (wait == null) throw new InvalidOperationException("The player loop has no wait for the last presentation.");
            int target = Array.FindIndex(phases, phase => phase.type == (moved ? typeof(PostLateUpdate) : typeof(TimeUpdate)));
            if (target < 0) throw new InvalidOperationException("The player loop has no " + (moved ? nameof(PostLateUpdate) : nameof(TimeUpdate)) + " phase.");
            var list = (phases[target].subSystemList ?? new PlayerLoopSystem[0]).ToList();
            int position = moved ? list.FindIndex(system => system.type == typeof(PostLateUpdate.FinishFrameRendering)) : list.Count;
            if (position < 0) throw new InvalidOperationException("The player loop has no frame rendering step.");
            list.Insert(position, wait.Value);
            if (marked)
            {
                list.Insert(position + 1, new PlayerLoopSystem { type = typeof(WaitEnded), updateDelegate = ended });
                list.Insert(position, new PlayerLoopSystem { type = typeof(WaitStarted), updateDelegate = started });
            }
            phases[target].subSystemList = list.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
            Moved = moved;
        }
    }
}
