using System;
using UnityEngine;

namespace WorldPerformance
{
    // The switches and counters the hidden test client reads and sets for checks and paired measurements. The features
    // themselves start in Awake.
    public sealed partial class Plugin
    {
        public static bool TransferredFixesRunning => TransferredPatches.Running;
        public static string TransferredFixesFailure => TransferredPatches.Failure;
        public static bool IntroLogosSkipped => IntroLogos.Skipped;

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
        public static bool RemoveUnusedParticleMeshes { get => UnusedParticleMeshes.Removing; set => UnusedParticleMeshes.Removing = value; }
        public static bool UnusedParticleMeshesRunning => UnusedParticleMeshes.Running;
        public static int UnusedParticleMeshesRemoved => UnusedParticleMeshes.Removed;
        public static bool CopySharedParticleMeshes { get => SharedParticleMeshes.Copying; set => SharedParticleMeshes.Copying = value; }
        public static bool SharedParticleMeshesRunning => SharedParticleMeshes.Running;
        public static int SharedParticleMeshesCopied => SharedParticleMeshes.Copied;
        public static bool PoolDamageNumbers { get => DamageNumberReuse.Pooling; set => DamageNumberReuse.Pooling = value; }
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
        public static bool LeanArmorTooltip { get => ArmorTooltipText.Lean; set => ArmorTooltipText.Lean = value; }
        public static bool LeanChestPreview { get => ChestMovePreview.Lean; set => ChestMovePreview.Lean = value; }
        public static long ChestPreviewsSkipped => ChestMovePreview.Skipped;
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
        public static bool LeanCloudWrites { get => CloudWriteBuffers.Lean; set => CloudWriteBuffers.Lean = value; }
        public static bool CloudWriteBuffersRunning => CloudWriteBuffers.Running;
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
    }
}
