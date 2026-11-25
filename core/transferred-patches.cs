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
        internal const string StarLevelSystem = "MidnightsFX.StarLevelSystem";
        internal const string Jotunn = "com.jotunn.jotunn";
        internal const string Sailing = "org.bepinex.plugins.sailing";
        internal const string ConditionalConfigSync = "_shudnal.ConditionalConfigSync";
        internal const string Dyrr = "ezomic.valheim.dyrr";
        internal const string AzuAntiArthriticCrafting = "Azumatt.AzuAntiArthriticCrafting";
        private static readonly Dictionary<string, string[]> Supported = new Dictionary<string, string[]>
        {
            { QuickStack, new[] { "1.4.16" } }, { DropThat, new[] { "3.1.7" } },
            { AzuCraftyBoxes, new[] { "1.8.28" } }, { BetterUi, new[] { "2.5.13", "2.5.14" } },
            { ItemStacks, new[] { "1.1.1" } }, { MultiUserChest, new[] { "0.6.2" } }, { FastLink, new[] { "1.5.2", "1.5.3" } },
            { EpicLoot, new[] { "0.15.4", "0.15.5", "0.15.6" } }, { EquipmentAndQuickSlots, new[] { "3.2.1" } }, { WieldWhileSwimming, new[] { "1.1.4" } },
            { AzuClock, new[] { "1.1.1" } }, { BetterBeehives, new[] { "1.3.1" } }, { ComfortMe, new[] { "1.3.1" } },
            { FedoHud, new[] { "1.1.2" } }, { NoSmokeStayLit, new[] { "2.4.4" } }, { TameRidingOverhaul, new[] { "0.1.20" } },
            { Treadwell, new[] { "1.0.1" } }, { StarLevelSystem, new[] { "1.33.0", "1.34.0" } },
            { Jotunn, new[] { "2.30.2" } },
            { Sailing, new[] { "1.1.12" } },
            { ConditionalConfigSync, new[] { "1.0.11" } },
            { Dyrr, new[] { "1.4.2" } },
            { AzuAntiArthriticCrafting, new[] { "2.1.12" } },
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
        private static readonly List<Harmony> fixes = new List<Harmony>();
        private static readonly List<PerformancePatches> integrations = new List<PerformancePatches>();
        private static readonly HashSet<string> olderImplementations = new HashSet<string>();
        private static readonly HashSet<string> deferred = new HashSet<string>();
        private static readonly List<string> failures = new List<string>();
        internal static string Failure => string.Join("\n", failures);
        internal static bool Running { get; private set; }

        // Progression Fix 1.11.0 handed these implementations to this mod; only an older release still carries them.
        private static readonly System.Version HandedOver = new System.Version(1, 11, 0);

        // New Progression Fix requires this mod, so a reverse soft dependency would make a cycle. Read the installed
        // plugin's metadata before either load order: only an actual retained implementation in a release older than the
        // hand-over defers that feature, so a later class that happens to share a name leaves this mod's feature running.
        private static void FindPreviousOwner()
        {
            foreach (string path in Directory.EnumerateFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
            {
                AssemblyDefinition assembly;
                try { assembly = AssemblyDefinition.ReadAssembly(path); }
                catch (BadImageFormatException) { continue; }
                using (assembly)
                {
                    var plugin = assembly.MainModule.Types.SelectMany(type => type.CustomAttributes).FirstOrDefault(attribute =>
                        attribute.AttributeType.FullName == typeof(BepInPlugin).FullName && attribute.ConstructorArguments.Count == 3 &&
                        (string)attribute.ConstructorArguments[0].Value == "valheim.epiclootprogressionfix");
                    if (plugin == null || !System.Version.TryParse(plugin.ConstructorArguments[2].Value as string, out var version) || version >= HandedOver) continue;
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

        private static void PatchTree(Harmony harmony, Type type)
        {
            if (type.ContainsGenericParameters || type.IsDefined(typeof(CompilerGeneratedAttribute), false)) return;
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            if (type.IsDefined(typeof(HarmonyPatch), false)) harmony.CreateClassProcessor(type).Patch();
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) PatchTree(harmony, nested);
        }

        // Each fix patches under an owner of its own, so a changed game method takes only that fix off and reports it.
        private static void Fix(Type type)
        {
            if (Previous(type)) return;
            var harmony = new Harmony(Plugin.Id + ".game-fixes." + type.Name);
            try { PatchTree(harmony, type); fixes.Add(harmony); }
            catch (Exception error)
            {
                harmony.UnpatchOwner();
                string message = type.Name + " unavailable: " + error.GetBaseException().Message;
                failures.Add(message);
                Plugin.Fallback(message);
            }
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
                    typeof(DamageNumberFading), typeof(DamageNumberReuse), typeof(GhostDungeonRooms),
                    typeof(ConfigWatcherListings), typeof(PacedGrassRebuilds),
                    typeof(LoadingScreenPriority), typeof(ArrivalConfirmation),
                    typeof(AsyncLocationSpawns) })
                    Fix(type);
                // This is the synchronous fallback of the existing map cache and background packing.
                Fix(typeof(MapSaveWrites));
                Optional(typeof(SendWindow), patches => SendWindow.Initialize(patches, Plugin.Log));
                Optional(typeof(OwnershipOrder), OwnershipOrder.Initialize);
                Optional(typeof(ReceivedRecordSaves), ReceivedRecordSaves.Initialize);
                Optional(typeof(SavedChunkValues), SavedChunkValues.Initialize);
                Optional(typeof(ServerAssetUnload), ServerAssetUnload.Initialize);
                Optional(typeof(ConnectionLog), ConnectionLog.Initialize);
                Optional(typeof(FrameReports), FrameReports.Initialize);
                Optional(typeof(DropThatSyncCache), patches => DropThatSyncCache.Initialize(patches, Plugin.Log));
                Optional(typeof(DropThatParsing), patches => DropThatParsing.Initialize(patches, Plugin.Log));
                Optional(typeof(ConfigFileStats), ConfigFileStats.Initialize);
                Optional(typeof(BountyLedgerWrites), BountyLedgerWrites.Initialize);
                Optional(typeof(ServerListQueries), ServerListQueries.Initialize);
                Optional(typeof(SteamServerJoins), SteamServerJoins.Initialize);
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
                Optional(typeof(CraftyBoxesPrefabNames), patches => CraftyBoxesPrefabNames.Initialize(patches, Plugin.Log));
                Optional(typeof(SavedItemMagic), patches => SavedItemMagic.Initialize(patches, Plugin.Log));
                Optional(typeof(SavedItemCopies), patches => SavedItemCopies.Initialize(patches, Plugin.Log));
                Optional(typeof(OwnedCopies), OwnedCopies.Initialize);
                Optional(typeof(ColoredCreatureCopies), patches => ColoredCreatureCopies.Initialize(patches, Plugin.Log));
                Optional(typeof(FloatingWaterCache), FloatingWaterCache.Initialize);
                Optional(typeof(LightFlickerAngles), LightFlickerAngles.Initialize);
                Optional(typeof(PlainTextTags), PlainTextTags.Initialize);
                Optional(typeof(StatusEffectParts), StatusEffectParts.Initialize);
                Optional(typeof(CharacterStepTrims), CharacterStepTrims.Initialize);
                Optional(typeof(SteadySoundLevels), SteadySoundLevels.Initialize);
                Optional(typeof(CoverChecks), CoverChecks.Initialize);
                Optional(typeof(CookingSlotKeys), CookingSlotKeys.Initialize);
                Optional(typeof(PropertyNameHashes), PropertyNameHashes.Initialize);
                Optional(typeof(HudNumberTexts), HudNumberTexts.Initialize);
                Optional(typeof(InstanceCameraPosition), InstanceCameraPosition.Initialize);
                Optional(typeof(InputCheckOrder), InputCheckOrder.Initialize);
                Optional(typeof(JotunnButtonTwins), patches => JotunnButtonTwins.Initialize(patches, Plugin.Log));
                Optional(typeof(SailingShipUser), patches => SailingShipUser.Initialize(patches, Plugin.Log));
                Optional(typeof(ConfigSyncFlush), patches => ConfigSyncFlush.Initialize(patches, Plugin.Log));
                Optional(typeof(EpicLootLowHealth), patches => EpicLootLowHealth.Initialize(patches, Plugin.Log));
                Optional(typeof(EpicLootFrameTrims), patches => EpicLootFrameTrims.Initialize(patches, Plugin.Log));
                Optional(typeof(EpicLootConvertRefresh), patches => EpicLootConvertRefresh.Initialize(patches, Plugin.Log));
                Optional(typeof(EpicLootSelectAll), patches => EpicLootSelectAll.Initialize(patches, Plugin.Log));
                Optional(typeof(SettledAnswers), patches => SettledAnswers.Initialize(patches, Plugin.Log));
                Optional(typeof(PlayerFirstChecks), patches => PlayerFirstChecks.Initialize(patches, Plugin.Log));
                Optional(typeof(DyrrPlayerId), patches => DyrrPlayerId.Initialize(patches, Plugin.Log));
                Optional(typeof(ComfortStatusList), patches => ComfortStatusList.Initialize(patches, Plugin.Log));
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
            foreach (var harmony in fixes.AsEnumerable().Reverse()) harmony.UnpatchOwner();
            fixes.Clear();
            Running = false;
        }
    }
}
