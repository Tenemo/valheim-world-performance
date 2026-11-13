using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace WorldPerformance
{
    [BepInPlugin(Id, "World Performance", Version)]
    // Loads after Better Networking when it is installed, so the pooled compression finds its patches; it is not required.
    [BepInDependency(PooledCompression.BetterNetworking, BepInDependency.DependencyFlags.SoftDependency)]
    // Loads after Network Performance System when it is installed, so this mod sees which of the two owns each
    // mechanism they share; it is not required.
    [BepInDependency(ForeignOwnership.NetworkPerformance, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.QuickStack, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.DropThat, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.AzuCraftyBoxes, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.BetterUi, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.ItemStacks, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.MultiUserChest, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.FastLink, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.EpicLoot, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.EquipmentAndQuickSlots, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.WieldWhileSwimming, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.AzuClock, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.BetterBeehives, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.ComfortMe, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.FedoHud, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.NoSmokeStayLit, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.TameRidingOverhaul, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.Treadwell, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "tenemo.WorldPerformance";
        public const string Version = "1.0.1";
        // Contract 1: call on the startup thread, do not execute methods with pending patches inside the scope,
        // and dispose before using them. Disposal completes all pending wrappers, including after an exception.
        public static IDisposable BeginStartupPatchBatch(int contractVersion) => RepeatedPatchRebuilds.Begin(contractVersion);
        internal static BepInEx.Logging.ManualLogSource Log { get; private set; }
        internal static ConfigEntry<int> ServerSendRate, UploadBudget;
        internal static ConfigEntry<bool> ConnectionLogging;
        public static bool TransferredFixesRunning => TransferredPatches.Running;
        public static string TransferredFixesFailure => TransferredPatches.Failure;
        // What fell back to the game's own behavior; mod-compatibility fails when this is not empty.
        public static string StartFailure { get; private set; }
        public static bool IntroLogosSkipped => IntroLogos.Skipped;
        // Whether the spatial building renderer still waits for the first world on a client with graphics; see Awake.
        private static bool rendererPending;

        // The shipped feature; the isolated client switches it off and on for paired measurements.
        public static bool PauseUnseenEffects { get => UnseenEffects.Paused; set => UnseenEffects.SetPaused(value); }
        public static string UnseenEffectsReport() => UnseenEffects.Report();
        public static ParticleSystem[] UnseenEffectsManaged() => UnseenEffects.Managed();
        public static string UnseenEffectsFailure => UnseenEffects.Failure;
        // The shipped feature; the isolated client switches it off and on for checks and paired measurements.
        public static bool FarFlameStandIns { get => FarFlames.Enabled; set => FarFlames.SetEnabled(value); }
        public static string FarFlamesReport() => FarFlames.Report();
        public static string FarFlamesFailure => FarFlames.Failure;
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        public static bool HoldLoadingThread { get => LoadingThreadPriority.Holding; set => LoadingThreadPriority.Holding = value; }
        public static string LoadingThreadReport() => LoadingThreadPriority.Report();
        public static string LoadingThreadFailure => LoadingThreadPriority.Failure;
        // The shipped feature; the isolated client switches it off and on for checks and paired measurements.
        public static bool SmallFarShadows { get => WorldPerformance.SmallFarShadows.Enabled; set => WorldPerformance.SmallFarShadows.SetEnabled(value); }
        public static string SmallFarShadowsReport() => WorldPerformance.SmallFarShadows.Report();

        // The shipped feature; the isolated client switches it off and on for checks and paired measurements.
        public static bool MovePresentationWait { get => PresentationWait.Moving; set => PresentationWait.SetMoving(value); }
        public static bool PresentationWaitRunning => PresentationWait.Running;
        public static bool PresentationWaitMoved => PresentationWait.Moved;
        public static long PresentationWaitTicks => PresentationWait.WaitTicks;
        public static long[] PresentationWaitCounts() => new[] { PresentationWait.Moves, PresentationWait.Returns };
        // The frame reports and machine details this game sends when its server asks; checks can count frames without window
        // focus as played.
        public static bool FrameReportsCountUnfocused { get => FrameReports.CountUnfocused; set => FrameReports.CountUnfocused = value; }
        public static string FrameReportsLast => FrameReports.LastReport;
        public static int[] FrameReportsCounts() => new[] { FrameReports.Requests, FrameReports.Sent, FrameReports.IntervalStart, FrameReports.IntervalEnd, FrameReports.MachineSent };
        public static string FrameReportsMachine => FrameReports.LastMachine == null ? "" : FrameReports.MachineColumns + "\n" + string.Join("\n", FrameReports.LastMachine);
        public static string PresentationWaitFailure => PresentationWait.Failure;

        // Shipped with the paused effects; the isolated client switches each off and on for paired measurements.
        public static bool CacheDisplayMode { get => DisplayMode.Cached; set => DisplayMode.Cached = value; }
        public static bool DisplayModeRunning => DisplayMode.Running;
        public static long DisplayModeQueries => DisplayMode.Queries;
        public static bool SkipRestingFloaters { get => RestingFloaters.Skipping; set => RestingFloaters.Skipping = value; }
        public static bool RestingFloatersRunning => RestingFloaters.Running;
        public static long RestingFloatersSkipped => RestingFloaters.Skipped;
        public static bool PauseSilencedLoops { get => SilencedLoops.Pausing; set => SilencedLoops.Pausing = value; }
        public static bool SilencedLoopsRunning => SilencedLoops.Running;
        public static bool CacheStaticGround { get => StaticGround.Caching; set => StaticGround.Caching = value; }
        public static bool StaticGroundRunning => StaticGround.Running;
        public static long[] StaticGroundCounts() => new[] { StaticGround.Kept, StaticGround.Cast, StaticGround.Forgotten, StaticGround.Answers };
        public static bool PauseRestingDoors { get => RestingDoors.Pausing; set => RestingDoors.Pausing = value; }
        public static int RestingDoorsPaused => RestingDoors.Paused;
        public static string RestingDoorsFailure => RestingDoors.Failure;
        public static bool ShareWaterTime { get => SharedWaterTime.Sharing; set => SharedWaterTime.Sharing = value; }
        public static bool SharedWaterTimeRunning => SharedWaterTime.Running;
        public static int[] SharedWaterTimeCounts() => new[] { SharedWaterTime.Surfaces, SharedWaterTime.Copies };
        public static bool LeanRecordUpdates { get => WorldPerformance.LeanRecordUpdates.Lean; set => WorldPerformance.LeanRecordUpdates.Lean = value; }
        public static bool LeanRecordUpdatesRunning => WorldPerformance.LeanRecordUpdates.Running;
        public static long[] LeanRecordUpdatesCounts() => new[] { WorldPerformance.LeanRecordUpdates.Written, WorldPerformance.LeanRecordUpdates.Read, WorldPerformance.LeanRecordUpdates.Batches };
        public static string LeanRecordUpdatesFailure => WorldPerformance.LeanRecordUpdates.Failure;
        public static bool LeanActiveAreaTests { get => ActiveAreaTests.Lean; set => ActiveAreaTests.Lean = value; }
        public static bool ActiveAreaTestsRunning => ActiveAreaTests.Running;
        public static long ActiveAreaTestsCount => ActiveAreaTests.Tests;
        public static bool LeanPackageFloats { get => PackageFloats.Lean; set => PackageFloats.Lean = value; }
        public static bool PackageFloatsRunning => PackageFloats.Running;
        public static long PackageFloatsCount => PackageFloats.Writes;
        public static bool LeanInventoryScreen { get => InventoryScreen.Lean; set => InventoryScreen.Lean = value; }
        public static bool PoolCompression { get => PooledCompression.Pooling; set => PooledCompression.Pooling = value; }
        public static bool PooledCompressionRunning => PooledCompression.Running;
        public static bool PooledCompressionKeepsSendPackage => PooledCompression.KeepsSendPackage;
        public static long[] PooledCompressionCounts() => new[] { PooledCompression.Sends, PooledCompression.Receives, PooledCompression.Fallbacks };
        public static string PooledCompressionFailure => PooledCompression.Failure;
        public static bool ReuseMapData { get => MapDataPacking.Reusing; set => MapDataPacking.Reusing = value; }
        public static bool PackMapDataOnWorker { get => MapDataPacking.Deferring; set => MapDataPacking.Deferring = value; }
        public static bool MapDataPackingRunning => MapDataPacking.Running;
        // 0 until the worker's bytes are checked against the game's, 1 when they matched, -1 when they did not.
        public static int MapDataPackerVerified => MapDataPacking.Verified;
        public static int MapDataSavesWaiting => MapDataPacking.Waiting;
        public static long[] MapDataPackingCounts() => new[] { MapDataPacking.Reused, MapDataPacking.Packed, MapDataPacking.Deferred, MapDataPacking.WorkerPacked, MapDataPacking.Failed };
        public static bool DeferAssetUnload { get => DeferredAssetUnload.Deferring; set => DeferredAssetUnload.Deferring = value; }
        public static bool DeferredAssetUnloadRunning => DeferredAssetUnload.Running;
        public static bool AssetUnloadDue => DeferredAssetUnload.Due;
        public static bool InNaturalPause => DeferredAssetUnload.NaturalPause();
        public static long[] DeferredAssetUnloadCounts() => new[] { DeferredAssetUnload.Deferred, DeferredAssetUnload.Paused, DeferredAssetUnload.Overdue };
        public static bool HoldSnappingRenderMeshes { get => SnappingRenderMeshes.Holding; set => SnappingRenderMeshes.Holding = value; }
        public static bool SnappingRenderMeshesRunning => SnappingRenderMeshes.Running;
        public static int SnappingRenderMeshesWaiting => SnappingRenderMeshes.Waiting;
        public static long[] SnappingRenderMeshesCounts() => new[] { SnappingRenderMeshes.Held, SnappingRenderMeshes.Rebuilt };
        public static bool CacheTerrainColours { get => TerrainRebuilds.Colouring; set => TerrainRebuilds.Colouring = value; }
        public static bool BakeTerrainColliders { get => TerrainRebuilds.Baking; set => TerrainRebuilds.Baking = value; }
        public static bool TerrainRebuildsRunning => TerrainRebuilds.Running;
        public static string TerrainRebuildsFailure => TerrainRebuilds.Failure;
        public static long[] TerrainRebuildsCounts() => new[] { TerrainRebuilds.Coloured, TerrainRebuilds.Baked };
        public static bool StageDistantTerrain { get => StagedDistantTerrain.Staging; set => StagedDistantTerrain.Staging = value; }
        public static bool StagedDistantTerrainRunning => StagedDistantTerrain.Running;
        public static string StagedDistantTerrainFailure => StagedDistantTerrain.Failure;
        public static long[] StagedDistantTerrainCounts() => new[] { StagedDistantTerrain.Staged, StagedDistantTerrain.Shown };
        public static bool HoldCollectionBallast { get => CollectionBallast.Holding; set => CollectionBallast.Holding = value; }
        public static bool CollectionBallastRunning => CollectionBallast.Running;
        public static int CollectionBallastMegabytes => CollectionBallast.Megabytes;
        public static bool ZoneOccupied(Vector2s sector) => ZoneExpiryChecks.Occupied(ZNetScene.instance, sector);
        public static bool CollisionCallbacksReused => CollisionCallbacks.Reusing;
        public static string CollisionCallbacksFailure => CollisionCallbacks.Failure;
        public static bool SkipBiomeBorderWarning { get => BiomeBorderWarning.Skipping; set => BiomeBorderWarning.Skipping = value; }
        public static bool LeanInputHints { get => InputHintRefresh.Lean; set => InputHintRefresh.Lean = value; }
        public static string RestingCreaturesFailure => RestingCreatures.Failure;
        public static bool PauseIdleEffects { get => IdleEffects.Pausing; set => IdleEffects.Pausing = value; }
        public static bool PauseRestingItemSparkles { get => RestingItemSparkles.Pausing; set => RestingItemSparkles.Pausing = value; }
        public static int[] RestingItemSparklesCounts() => new[] { RestingItemSparkles.Count, RestingItemSparkles.PausedCount };
        public static string RestingItemSparklesFailure => RestingItemSparkles.Failure;
        public static int[] IdleEffectsCounts() => new[] { IdleEffects.PausedSystems, IdleEffects.PausedSounds, IdleEffects.HiddenRenderers };
        public static bool IdleEffectsHideRenderers { get => IdleEffects.HidingRenderers; set => IdleEffects.HidingRenderers = value; }
        public static string IdleEffectsFailure => IdleEffects.Failure;
        public static bool PauseIdleVines { get => IdleVines.Pausing; set => IdleVines.SetPausing(value); }
        public static bool SkipBarsBehindCamera { get => BarsBehindCamera.Skipping; set => BarsBehindCamera.Skipping = value; }
        public static long BarsBehindCameraSkipped => BarsBehindCamera.Skipped;
        public static bool BarBehindCameraHidden(object hud) => BarsBehindCamera.Hidden(hud);
        public static string BarsBehindCameraFailure => BarsBehindCamera.Failure;
        public static bool HideDistantSignWriting { get => DistantSignWriting.Hiding; set => DistantSignWriting.Hiding = value; }
        public static int[] DistantSignWritingCounts() => new[] { DistantSignWriting.Count, DistantSignWriting.HiddenCount };
        public static bool[] DistantSignWritingState(Sign sign) { var (registered, hidden) = DistantSignWriting.State(sign); return new[] { registered, hidden }; }
        public static string DistantSignWritingFailure => DistantSignWriting.Failure;
        public static bool PaceMistEmitter { get => MistEmitterRate.Pacing; set => MistEmitterRate.Pacing = value; }

        // The spatial building renderer starts with the first world; the isolated client switches it off and on, and to native
        // drawing in place, for checks and paired measurements, has lights draw whole merged meshes or leave out their merged
        // slots for checks, and counts the renderer's processor cycles and the collector's events within its work while it
        // measures frames.
        public static bool SpatialBuildingRenderer
        {
            get => SpatialBuildings.Running;
            set { rendererPending = false; if (value) SpatialBuildings.Start(); else SpatialBuildings.Stop(); }
        }
        public static bool SpatialBuildingsNative { get => SpatialBuildings.Native; set => SpatialBuildings.SetNative(value); }
        public static int SpatialBuildingsNativeCoverage { get => SpatialBuildings.NativeCoverage; set => SpatialBuildings.SetNativeCoverage(value); }
        public static bool SpatialBuildingsCoverageSettled => SpatialBuildings.CoverageSettled;
        public static bool SpatialBuildingsWholeLightShadows { get => SpatialBuildings.WholeLightShadows; set => SpatialBuildings.SetWholeLightShadows(value); }
        public static bool SpatialBuildingsLightSlots { get => SpatialBuildings.LightSlots; set => SpatialBuildings.SetLightSlots(value); }
        public static bool SpatialBuildingsSettled => SpatialBuildings.Settled;
        public static bool SpatialBuildingMerged(GameObject piece) => SpatialBuildings.Owns(piece);
        public static string SpatialBuildingsReport() => SpatialBuildings.Report();
        public static long[] SpatialBuildingsTiming() => SpatialBuildings.Timing();
        public static bool SpatialBuildingsCountCycles { get => SpatialBuildings.CountCycles; set => SpatialBuildings.CountCycles = value; }
        public static IntPtr SpatialBuildingsCollectorEvents { get => SpatialBuildings.CollectorEvents; set => SpatialBuildings.CollectorEvents = value; }
        public static string SpatialBuildingsFailure => SpatialBuildings.Failure;
        public static int SpatialBuildingsSkyLevels => SpatialBuildings.SkyLevels;
        // Crops: planted natural objects and scenery materials without instancing merge while the first is set, which restarts a
        // running renderer, and their merged meshes draw while the second is set; the isolated client switches both.
        public static bool SpatialBuildingsMergeCrops
        {
            get => SpatialBuildings.MergeCrops;
            set
            {
                if (value == SpatialBuildings.MergeCrops) return;
                SpatialBuildings.MergeCrops = value;
                if (SpatialBuildings.Running) { SpatialBuildings.Stop(); SpatialBuildings.Start(); }
            }
        }
        public static bool SpatialBuildingsCropsMerged { get => !SpatialBuildings.CropsNative; set => SpatialBuildings.SetCropsNative(!value); }
        public static long[] SpatialBuildingsCropCounts() => SpatialBuildings.CropCounts();
        public static bool SpatialBuildingsSmallShadowsNear { get => SpatialBuildings.SmallShadowsNear; set => SpatialBuildings.SetSmallShadowsNear(value); }
        public static string SpatialBuildingsDescribe(ZNetView view, Vector3 camera) => SpatialBuildings.Describe(view, camera);

        private void Awake()
        {
            Log = Logger;
            // Before any feature looks up another mod's types.
            Guard(nameof(TypeLookups), () => TypeLookups.Start(new HarmonyLib.Harmony(Id + ".type-lookups")));
            Camera.onPreCull += BeforeCamera;
            // Dedicated servers skip client visuals; their separate garbage option does not control the game fixes.
            bool graphics = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            rendererPending = graphics;
            // Before Progression Fix, the last plugin to start, adds its patches.
            if (graphics) Guard(nameof(RepeatedPatchRebuilds), () => RepeatedPatchRebuilds.Start(new HarmonyLib.Harmony(Id + ".patch-rebuilds")));
            BindSendRate();
            Guard(nameof(TransferredPatches), TransferredPatches.Start);
            bool serverGarbageFixes = !graphics && Config.Bind("Dedicated server", "Garbage fixes", false,
                "Also write package floats, send and read network records, test the active area, compress Better Networking's packets and pass collision callbacks without garbage on a dedicated server. Clients always use these optimizations. This option does not control the independent game fixes.").Value;
            if (graphics || serverGarbageFixes)
            {
                // Package floats come first, since a patched method is compiled at once and could otherwise keep the native
                // float writes inlined.
                Guard(nameof(PackageFloats), PackageFloats.Start);
                Guard(nameof(WorldPerformance.LeanRecordUpdates), WorldPerformance.LeanRecordUpdates.Start);
                Guard(nameof(ActiveAreaTests), ActiveAreaTests.Start);
                Guard(nameof(PooledCompression), PooledCompression.Start);
                Guard(nameof(CollisionCallbacks), CollisionCallbacks.Start);
            }
            if (!graphics)
            {
                Logger.LogInfo(serverGarbageFixes
                    ? "World Performance loaded without graphics; packages write floats without garbage, network records are sent and read without temporary lists and delegates, active-area tests make no garbage, collision callbacks reuse their objects, and effects and rendering stay native."
                    : "World Performance loaded without graphics; independent game fixes are active, and [Dedicated server] Garbage fixes enables additional allocation reductions.");
                return;
            }
            Guard(nameof(UnseenEffects), UnseenEffects.Start);
            Guard(nameof(RestingItemSparkles), RestingItemSparkles.Start);
            Guard(nameof(FarFlames), () => FarFlames.SetEnabled(true));
            Guard(nameof(WorldPerformance.SmallFarShadows), () => WorldPerformance.SmallFarShadows.SetEnabled(true));
            Guard(nameof(DisplayMode), DisplayMode.Start);
            Guard(nameof(RestingFloaters), RestingFloaters.Start);
            Guard(nameof(SilencedLoops), SilencedLoops.Start);
            Guard(nameof(StaticGround), StaticGround.Start);
            Guard(nameof(RestingDoors), RestingDoors.Start);
            Guard(nameof(SharedWaterTime), SharedWaterTime.Start);
            Guard(nameof(MapDataPacking), MapDataPacking.Start);
            Guard(nameof(DeferredAssetUnload), DeferredAssetUnload.Start);
            Guard(nameof(SnappingRenderMeshes), SnappingRenderMeshes.Start);
            Guard(nameof(TerrainRebuilds), TerrainRebuilds.Start);
            Guard(nameof(StagedDistantTerrain), StagedDistantTerrain.Start);
            Guard(nameof(CollectionBallast), CollectionBallast.Start);
            Guard(nameof(BiomeBorderWarning), BiomeBorderWarning.Start);
            Guard(nameof(InputHintRefresh), InputHintRefresh.Start);
            Guard(nameof(RestingCreatures), RestingCreatures.Start);
            Guard(nameof(IdleEffects), IdleEffects.Start);
            Guard(nameof(PathfindingRecords), PathfindingRecords.Start);
            Guard(nameof(UnchangedContainers), UnchangedContainers.Start);
            Guard(nameof(SettledSupport), SettledSupport.Start);
            Guard(nameof(SnapPointSearch), SnapPointSearch.Start);
            Guard(nameof(ComfortPanelRefresh), ComfortPanelRefresh.Start);
            Guard(nameof(IdleVines), IdleVines.Start);
            Guard(nameof(MistEmitterRate), MistEmitterRate.Start);
            Guard(nameof(BarsBehindCamera), BarsBehindCamera.Start);
            Guard(nameof(DistantSignWriting), DistantSignWriting.Start);
            Guard(nameof(IntroLogos), IntroLogos.Start);
            Logger.LogInfo("World Performance loaded: unseen world effects pause, torch flames beyond 30 m come from shared copies of their systems, small objects beyond 60 m cast no sun shadows, the display mode is read twice a second, resting items skip wave heights, silenced looping sounds pause, resting objects keep their ground heights, the animators of resting doors pause, water volumes share their water time, saves reuse an unchanged packed map and pack a changed one on a worker, the hourly asset unload waits for a natural pause, ground snapping leaves terrain render meshes to the next frames, terrain rebuilds colour tiles from their corners once and bake their colliders on workers, the distant terrain builds its moved regions one a frame and shows them together, empty reference arrays space collections out, biome borders log no warning, key hints refresh only their own labels, health bars the hud would hide again behind the camera stay hidden, sign writing beyond 60 m is not drawn, the intro logos are skipped, creatures at rest sleep, reuse their ground rays and update every few steps, idle cooking and weather effects pause, containers skip reloading unchanged items, building keeps each nearby piece's snap points, idle vine segments stop updating, the mist emitter runs at most 10 times a second, packages write floats without garbage, network records are sent and read without temporary lists and delegates, active-area tests make no garbage and collision callbacks reuse their objects. The spatial building renderer and the later presentation wait start with the first world.");
        }

        // One feature that cannot start leaves the others running.
        private static void Guard(string feature, Action start)
        {
            try { start(); }
            catch (Exception error) { Fallback(feature + " did not start: " + error.Message); }
        }

        // A feature that stops working must fail mod-compatibility, which also finds this line in the test server's log.
        internal static void Fallback(string message)
        {
            if (StartFailure != null && StartFailure.Contains(message)) return;
            StartFailure = StartFailure == null ? message : StartFailure + "; " + message;
            Log?.LogError("Feature fell back: " + message);
        }

        private void BindSendRate()
        {
            int previousRate = 256;
            string previousPath = Path.Combine(Paths.ConfigPath, "valheim.epiclootprogressionfix.cfg");
            if (File.Exists(previousPath))
            {
                var previous = new ConfigFile(previousPath, false) { SaveOnConfigSet = false };
                previousRate = previous.Bind("Network", "Server send rate", previousRate).Value;
            }
            ServerSendRate = Config.Bind("Network", "Server send rate", previousRate,
                new ConfigDescription("Dedicated server only: KiB per second sent to each player. Restart after changing.",
                    new AcceptableValueRange<int>(64, 4096)));
            UploadBudget = Config.Bind("Network", "Upload budget", 1536,
                new ConfigDescription("Dedicated server only: KiB per second for all players together, divided equally among them, which no player's rate exceeds. Keep it within the server's upload capacity; the connection log's out_kib_s, ping_ms and quality columns show when it is too high.",
                    new AcceptableValueRange<int>(256, 16384)));
            ConnectionLogging = Config.Bind("Network", "Connection log", true,
                "Dedicated server only: record each player's connection, the creatures they simulate, how smoothly their game runs and the server's frame times every five seconds, and each player's machine and graphics settings, in a network-log folder beside the saves.");
        }

        private void BeforeCamera(Camera camera)
        {
            FarFlames.BeforeCamera(camera);
            SpatialBuildings.BeforeCamera(camera);
        }

        // Before the particle update begins, so particle systems changed here never wait for running particle jobs.
        private void Update()
        {
            FarFlames.Update();
            IdleEffects.Update();
            RestingItemSparkles.Update();
            LoadingThreadPriority.Update();
            FrameReports.Update();
        }

        private void LateUpdate()
        {
            ConnectionLog.LateUpdate();
            if (rendererPending && ZNetScene.instance)
            {
                PresentationWait.Start();
                StartRenderer();
            }
            SpatialBuildings.LateUpdate();
            WorldPerformance.SmallFarShadows.LateUpdate();
            SilencedLoops.LateUpdate();
            DeferredAssetUnload.LateUpdate();
            ServerAssetUnload.LateUpdate();
            MapDataPacking.LateUpdate();
            SnappingRenderMeshes.LateUpdate();
        }

        // Starts the renderer once, with the first world; where it cannot run, rendering stays native.
        private static void StartRenderer()
        {
            try
            {
                SpatialBuildingRenderer = true;
                Log.LogInfo("The spatial building renderer is running.");
            }
            catch (Exception error)
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11) Fallback(error.Message);
                else Log.LogWarning(error.Message);
            }
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= BeforeCamera;
            PresentationWait.Stop();
            TransferredPatches.Stop();
            FarFlames.Stop();
            WorldPerformance.SmallFarShadows.Stop();
            UnseenEffects.Stop();
            RestingItemSparkles.Stop();
            DisplayMode.Stop();
            RestingFloaters.Stop();
            SilencedLoops.Stop();
            StaticGround.Stop();
            RestingDoors.Stop();
            SharedWaterTime.Stop();
            MapDataPacking.Stop();
            DeferredAssetUnload.Stop();
            SnappingRenderMeshes.Stop();
            TerrainRebuilds.Stop();
            StagedDistantTerrain.Stop();
            CollectionBallast.Stop();
            BiomeBorderWarning.Stop();
            InputHintRefresh.Stop();
            RestingCreatures.Stop();
            IdleEffects.Stop();
            UnchangedContainers.Stop();
            SettledSupport.Stop();
            SnapPointSearch.Stop();
            ComfortPanelRefresh.Stop();
            IdleVines.Stop();
            MistEmitterRate.Stop();
            BarsBehindCamera.Stop();
            DistantSignWriting.Stop();
            WorldPerformance.LeanRecordUpdates.Stop();
            ActiveAreaTests.Stop();
            PackageFloats.Stop();
            PooledCompression.Stop();
            CollisionCallbacks.Stop();
            SpatialBuildings.Stop();
        }
    }
}
