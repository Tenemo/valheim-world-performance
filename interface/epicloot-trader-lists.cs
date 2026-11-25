using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Epic Loot 0.15.6's trader window destroys and recreates every row of a bounty list each time it redraws one, and a
    // claim or an abandon redraws all five of its lists, whose shop rows instantiate each item's prefab to read it. With 130
    // open bounties a claim took 120-230 ms and the next frame 45-65 ms. A bounty list now keeps the rows of the bounties it
    // still lists, draws again only those whose bounty changed, creates only the missing ones and removes the rest, in the
    // order and with the selection Epic Loot leaves. A claim or an abandon redraws the two bounty lists; the shop lists, which
    // neither can change unless the player learns new materials, recipes or biomes from the reward, keep their rows and lose
    // their selection as a redraw does.
    internal static class EpicLootTraderLists
    {
        // The shipped feature; the isolated client switches it off and on for checks and paired measurements.
        internal static bool Shortcut = true;
        internal static long Kept, Drawn, Created, Removed, ShopsKept, ShopsRedrawn;

        private sealed class Knowledge { internal int Materials, Recipes, Biomes; }

        private static Type claimableType, availableType, shopType, gambleType, mapType, rowType;
        private static MethodInfo refreshItems, refreshButton, clearSelection, needsRefresh, forEachPanel, currenciesOf, setItem, toPackage, selected, setSelected;
        private static MethodInfo claimable, available, interval;
        private static FieldInfo panels, list, prefab, current, selectedIndex, bountyOf, onSelected, bounties;
        private static PropertyInfo id;
        private static Type stepType, selectionType;
        private static ConditionalWeakTable<object, Knowledge> builtWith = new ConditionalWeakTable<object, Knowledge>();
        private static readonly Dictionary<int, byte[]> drawnWith = new Dictionary<int, byte[]>();
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> knownMaterials = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownMaterial");
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> knownRecipes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> knownBiomes = AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownBiome");
        private static readonly List<MethodBase> replaced = new List<MethodBase>();
        private static bool bountyChange;
        // 0 until the first redraw reads the patches, then 1 when the lists are kept here, -1 when Epic Loot redraws them.
        private static int ready;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var merchant = assembly.GetType("EpicLoot.Adventure.MerchantPanel");
            var panel = assembly.GetType("EpicLoot.Adventure.Feature.IMerchantListPanel");
            claimableType = assembly.GetType("EpicLoot.Adventure.Feature.ClaimableBountiesListPanel");
            availableType = assembly.GetType("EpicLoot.Adventure.Feature.AvailableBountiesListPanel");
            shopType = assembly.GetType("EpicLoot.Adventure.Feature.SecretStashListPanel");
            gambleType = assembly.GetType("EpicLoot.Adventure.Feature.GambleListPanel");
            mapType = assembly.GetType("EpicLoot.Adventure.Feature.TreasureMapListPanel");
            rowType = assembly.GetType("EpicLoot.Adventure.BountyListElement");
            var bounty = assembly.GetType("EpicLoot.Adventure.BountyInfo");
            var currencies = assembly.GetType("EpicLoot.Adventure.Currencies");
            var manager = assembly.GetType("EpicLoot.Adventure.AdventureDataManager");
            var feature = assembly.GetType("EpicLoot.Adventure.Feature.BountiesAdventureFeature");
            if (merchant == null || panel == null || claimableType == null || availableType == null || shopType == null || gambleType == null || mapType == null ||
                rowType == null || bounty == null || currencies == null || manager == null || feature == null)
                throw new InvalidOperationException("Epic Loot's trader window changed.");
            var basePanel = claimableType.BaseType;
            refreshItems = AccessTools.Method(panel, "RefreshItems", new[] { currencies });
            refreshButton = AccessTools.Method(panel, "RefreshButton", new[] { currencies });
            clearSelection = AccessTools.Method(panel, "ClearSelection", Type.EmptyTypes);
            needsRefresh = AccessTools.Method(panel, "NeedsRefresh", Type.EmptyTypes);
            forEachPanel = AccessTools.Method(merchant, "ForEachPanel", new[] { typeof(string), typeof(Action<>).MakeGenericType(panel) });
            currenciesOf = AccessTools.Method(merchant, "GetPlayerCurrencies", Type.EmptyTypes);
            panels = AccessTools.Field(merchant, "Panels");
            list = AccessTools.Field(basePanel, "List");
            prefab = AccessTools.Field(basePanel, "ElementPrefab");
            current = AccessTools.Field(basePanel, "_currentInterval");
            selectedIndex = AccessTools.Field(basePanel, "_selectedItemIndex");
            selected = AccessTools.Method(basePanel, "OnItemSelected", new[] { typeof(int) });
            bountyOf = AccessTools.Field(rowType, "BountyInfo");
            onSelected = AccessTools.Field(rowType, "OnSelected");
            setItem = AccessTools.Method(rowType, "SetItem", new[] { bounty });
            setSelected = AccessTools.Method(rowType, "SetSelected", new[] { typeof(bool) });
            toPackage = AccessTools.Method(bounty, "ToPackage", new[] { typeof(ZPackage) });
            id = AccessTools.Property(bounty, "ID");
            bounties = AccessTools.Field(manager, "Bounties");
            claimable = AccessTools.Method(feature, "GetClaimableBounties", Type.EmptyTypes);
            available = AccessTools.Method(feature, "GetAvailableBounties", Type.EmptyTypes);
            interval = AccessTools.Method(feature, "GetCurrentInterval", Type.EmptyTypes);
            var claim = AccessTools.DeclaredMethod(claimableType, "OnMainButtonClicked");
            var abandon = AccessTools.DeclaredMethod(merchant, "OnAbandonBounty");
            var refreshAll = AccessTools.DeclaredMethod(merchant, "RefreshAll");
            var claimableItems = AccessTools.DeclaredMethod(claimableType, "RefreshItems", new[] { currencies });
            var availableItems = AccessTools.DeclaredMethod(availableType, "RefreshItems", new[] { currencies });
            var destroyAll = AccessTools.Method(basePanel, "DestroyAllListElementsInList");
            if (refreshItems == null || refreshButton == null || clearSelection == null || needsRefresh == null || needsRefresh.ReturnType != typeof(bool) || forEachPanel == null || currenciesOf == null || panels == null || list == null ||
                list.FieldType != typeof(RectTransform) || prefab == null || prefab.FieldType != rowType || current == null || current.FieldType != typeof(int) ||
                selectedIndex == null || selectedIndex.FieldType != typeof(int) || selected == null || bountyOf == null || bountyOf.FieldType != bounty ||
                onSelected == null || onSelected.FieldType != typeof(Action<>).MakeGenericType(bounty) || setItem == null || setSelected == null || toPackage == null ||
                id == null || id.PropertyType != typeof(string) || bounties == null || bounties.FieldType != feature || claimable == null || available == null ||
                interval == null || claim == null || abandon == null || refreshAll == null || claimableItems == null || availableItems == null || destroyAll == null ||
                basePanel != availableType.BaseType || !typeof(IList).IsAssignableFrom(claimable.ReturnType) || !typeof(IList).IsAssignableFrom(available.ReturnType))
                throw new InvalidOperationException("Epic Loot's trader window changed.");
            stepType = typeof(Action<>).MakeGenericType(panel);
            selectionType = onSelected.FieldType;
            // The redraws this replaces or leaves out; another mod's patch on them turns this off and fails mod-compatibility.
            replaced.AddRange(new MethodBase[] { refreshAll, claimableItems, availableItems, destroyAll, setItem,
                AccessTools.DeclaredMethod(shopType, "RefreshItems", new[] { currencies }), AccessTools.DeclaredMethod(gambleType, "RefreshItems", new[] { currencies }),
                AccessTools.DeclaredMethod(mapType, "RefreshItems", new[] { currencies }), AccessTools.Method(rowType, "Awake"), AccessTools.Method(rowType, "GetTooltip"),
                AccessTools.Method(rowType, "GetDisplayName") });
            if (replaced.Any(method => method == null)) throw new InvalidOperationException("Epic Loot's trader window changed.");
            var self = typeof(EpicLootTraderLists);
            harmony.Patch(claim, prefix: new HarmonyMethod(self, nameof(BountyChange)), finalizer: new HarmonyMethod(self, nameof(BountyChanged)));
            harmony.Patch(abandon, prefix: new HarmonyMethod(self, nameof(BountyChange)), finalizer: new HarmonyMethod(self, nameof(BountyChanged)));
            harmony.Patch(refreshAll, prefix: new HarmonyMethod(self, nameof(RefreshAll)));
            harmony.Patch(claimableItems, prefix: new HarmonyMethod(self, nameof(RefreshClaimable)));
            harmony.Patch(availableItems, prefix: new HarmonyMethod(self, nameof(RefreshAvailable)));
            foreach (var shop in new[] { shopType, gambleType, mapType })
                harmony.Patch(AccessTools.DeclaredMethod(shop, "RefreshItems", new[] { currencies }), postfix: new HarmonyMethod(self, nameof(ShopBuilt)));
            log.LogInfo("Epic Loot's trader keeps its bounty rows and redraws only the lists a claim or an abandon changes.");
        }

        private static bool Ready()
        {
            if (ready != 0) return ready > 0;
            // Progression Fix's tooltip patch only replaces a fixed text.
            var problems = replaced.Select(method => PatchSets.Unknown(method, "EpicLootProgressionFix.BountyObjectiveText.Transpiler")).Where(problem => problem != null).ToList();
            ready = problems.Count == 0 ? 1 : -1;
            if (problems.Count > 0) Plugin.Fallback("EpicLootTraderLists unavailable: " + string.Join("; ", problems));
            return ready > 0;
        }

        private static void BountyChange() => bountyChange = true;
        private static void BountyChanged() => bountyChange = false;

        private static Knowledge Known()
        {
            var player = Player.m_localPlayer;
            return player == null ? new Knowledge { Materials = -1 } :
                new Knowledge { Materials = knownMaterials(player).Count, Recipes = knownRecipes(player).Count, Biomes = knownBiomes(player).Count };
        }

        private static void ShopBuilt(object __instance)
        {
            builtWith.Remove(__instance);
            builtWith.Add(__instance, Known());
        }

        // MerchantPanel.RefreshAll during a claim or an abandon: the bounty lists are redrawn, a shop list only when the reward
        // taught the player something its stock depends on, and every list's button is refreshed, each step as Epic Loot
        // guards it.
        private static bool RefreshAll(object __instance)
        {
            if (!Shortcut || !bountyChange || !Ready()) return true;
            var step = new Step(Known(), Call(currenciesOf, __instance));
            Call(forEachPanel, __instance, "RefreshAll", Delegate.CreateDelegate(stepType, step, Step.Method));
            return false;
        }

        private sealed class Step
        {
            internal static readonly MethodInfo Method = AccessTools.Method(typeof(Step), nameof(Run));
            private readonly Knowledge known;
            private readonly object[] money;
            internal Step(Knowledge known, object money) { this.known = known; this.money = new[] { money }; }

            private void Run(object panel)
            {
                var type = panel.GetType();
                bool bountyList = type == claimableType || type == availableType;
                bool keep = !bountyList && (type == shopType || type == gambleType || type == mapType) && !(bool)Call(needsRefresh, panel) &&
                    builtWith.TryGetValue(panel, out var built) && built.Materials == known.Materials && built.Recipes == known.Recipes && built.Biomes == known.Biomes;
                if (keep) { ShopsKept++; Call(clearSelection, panel); }
                else { if (!bountyList) ShopsRedrawn++; Call(refreshItems, panel, money); }
                Call(refreshButton, panel, money);
            }
        }

        // A reflected call that throws what the method throws, as Epic Loot's own call would.
        private static object Call(MethodInfo method, object instance, params object[] arguments)
        {
            try { return method.Invoke(instance, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static bool RefreshClaimable(object __instance) => !Keep(__instance, claimable);
        private static bool RefreshAvailable(object __instance) => !Keep(__instance, available);

        // A bounty list's RefreshItems with its rows kept. Returns false, leaving the redraw to Epic Loot, when the list holds
        // anything but one row for each of distinct bounties.
        private static bool Keep(object panel, MethodInfo listed)
        {
            if (!Shortcut || !Ready()) return false;
            var feature = bounties.GetValue(null);
            var items = (IList)Call(listed, feature);
            var parent = (RectTransform)list.GetValue(panel);
            var ids = new string[items.Count];
            var wanted = new Dictionary<string, int>(items.Count);
            for (int index = 0; index < items.Count; index++)
                if (items[index] == null || (ids[index] = (string)id.GetValue(items[index], null)) == null || wanted.ContainsKey(ids[index])) return Native(panel, feature);
                else wanted[ids[index]] = index;
            var rows = new Dictionary<string, Component>(parent.childCount);
            for (int index = 0; index < parent.childCount; index++)
            {
                var row = parent.GetChild(index).GetComponent(rowType);
                var info = row == null ? null : bountyOf.GetValue(row);
                string key = info == null ? null : (string)id.GetValue(info, null);
                if (key == null || rows.ContainsKey(key)) return Native(panel, feature);
                rows[key] = row;
            }
            current.SetValue(panel, Call(interval, feature));
            // Rows whose bounty is no longer listed go first, so the list holds exactly its rows at every step.
            for (int index = parent.childCount - 1; index >= 0; index--)
            {
                var child = parent.GetChild(index);
                var row = child.GetComponent(rowType);
                string key = (string)id.GetValue(bountyOf.GetValue(row), null);
                if (wanted.ContainsKey(key)) continue;
                rows.Remove(key);
                drawnWith.Remove(row.GetInstanceID());
                Removed++;
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
            selectedIndex.SetValue(panel, -1);
            var template = (Component)prefab.GetValue(panel);
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index];
                byte[] content = Content(item);
                if (rows.TryGetValue(ids[index], out var row) && drawnWith.TryGetValue(row.GetInstanceID(), out var drawn) && drawn.SequenceEqual(content))
                {
                    // Every value the row shows comes from these, so only the bounty it hands to the buttons changes.
                    bountyOf.SetValue(row, item);
                    Kept++;
                }
                else
                {
                    if (row == null)
                    {
                        row = (Component)UnityEngine.Object.Instantiate(template, parent);
                        row.gameObject.SetActive(true);
                        Created++;
                    }
                    else Drawn++;
                    Call(setItem, row, item);
                    drawnWith[row.GetInstanceID()] = content;
                }
                row.transform.SetSiblingIndex(index);
                onSelected.SetValue(row, Delegate.CreateDelegate(selectionType, new Selection(panel, index), Selection.Method));
                Call(setSelected, row, false);
            }
            return true;
        }

        // Epic Loot's own redraw, when its rows cannot be matched to bounties; the rows it destroys are forgotten.
        private static bool Native(object panel, object feature)
        {
            var parent = (RectTransform)list.GetValue(panel);
            for (int index = 0; index < parent.childCount; index++) drawnWith.Remove(parent.GetChild(index).GetComponent(rowType)?.GetInstanceID() ?? 0);
            return false;
        }

        // What a row shows of its bounty: everything the bounty saves, and the language the texts are in.
        private static byte[] Content(object bounty)
        {
            var package = new ZPackage();
            package.Write(Localization.instance.GetSelectedLanguage());
            Call(toPackage, bounty, package);
            return package.GetArray();
        }

        // A row's OnSelected as Epic Loot subscribes it: selecting the row selects its index in its list.
        private sealed class Selection
        {
            internal static readonly MethodInfo Method = AccessTools.Method(typeof(Selection), nameof(Select));
            private readonly object panel;
            private readonly object[] index;
            internal Selection(object panel, int index) { this.panel = panel; this.index = new object[] { index }; }
            private void Select(object bounty) => Call(selected, panel, index);
        }
    }
}
