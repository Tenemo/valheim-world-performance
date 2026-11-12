using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Mono.Cecil;

namespace WorldPerformance
{
    internal static class PluginIds
    {
        internal const string QuickStack = "goldenrevolver.quick_stack_store";
        internal const string DropThat = "asharppen.valheim.drop_that";
        internal const string AzuCraftyBoxes = "Azumatt.AzuCraftyBoxes";
        internal const string BetterUi = "MK_BetterUI";
        internal const string ItemStacks = "shudnal.ItemStacksItemWeights";
        internal const string MultiUserChest = "com.maxsch.valheim.MultiUserChest";
        internal const string FastLink = "Azumatt.FastLink";
        internal const string EpicLoot = "randyknapp.mods.epicloot";
        internal const string EquipmentAndQuickSlots = "randyknapp.mods.equipmentandquickslots";
        internal const string WieldWhileSwimming = "blacks7ar.WieldEquipmentWhileSwimming";
        internal const string AzuClock = "Azumatt.AzuClock";
        internal const string BetterBeehives = "maxfoxgaming.betterbeehives";
        internal const string ComfortMe = "hardwire99.comfortme";
        internal const string FedoHud = "fedo.hud";
        internal const string NoSmokeStayLit = "TastyChickenLegs.NoSmokeStayLit";
        internal const string TameRidingOverhaul = "uk.ainzworth.valheim.tameridingoverhaul";
        internal const string Treadwell = "com.jstack424.treadwell";
        private static readonly Dictionary<string, string[]> Supported = new Dictionary<string, string[]>
        {
            { QuickStack, new[] { "1.4.16" } }, { DropThat, new[] { "3.1.7" } },
            { AzuCraftyBoxes, new[] { "1.8.28" } }, { BetterUi, new[] { "2.5.13", "2.5.14" } },
            { ItemStacks, new[] { "1.1.1" } }, { MultiUserChest, new[] { "0.6.2" } }, { FastLink, new[] { "1.5.2", "1.5.3" } },
            { EpicLoot, new[] { "0.15.4", "0.15.5", "0.15.6" } }, { EquipmentAndQuickSlots, new[] { "3.2.1" } }, { WieldWhileSwimming, new[] { "1.1.4" } },
            { AzuClock, new[] { "1.1.1" } }, { BetterBeehives, new[] { "1.3.1" } }, { ComfortMe, new[] { "1.3.1" } },
            { FedoHud, new[] { "1.1.2" } }, { NoSmokeStayLit, new[] { "2.4.4" } }, { TameRidingOverhaul, new[] { "0.1.20" } },
            { Treadwell, new[] { "1.0.1" } }
        };

        internal static bool TryGet(string id, out PluginInfo plugin)
        {
            if (!Chainloader.PluginInfos.TryGetValue(id, out plugin)) return false;
            if (plugin.Instance == null || !Supported[id].Contains(plugin.Metadata.Version.ToString()))
                throw new InvalidOperationException(plugin.Metadata.Name + " " + plugin.Metadata.Version + " is not initialized with a supported version (" + string.Join(", ", Supported[id]) + ").");
            return true;
        }
    }

    internal static class TransferredPatches
    {
        private static readonly Harmony core = new Harmony(Plugin.Id + ".game-fixes");
        private static readonly List<PerformancePatches> integrations = new List<PerformancePatches>();
        private static readonly HashSet<string> olderImplementations = new HashSet<string>();
        private static readonly HashSet<string> deferred = new HashSet<string>();
        private static readonly List<string> failures = new List<string>();
        internal static string Failure => string.Join("\n", failures);
        internal static bool Running { get; private set; }

        // New Progression Fix requires this mod, so a reverse soft dependency would make a cycle. Read the installed
        // plugin's metadata before either load order: only an actual retained implementation defers that feature.
        private static void FindPreviousOwner()
        {
            foreach (string path in Directory.EnumerateFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
            {
                AssemblyDefinition assembly;
                try { assembly = AssemblyDefinition.ReadAssembly(path); }
                catch (BadImageFormatException) { continue; }
                using (assembly)
                {
                    bool progression = assembly.MainModule.Types.Any(type => type.CustomAttributes.Any(attribute =>
                        attribute.AttributeType.FullName == typeof(BepInPlugin).FullName && attribute.ConstructorArguments.Count == 3 &&
                        (string)attribute.ConstructorArguments[0].Value == "valheim.epiclootprogressionfix"));
                    if (!progression) continue;
                    foreach (var type in assembly.MainModule.Types)
                        if (type.Namespace == "EpicLootProgressionFix") olderImplementations.Add(type.Name);
                }
            }
        }

        private static bool Previous(Type type)
        {
            if (!olderImplementations.Contains(type.Name)) return false;
            deferred.Add(type.Name);
            return true;
        }

        private static void PatchTree(Type type)
        {
            if (type.ContainsGenericParameters || type.IsDefined(typeof(CompilerGeneratedAttribute), false)) return;
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            if (type.IsDefined(typeof(HarmonyPatch), false)) core.CreateClassProcessor(type).Patch();
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) PatchTree(nested);
        }

        internal static void Start()
        {
            FindPreviousOwner();
            try
            {
                foreach (var type in new[] { typeof(PathfindingGroundHits), typeof(PathfindingLinkSpacing), typeof(PathfindingEdgeLinks), typeof(PathfindingRemovals),
                    typeof(GroundSupport), typeof(TerrainPaintUpdates), typeof(PrefabComponentQueries), typeof(LoadingPassThrottle),
                    typeof(ZoneExpiryChecks), typeof(TerrainResultCapacity), typeof(RemoteFloatingBodies), typeof(LocalizedTextLifetime), typeof(HeadlessRendering),
                    typeof(HeadlessCharacterPreview), typeof(PlantGrowthChecks), typeof(FirstPlantGrowthCheck), typeof(PlantSpacingQuery),
                    typeof(CookingSlotVisuals), typeof(PacedRemovals), typeof(PacedDungeonRooms),
                    typeof(TerrainSupportForwarders), typeof(BuildButtonSubscriptions), typeof(BuildButtonOrder), typeof(ClientMapCache),
                    typeof(ParallelBiomePoints), typeof(WorldGenerationCache), typeof(LoadingScreenZones), typeof(PlayerDepartures), typeof(ServerTerrainMeshes),
                    typeof(DamageNumberFading), typeof(GhostDungeonRooms),
                    typeof(ConfigWatcherListings), typeof(PacedGrassRebuilds),
                    typeof(LoadingScreenPriority), typeof(ArrivalConfirmation),
                    typeof(AsyncLocationSpawns) })
                    if (!Previous(type)) PatchTree(type);
                // This is the synchronous fallback of the existing map cache and background packing.
                if (!Previous(typeof(MapSaveWrites))) PatchTree(typeof(MapSaveWrites));
                Optional(typeof(SendWindow), patches => SendWindow.Initialize(patches, Plugin.Log));
                Optional(typeof(OwnershipOrder), OwnershipOrder.Initialize);
                Optional(typeof(ReceivedRecordSaves), ReceivedRecordSaves.Initialize);
                Optional(typeof(SavedChunkValues), SavedChunkValues.Initialize);
                Optional(typeof(ServerAssetUnload), ServerAssetUnload.Initialize);
                Optional(typeof(ConnectionLog), patches => ConnectionLog.Initialize());
                Optional(typeof(FrameReports), FrameReports.Initialize);
                Optional(typeof(DropThatSyncCache), patches => DropThatSyncCache.Initialize(patches, Plugin.Log));
                Optional(typeof(DropThatParsing), patches => DropThatParsing.Initialize(patches, Plugin.Log));
                Optional(typeof(ConfigFileStats), ConfigFileStats.Initialize);
                Optional(typeof(BountyLedgerWrites), BountyLedgerWrites.Initialize);
                Optional(typeof(ServerListQueries), ServerListQueries.Initialize);
                Optional(typeof(QuickStackUnload), QuickStackUnload.Initialize);
                Optional(typeof(CraftyBoxesUnload), patches => CraftyBoxesUnload.Initialize(patches, Plugin.Log));
                Optional(typeof(InventoryLocks), InventoryLocks.Initialize);
                Optional(typeof(InventoryIconLifetime), InventoryIconLifetime.Initialize);
                Optional(typeof(StackInventoryLifetime), StackInventoryLifetime.Initialize);
                Optional(typeof(AttackSpeedHandlers), AttackSpeedHandlers.Initialize);
                Optional(typeof(MagicBackgroundRedraws), MagicBackgroundRedraws.Initialize);
                Optional(typeof(QualityAndDurabilityRedraws), QualityAndDurabilityRedraws.Initialize);
                Optional(typeof(FavoriteBorderRedraws), FavoriteBorderRedraws.Initialize);
                Optional(typeof(InventoryTooltips), InventoryTooltips.Initialize);
                Optional(typeof(HeldTextWrites), HeldTextWrites.Initialize);
                Optional(typeof(SwimmingEquipmentChecks), SwimmingEquipmentChecks.Initialize);
                Optional(typeof(ClockFontFallback), ClockFontFallback.Initialize);
                Optional(typeof(HivePollinationSearch), patches => HivePollinationSearch.Initialize(patches, Plugin.Log));
                Optional(typeof(ComfortChipRefresh), patches => ComfortChipRefresh.Initialize(patches, Plugin.Log));
                Optional(typeof(ComfortPlateHiding), patches => ComfortPlateHiding.Initialize(patches, Plugin.Log));
                Optional(typeof(FedoHudIcons), patches => FedoHudIcons.Initialize(patches, Plugin.Log));
                Optional(typeof(BetterUiHealthText), patches => BetterUiHealthText.Initialize(patches, Plugin.Log));
                Optional(typeof(KeepLitBurning), patches => KeepLitBurning.Initialize(patches, Plugin.Log));
                Optional(typeof(CookingSlotRepeats), patches => CookingSlotRepeats.Initialize(patches, Plugin.Log));
                Optional(typeof(CraftyBoxesLoading), patches => CraftyBoxesLoading.Initialize(patches, Plugin.Log));
                Optional(typeof(TameSitUpdate), patches => TameSitUpdate.Initialize(patches, Plugin.Log));
                Optional(typeof(TreadwellBrushes), patches => TreadwellBrushes.Initialize(patches, Plugin.Log));
                Optional(typeof(CraftyBoxesCounts), patches => CraftyBoxesCounts.Initialize(patches, Plugin.Log));
                Optional(typeof(SavedItemMagic), patches => SavedItemMagic.Initialize(patches, Plugin.Log));
                Optional(typeof(SavedItemCopies), patches => SavedItemCopies.Initialize(patches, Plugin.Log));
                Running = true;
                Plugin.Log.LogInfo("Independent game performance fixes initialized" + (deferred.Count == 0 ? "." : "; retained Progression Fix implementations keep their existing owner."));
            }
            catch (Exception error)
            {
                Stop();
                failures.Add(error.GetBaseException().Message);
                throw;
            }
        }

        private static void Optional(Type type, Action<PerformancePatches> initialize)
        {
            if (Previous(type)) return;
            var patches = new PerformancePatches(type.Name);
            try { initialize(patches); patches.Apply(); integrations.Add(patches); }
            catch (Exception error)
            {
                patches.Stop();
                string message = type.Name + " unavailable: " + error.GetBaseException().Message;
                failures.Add(message);
                Plugin.Fallback(message);
            }
        }

        internal static void Stop()
        {
            foreach (var patches in integrations.AsEnumerable().Reverse()) patches.Stop();
            integrations.Clear();
            core.UnpatchSelf();
            Running = false;
        }
    }
}
