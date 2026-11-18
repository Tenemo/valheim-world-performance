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
            Guard(nameof(UnusedParticleMeshes), UnusedParticleMeshes.Start);
            Guard(nameof(SharedParticleMeshes), SharedParticleMeshes.Start);
            Guard(nameof(StaticGround), StaticGround.Start);
            Guard(nameof(RestingDoors), RestingDoors.Start);
            Guard(nameof(SharedWaterTime), SharedWaterTime.Start);
            Guard(nameof(MapDataPacking), MapDataPacking.Start);
            Guard(nameof(CloudWriteBuffers), CloudWriteBuffers.Start);
            Guard(nameof(DeferredAssetUnload), DeferredAssetUnload.Start);
            Guard(nameof(KeptStreamedAssets), KeptStreamedAssets.Start);
            Guard(nameof(SnappingRenderMeshes), SnappingRenderMeshes.Start);
            Guard(nameof(TerrainRebuilds), TerrainRebuilds.Start);
            Guard(nameof(GrassGround), GrassGround.Start);
            Guard(nameof(DistantSectorLists), DistantSectorLists.Start);
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
            Logger.LogInfo("World Performance loaded; the spatial building renderer and the later presentation wait start with the first world.");
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
            // Nothing moves the camera between the two.
            var camera = Utils.GetMainCamera();
            var position = camera ? camera.transform.position : Vector3.zero;
            SpatialBuildings.LateUpdate(camera, position);
            WorldPerformance.SmallFarShadows.LateUpdate(camera, position);
            SilencedLoops.LateUpdate();
            UnusedParticleMeshes.LateUpdate();
            SharedParticleMeshes.LateUpdate();
            DeferredAssetUnload.LateUpdate();
            KeptStreamedAssets.LateUpdate();
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
            KeptStreamedAssets.Stop();
            SnappingRenderMeshes.Stop();
            TerrainRebuilds.Stop();
            GrassGround.Stop();
            DistantSectorLists.Stop();
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
