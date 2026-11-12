using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // How each player's game runs, for the server's connection log: every measurement so far comes from one machine. At each
    // five-second row the server asks every player's game for its frames since the last row, and the game answers with one
    // small message: frame times, physics steps and the share of the frame its main thread waits for presentation, which
    // says whether the processor or the graphics card, vsync or a frame limit sets the pace. Frames behind a loading screen
    // or the menu, or with the window in the background, are counted apart so they never read as slow play. With its first
    // answer, and whenever they change, the game also sends its machine and graphics settings, which the server keeps in a
    // file of their own. A game without this ignores the request, and a game never sends unless its server asked.
    internal static class FrameReports
    {
        internal const string RequestRpc = "WorldPerformance FrameRequest", ReportRpc = "WorldPerformance FrameReport", MachineRpc = "WorldPerformance MachineReport";
        internal const int Protocol = 1;
        private const int Capacity = 8192;

        [Flags] internal enum Excluded : byte { Loading = 1, Menu = 2, Unfocused = 4 }

        // The player's side: the frames since the last request.
        private static readonly float[] times = new float[Capacity];
        private static int played, excludedFrames, over33, over50;
        private static double total, steps;
        private static long waited, waitedFrames, last, lastWait;
        private static double lastFixed;
        private static float longest;
        private static Excluded excluded;
        private static bool measuring, fresh, unmeasured;
        private static int counted;
        private static ZRpc server;
        // Checks can count frames without window focus as played.
        internal static bool CountUnfocused;
        internal static int Sent { get; private set; }
        internal static int Requests { get; private set; }
        internal static int IntervalStart { get; private set; }
        internal static int IntervalEnd { get; private set; }
        internal static string LastReport { get; private set; } = "";
        internal static string[] LastMachine { get; private set; }
        internal static int MachineSent { get; private set; }
        private static bool machineFailed;

        internal const string MachineColumns = "world_performance,processor,logical_processors,memory_mb,graphics_card,graphics_memory_mb,graphics_api,width,height,window_mode,refresh_hz,vsync,frame_limit,preset,render_height,upscaling,vegetation,lod,lights,shadow_quality,point_lights,point_light_shadows,ssao,cloth_quality,simulation_distance,distant_shadows,tessellation,bloom,depth_of_field,motion_blur,chromatic_aberration,sun_shafts,soft_particles,anti_aliasing,anisotropic_textures";
        internal static readonly int MachineCount = MachineColumns.Split(',').Length;

        internal sealed class Report
        {
            internal int request, frames, excludedFrames, over33, over50, frameLimit, vsync;
            internal Excluded excluded;
            internal float mean, median, p95, p99, longest, steps, wait;
        }

        // The server's side: the last answer from each player.
        private static readonly Dictionary<long, Report> reports = new Dictionary<long, Report>();

        internal static void Initialize(PerformancePatches patches) =>
            patches.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(FrameReports), nameof(Connected)));

        private static void Connected(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(ReportRpc, (rpc, package) => Received(peer, package));
                peer.m_rpc.Register<ZPackage>(MachineRpc, (rpc, package) => ReceivedMachine(peer, package));
            }
            else peer.m_rpc.Register<ZPackage>(RequestRpc, Requested);
        }

        // Once a frame from the plugin's update, only after the server has asked. Each frame runs from one update to the
        // next, and a request only moves the boundary between reports, so every frame counts once whichever comes first.
        internal static void Update()
        {
            if (!measuring) return;
            if (!ZNet.instance) { measuring = false; server = null; return; }
            long now = Stopwatch.GetTimestamp(), wait = PresentationWait.WaitTicks;
            double fixedTime = Time.fixedTimeAsDouble;
            long frame = now - last, waitedNow = wait - lastWait;
            double stepped = fixedTime - lastFixed;
            last = now;
            lastWait = wait;
            lastFixed = fixedTime;
            if (fresh) { fresh = false; IntervalStart = Time.frameCount + 1; return; }
            counted = Time.frameCount;
            var state = State();
            if (state != 0)
            {
                excludedFrames++;
                excluded |= state;
                return;
            }
            float milliseconds = (float)(frame * 1000.0 / Stopwatch.Frequency);
            if (played < Capacity) times[played] = milliseconds;
            played++;
            total += milliseconds;
            if (milliseconds > longest) longest = milliseconds;
            if (milliseconds > 33f) over33++;
            if (milliseconds > 50f) over50++;
            steps += Math.Round(stepped / Time.fixedDeltaTime);
            if (PresentationWait.Running) { waited += waitedNow; waitedFrames += frame; }
            else unmeasured = true;
        }

        private static Excluded State()
        {
            Excluded state = 0;
            var player = Player.m_localPlayer;
            var hud = Hud.instance;
            if (!player || player.IsTeleporting() || hud && hud.m_loadingScreen.gameObject.activeSelf) state |= Excluded.Loading;
            if (Menu.IsVisible()) state |= Excluded.Menu;
            if (!CountUnfocused && !Application.isFocused) state |= Excluded.Unfocused;
            return state;
        }

        // A request closes the frames since the last one and starts the next; the first only starts them.
        private static void Requested(ZRpc rpc, ZPackage package)
        {
            if (package.ReadInt() != Protocol) return;
            int request = package.ReadInt();
            Requests++;
            bool continuing = measuring && rpc == server;
            if (continuing) Send(rpc, request);
            SendMachine(rpc, !continuing);
            server = rpc;
            measuring = true;
            // A join can stall long enough for two requests to arrive before the next update; the first frame after a
            // new connection still runs from the last update before it and stays uncounted.
            if (!continuing) fresh = true;
            if (continuing) IntervalStart = counted + 1;
            played = excludedFrames = over33 = over50 = 0;
            total = steps = 0;
            waited = waitedFrames = 0;
            longest = 0f;
            excluded = 0;
            unmeasured = false;
        }

        private static void Send(ZRpc rpc, int request)
        {
            int stored = Math.Min(played, Capacity);
            Array.Sort(times, 0, stored);
            var package = new ZPackage();
            package.Write(Protocol);
            package.Write(request);
            package.Write(played);
            package.Write(excludedFrames);
            package.Write((byte)excluded);
            package.Write(played == 0 ? 0f : (float)(total / played));
            package.Write(Percentile(stored, 0.5f));
            package.Write(Percentile(stored, 0.95f));
            package.Write(Percentile(stored, 0.99f));
            package.Write(longest);
            package.Write(over33);
            package.Write(over50);
            package.Write(played == 0 ? 0f : (float)(steps / played));
            package.Write(unmeasured || waitedFrames == 0 ? -1f : (float)((double)waited / waitedFrames));
            package.Write(Application.targetFrameRate);
            package.Write(QualitySettings.vSyncCount);
            rpc.Invoke(ReportRpc, package);
            Sent++;
            IntervalEnd = counted;
            LastReport = FormattableString.Invariant($"request {request} frames {played} excluded {excludedFrames} {Names(excluded)} mean {(played == 0 ? 0.0 : total / played):F3} median {Percentile(stored, 0.5f):F3} p95 {Percentile(stored, 0.95f):F3} p99 {Percentile(stored, 0.99f):F3} max {longest:F3} over33 {over33} over50 {over50} steps {(played == 0 ? 0.0 : steps / played):F3} wait {(unmeasured || waitedFrames == 0 ? -1.0 : (double)waited / waitedFrames):F4} limit {Application.targetFrameRate} vsync {QualitySettings.vSyncCount} frames {IntervalStart}-{IntervalEnd}");
        }

        // The machine and the graphics settings the player chose, which a moment in the background does not change; the frame
        // reports carry the vsync and frame limit in force. Nothing that names the person or the computer.
        private static string[] Machine()
        {
            var manager = GraphicsSettingsManager.Instance;
            var settings = manager == null ? default(GraphicsSettingsState) : manager.GetCurrentSettingsWithCurrentPresetApplied(false);
            string preset = "";
            if (manager != null)
            {
                int id = manager.CurrentPresetID;
                var configuration = manager.GetCurrentGraphicsModeConfiguration();
                preset = id == GraphicsSettingsManager.c_customPresetId ? "custom"
                    : configuration != null && configuration.TryFindPresetByID(id, out var found) && found.m_type ? found.m_type.NameTextId.Replace("$settings_", "").TrimStart('$') : Count(id);
            }
            string Flag(bool value) => manager == null ? "" : value ? "1" : "0";
            string Level(int value) => manager == null ? "" : Count(value);
            return new[]
            {
                Plugin.Version, SystemInfo.processorType, Count(SystemInfo.processorCount), Count(SystemInfo.systemMemorySize),
                SystemInfo.graphicsDeviceName, Count(SystemInfo.graphicsMemorySize), SystemInfo.graphicsDeviceType.ToString(),
                Count(Screen.width), Count(Screen.height), Screen.fullScreenMode.ToString(),
                Screen.currentResolution.refreshRateRatio.value.ToString("F2", CultureInfo.InvariantCulture),
                Flag(settings.m_presentSettings.m_vsync), Level(settings.m_presentSettings.m_fpsLimit), preset,
                Level(settings.m_target3DResolutionVertical), manager == null ? "" : settings.m_upscalingAlgorithm.ToString(),
                manager == null ? "" : settings.m_vegetation.ToString(), Level(settings.m_lod), Level(settings.m_lights), Level(settings.m_shadowQuality),
                Level(settings.m_pointLights), Level(settings.m_pointLightShadows), Level(settings.m_ssao), Level(settings.m_clothQuality),
                Level(settings.m_simulationDistance), Flag(settings.m_distantShadows), Flag(settings.m_tesselation), Flag(settings.m_bloom),
                Flag(settings.m_depthOfField), Flag(settings.m_motionBlur), Flag(settings.m_chromaticAberration), Flag(settings.m_sunShafts),
                Flag(settings.m_softParticles), Flag(settings.m_antiAliasing), Flag(settings.m_anisotropicTextures)
            };
        }

        // At a new connection and whenever the details change, read at each request.
        private static void SendMachine(ZRpc rpc, bool joined)
        {
            if (machineFailed) return;
            string[] machine;
            try { machine = Machine(); }
            catch (Exception error)
            {
                machineFailed = true;
                Plugin.Fallback("Machine details unavailable: " + error.GetBaseException().Message);
                return;
            }
            if (!joined && LastMachine != null && ((IStructuralEquatable)machine).Equals(LastMachine, StructuralComparisons.StructuralEqualityComparer)) return;
            var package = new ZPackage();
            package.Write(Protocol);
            package.Write(machine.Length);
            foreach (string value in machine) package.Write(value ?? "");
            rpc.Invoke(MachineRpc, package);
            LastMachine = machine;
            MachineSent++;
        }

        private static void ReceivedMachine(ZNetPeer peer, ZPackage package)
        {
            if (package.ReadInt() != Protocol || package.ReadInt() != MachineCount) return;
            var values = new string[MachineCount];
            for (int index = 0; index < values.Length; index++)
            {
                string value = package.ReadString();
                values[index] = value.Length > 128 ? value.Substring(0, 128) : value;
            }
            ConnectionLog.WriteMachine(peer.m_uid, values);
        }

        // The same convention as the server's own frame percentiles.
        private static float Percentile(int stored, float share) => stored == 0 ? 0f : times[Mathf.Clamp((int)(stored * share), 0, stored - 1)];

        internal static string Names(Excluded state)
        {
            if (state == 0) return "";
            var names = new List<string>(3);
            if ((state & Excluded.Loading) != 0) names.Add("loading");
            if ((state & Excluded.Menu) != 0) names.Add("menu");
            if ((state & Excluded.Unfocused) != 0) names.Add("unfocused");
            return string.Join("+", names);
        }

        // The server asks each player at its row; a game without this ignores the request.
        internal static void Ask(ZNetPeer peer, int request)
        {
            if (peer.m_rpc == null || !peer.m_rpc.IsConnected()) return;
            var package = new ZPackage();
            package.Write(Protocol);
            package.Write(request);
            peer.m_rpc.Invoke(RequestRpc, package);
        }

        private static void Received(ZNetPeer peer, ZPackage package)
        {
            if (package.ReadInt() != Protocol) return;
            if (!reports.TryGetValue(peer.m_uid, out var report)) reports[peer.m_uid] = report = new Report();
            report.request = package.ReadInt();
            report.frames = package.ReadInt();
            report.excludedFrames = package.ReadInt();
            report.excluded = (Excluded)package.ReadByte();
            report.mean = package.ReadSingle();
            report.median = package.ReadSingle();
            report.p95 = package.ReadSingle();
            report.p99 = package.ReadSingle();
            report.longest = package.ReadSingle();
            report.over33 = package.ReadInt();
            report.over50 = package.ReadInt();
            report.steps = package.ReadSingle();
            report.wait = package.ReadSingle();
            report.frameLimit = package.ReadInt();
            report.vsync = package.ReadInt();
        }

        internal static bool Answered(long uid, int request) => reports.TryGetValue(uid, out var report) && report.request == request;

        internal const string Columns = "client_frames,client_excluded_frames,client_excluded,client_mean_ms,client_median_ms,client_p95_ms,client_p99_ms,client_max_ms,client_over_33ms,client_over_50ms,client_steps_per_frame,client_wait_share,client_frame_limit,client_vsync";

        // The player's answer to this row's request, or empty columns when none came.
        internal static void Append(StringBuilder row, long uid, int request)
        {
            if (!reports.TryGetValue(uid, out var report) || report.request != request)
            {
                row.Append(",,,,,,,,,,,,,");
                return;
            }
            bool any = report.frames > 0;
            row.Append(Count(report.frames)).Append(',').Append(Count(report.excludedFrames)).Append(',').Append(Names(report.excluded))
                .Append(',').Append(any ? Milliseconds(report.mean) : "").Append(',').Append(any ? Milliseconds(report.median) : "")
                .Append(',').Append(any ? Milliseconds(report.p95) : "").Append(',').Append(any ? Milliseconds(report.p99) : "")
                .Append(',').Append(any ? Milliseconds(report.longest) : "").Append(',').Append(Count(report.over33)).Append(',').Append(Count(report.over50))
                .Append(',').Append(any ? report.steps.ToString("F2", CultureInfo.InvariantCulture) : "")
                .Append(',').Append(any && report.wait >= 0f ? report.wait.ToString("F3", CultureInfo.InvariantCulture) : "")
                .Append(',').Append(report.frameLimit > 0 ? Count(report.frameLimit) : "").Append(',').Append(Count(report.vsync));
        }

        private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Milliseconds(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
