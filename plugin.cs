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
    [BepInDependency(PluginIds.StarLevelSystem, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.Jotunn, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.Sailing, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.ConditionalConfigSync, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.Dyrr, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(PluginIds.AzuAntiArthriticCrafting, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "tenemo.WorldPerformance";
        public const string Version = "1.0.7";
        // Contract 1: call on the startup thread, do not execute methods with pending patches inside the scope,
        // and dispose before using them. Disposal completes all pending wrappers, including after an exception.
        public static IDisposable BeginStartupPatchBatch(int contractVersion) => RepeatedPatchRebuilds.Begin(contractVersion);
        internal static BepInEx.Logging.ManualLogSource Log { get; private set; }
        internal static ConfigEntry<int> ServerSendRate, UploadBudget;
        internal static ConfigEntry<bool> ConnectionLogging;
        // What fell back to the game's own behavior; mod-compatibility fails when this is not empty.
        public static string StartFailure { get; private set; }
        // Whether the spatial building renderer still waits for the first world on a client with graphics; see Awake.
        private static bool rendererPending;

        // Starts with the first world, where the client has graphics; the isolated client also switches it off and on.
        public static bool SpatialBuildingRenderer
        {
            get => SpatialBuildings.Running;
            set { rendererPending = false; if (value) SpatialBuildings.Start(); else SpatialBuildings.Stop(); }
        }

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
            Guard(nameof(TransferredPatches), TransferredPatches.Start, TransferredPatches.Stop);
            bool serverGarbageFixes = !graphics && Config.Bind("Dedicated server", "Garbage fixes", false,
                "Also write package floats, send and read network records, test the active area, compress Better Networking's packets and pass collision callbacks without garbage on a dedicated server. Clients always use these optimizations. This option does not control the independent game fixes.").Value;
            if (graphics || serverGarbageFixes)
            {
                // Package floats come first, since a patched method is compiled at once and could otherwise keep the native
                // float writes inlined.
                Guard(nameof(PackageFloats), PackageFloats.Start, PackageFloats.Stop);
                Guard(nameof(WorldPerformance.LeanRecordUpdates), WorldPerformance.LeanRecordUpdates.Start, WorldPerformance.LeanRecordUpdates.Stop);
                Guard(nameof(ActiveAreaTests), ActiveAreaTests.Start, ActiveAreaTests.Stop);
                Guard(nameof(PooledCompression), PooledCompression.Start, PooledCompression.Stop);
                Guard(nameof(CollisionCallbacks), CollisionCallbacks.Start, CollisionCallbacks.Stop);
            }
            if (!graphics)
            {
                Logger.LogInfo(serverGarbageFixes
                    ? "World Performance loaded without graphics; packages write floats without garbage, network records are sent and read without temporary lists and delegates, active-area tests make no garbage, collision callbacks reuse their objects, and effects and rendering stay native."
                    : "World Performance loaded without graphics; independent game fixes are active, and [Dedicated server] Garbage fixes enables additional allocation reductions.");
                return;
            }
            Guard(nameof(UnseenEffects), UnseenEffects.Start, UnseenEffects.Stop);
            Guard(nameof(RestingItemSparkles), RestingItemSparkles.Start, RestingItemSparkles.Stop);
            Guard(nameof(FarFlames), () => FarFlames.SetEnabled(true), FarFlames.Stop);
            Guard(nameof(WorldPerformance.SmallFarShadows), () => WorldPerformance.SmallFarShadows.SetEnabled(true), WorldPerformance.SmallFarShadows.Stop);
            Guard(nameof(DisplayMode), DisplayMode.Start, DisplayMode.Stop);
            Guard(nameof(RestingFloaters), RestingFloaters.Start, RestingFloaters.Stop);
            Guard(nameof(SilencedLoops), SilencedLoops.Start, SilencedLoops.Stop);
            Guard(nameof(UnusedParticleMeshes), UnusedParticleMeshes.Start);
            Guard(nameof(SharedParticleMeshes), SharedParticleMeshes.Start);
            Guard(nameof(StaticGround), StaticGround.Start, StaticGround.Stop);
            Guard(nameof(RestingDoors), RestingDoors.Start, RestingDoors.Stop);
            Guard(nameof(SharedWaterTime), SharedWaterTime.Start, SharedWaterTime.Stop);
            Guard(nameof(MapDataPacking), MapDataPacking.Start, MapDataPacking.Stop);
            Guard(nameof(CloudWriteBuffers), CloudWriteBuffers.Start);
            Guard(nameof(DeferredAssetUnload), DeferredAssetUnload.Start, DeferredAssetUnload.Stop);
            Guard(nameof(KeptStreamedAssets), KeptStreamedAssets.Start, KeptStreamedAssets.Stop);
            Guard(nameof(SnappingRenderMeshes), SnappingRenderMeshes.Start, SnappingRenderMeshes.Stop);
            Guard(nameof(TerrainRebuilds), TerrainRebuilds.Start, TerrainRebuilds.Stop);
            Guard(nameof(GrassGround), GrassGround.Start, GrassGround.Stop);
            Guard(nameof(DistantSectorLists), DistantSectorLists.Start, DistantSectorLists.Stop);
            Guard(nameof(StagedDistantTerrain), StagedDistantTerrain.Start, StagedDistantTerrain.Stop);
            Guard(nameof(CollectionBallast), CollectionBallast.Start, CollectionBallast.Stop);
            Guard(nameof(BiomeBorderWarning), BiomeBorderWarning.Start, BiomeBorderWarning.Stop);
            Guard(nameof(InputHintRefresh), InputHintRefresh.Start, InputHintRefresh.Stop);
            Guard(nameof(RestingCreatures), RestingCreatures.Start, RestingCreatures.Stop);
            Guard(nameof(IdleEffects), IdleEffects.Start, IdleEffects.Stop);
            Guard(nameof(PathfindingRecords), PathfindingRecords.Start);
            Guard(nameof(UnchangedContainers), UnchangedContainers.Start, UnchangedContainers.Stop);
            Guard(nameof(SettledSupport), SettledSupport.Start, SettledSupport.Stop);
            Guard(nameof(SnapPointSearch), SnapPointSearch.Start, SnapPointSearch.Stop);
            Guard(nameof(ComfortPanelRefresh), ComfortPanelRefresh.Start, ComfortPanelRefresh.Stop);
            Guard(nameof(IdleVines), IdleVines.Start, IdleVines.Stop);
            Guard(nameof(MistEmitterRate), MistEmitterRate.Start, MistEmitterRate.Stop);
            Guard(nameof(BarsBehindCamera), BarsBehindCamera.Start, BarsBehindCamera.Stop);
            Guard(nameof(DistantSignWriting), DistantSignWriting.Start, DistantSignWriting.Stop);
            Guard(nameof(IntroLogos), IntroLogos.Start);
            Logger.LogInfo("World Performance loaded; the spatial building renderer and the later presentation wait start with the first world.");
        }

        // One feature that cannot start leaves the others running, and takes back whatever part of it had started, so a patch
        // applied before the failure never runs without the patches that complete it.
        private static void Guard(string feature, Action start, Action stop = null)
        {
            try { start(); }
            catch (Exception error)
            {
                Fallback(feature + " did not start: " + error.Message);
                if (stop == null) return;
                try { stop(); }
                catch (Exception stopping) { Fallback(feature + " could not be taken back after its failed start: " + stopping.GetBaseException().Message); }
            }
        }

        // The features' work each frame and before each camera, in the order below. One that throws stops there and is
        // reported, and the calls after it in the same frame still run.
        private enum Tick { FarFlames, IdleEffects, RestingItemSparkles, LoadingThreadPriority, FrameReports, ConnectionLog, Renderer, SpatialBuildings,
            SmallFarShadows, SilencedLoops, UnusedParticleMeshes, SharedParticleMeshes, DeferredAssetUnload, KeptStreamedAssets, ServerAssetUnload,
            MapDataPacking, SnappingRenderMeshes, FarFlamesCamera, SpatialBuildingsCamera, Count }
        private static readonly bool[] stoppedTicks = new bool[(int)Tick.Count];

        private static void Run(Tick tick, Action work, Action stop = null)
        {
            if (stoppedTicks[(int)tick]) return;
            try { work(); }
            catch (Exception error) { Stopped(tick, error, stop); }
        }

        private static void Stopped(Tick tick, Exception error, Action stop)
        {
            stoppedTicks[(int)tick] = true;
            Fallback(tick + " stopped after an error: " + error.GetBaseException().Message);
            Log?.LogError(error);
            if (stop == null) return;
            try { stop(); }
            catch (Exception stopping) { Fallback(tick + " could not be stopped after its error: " + stopping.GetBaseException().Message); }
        }

        // Set as the game quits, before OnDestroy, when no patched method runs again, so their wrappers are not rebuilt.
        internal static bool Quitting { get; private set; }

        private void OnApplicationQuit() => Quitting = true;

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
            // Progression Fix's old setting is only a starting value; a file that cannot be read leaves the default.
            if (File.Exists(previousPath))
                try
                {
                    var previous = new ConfigFile(previousPath, false) { SaveOnConfigSet = false };
                    previousRate = previous.Bind("Network", "Server send rate", previousRate).Value;
                }
                catch (Exception error) { Logger.LogWarning("Progression Fix's old server send rate could not be read: " + error.Message); }
            ServerSendRate = Config.Bind("Network", "Server send rate", previousRate,
                new ConfigDescription("Dedicated server only: KiB per second sent to each player. Restart after changing.",
                    new AcceptableValueRange<int>(64, 4096)));
            UploadBudget = Config.Bind("Network", "Upload budget", 1536,
                new ConfigDescription("Dedicated server only: KiB per second for all players together, divided equally among them, which no player's rate exceeds. Keep it within the server's upload capacity; the connection log's out_kib_s, ping_ms and quality columns show when it is too high.",
                    new AcceptableValueRange<int>(256, 16384)));
            ConnectionLogging = Config.Bind("Network", "Connection log", true,
                "Dedicated server only: record each player's connection, the zone they are in, the creatures they simulate, how smoothly their game runs and the server's frame times every five seconds, each player's machine and graphics settings, each boss altar offering and which player's game created each boss, in a network-log folder beside the saves.");
        }

        private void BeforeCamera(Camera camera)
        {
            if (!stoppedTicks[(int)Tick.FarFlamesCamera])
                try { FarFlames.BeforeCamera(camera); }
                catch (Exception error) { Stopped(Tick.FarFlamesCamera, error, FarFlames.Stop); }
            if (!stoppedTicks[(int)Tick.SpatialBuildingsCamera])
                try { SpatialBuildings.BeforeCamera(camera); }
                catch (Exception error) { Stopped(Tick.SpatialBuildingsCamera, error, SpatialBuildings.Stop); }
        }

        // Before the particle update begins, so particle systems changed here never wait for running particle jobs.
        private void Update()
        {
            Run(Tick.FarFlames, FarFlames.Update, FarFlames.Stop);
            Run(Tick.IdleEffects, IdleEffects.Update, IdleEffects.Stop);
            Run(Tick.RestingItemSparkles, RestingItemSparkles.Update, RestingItemSparkles.Stop);
            Run(Tick.LoadingThreadPriority, LoadingThreadPriority.Update);
            Run(Tick.FrameReports, FrameReports.Update);
        }

        private void LateUpdate()
        {
            Run(Tick.ConnectionLog, ConnectionLog.LateUpdate);
            if (rendererPending && ZNetScene.instance)
            {
                Run(Tick.Renderer, PresentationWait.Start);
                StartRenderer();
            }
            // Nothing moves the camera between the two.
            var camera = Utils.GetMainCamera();
            var position = camera ? camera.transform.position : Vector3.zero;
            if (!stoppedTicks[(int)Tick.SpatialBuildings])
                try { SpatialBuildings.LateUpdate(camera, position); }
                catch (Exception error) { Stopped(Tick.SpatialBuildings, error, SpatialBuildings.Stop); }
            if (!stoppedTicks[(int)Tick.SmallFarShadows])
                try { WorldPerformance.SmallFarShadows.LateUpdate(camera, position); }
                catch (Exception error) { Stopped(Tick.SmallFarShadows, error, WorldPerformance.SmallFarShadows.Stop); }
            Run(Tick.SilencedLoops, SilencedLoops.LateUpdate, SilencedLoops.Stop);
            Run(Tick.UnusedParticleMeshes, UnusedParticleMeshes.LateUpdate);
            Run(Tick.SharedParticleMeshes, SharedParticleMeshes.LateUpdate);
            Run(Tick.DeferredAssetUnload, DeferredAssetUnload.LateUpdate, DeferredAssetUnload.Stop);
            Run(Tick.KeptStreamedAssets, KeptStreamedAssets.LateUpdate, KeptStreamedAssets.Stop);
            Run(Tick.ServerAssetUnload, ServerAssetUnload.LateUpdate);
            Run(Tick.MapDataPacking, MapDataPacking.LateUpdate, MapDataPacking.Stop);
            Run(Tick.SnappingRenderMeshes, SnappingRenderMeshes.LateUpdate, SnappingRenderMeshes.Stop);
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
            Finish(PresentationWait.Stop);
            Finish(TransferredPatches.Stop);
            Finish(FarFlames.Stop);
            Finish(WorldPerformance.SmallFarShadows.Stop);
            Finish(UnseenEffects.Stop);
            Finish(RestingItemSparkles.Stop);
            Finish(DisplayMode.Stop);
            Finish(RestingFloaters.Stop);
            Finish(SilencedLoops.Stop);
            Finish(StaticGround.Stop);
            Finish(RestingDoors.Stop);
            Finish(SharedWaterTime.Stop);
            Finish(MapDataPacking.Stop);
            Finish(DeferredAssetUnload.Stop);
            Finish(KeptStreamedAssets.Stop);
            Finish(SnappingRenderMeshes.Stop);
            Finish(TerrainRebuilds.Stop);
            Finish(GrassGround.Stop);
            Finish(DistantSectorLists.Stop);
            Finish(StagedDistantTerrain.Stop);
            Finish(CollectionBallast.Stop);
            Finish(BiomeBorderWarning.Stop);
            Finish(InputHintRefresh.Stop);
            Finish(RestingCreatures.Stop);
            Finish(IdleEffects.Stop);
            Finish(UnchangedContainers.Stop);
            Finish(SettledSupport.Stop);
            Finish(SnapPointSearch.Stop);
            Finish(ComfortPanelRefresh.Stop);
            Finish(IdleVines.Stop);
            Finish(MistEmitterRate.Stop);
            Finish(BarsBehindCamera.Stop);
            Finish(DistantSignWriting.Stop);
            Finish(WorldPerformance.LeanRecordUpdates.Stop);
            Finish(ActiveAreaTests.Stop);
            Finish(PackageFloats.Stop);
            Finish(PooledCompression.Stop);
            Finish(CollisionCallbacks.Stop);
            Finish(SpatialBuildings.Stop);
        }

        // One feature whose stop throws leaves the others to stop.
        private static void Finish(Action stop)
        {
            try { stop(); }
            catch (Exception error) { Log?.LogError("Stopping a feature failed: " + error); }
        }
    }
}
