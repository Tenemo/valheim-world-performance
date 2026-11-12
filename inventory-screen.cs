using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WorldPerformance
{
    // The open inventory redraws its grid and crafting panel every frame. Epic Loot, BetterUI and Quick Stack Store draw over
    // the grid in the same frame: Epic Loot switches every cell's rarity background off before switching it back on for each
    // magic and set item, BetterUI replaces the quality number the game has just written with stars, the game resets each
    // worn item's durability colour before BetterUI colours it, and Quick Stack Store gives every favourite item's border the
    // slot's colour and visibility before its own. Each of those changes and changes back, so Unity rebuilds those graphics
    // and the inventory's canvas every frame. Each cell is now written only with the state it ends the frame with, which is
    // what players see.
    internal static class InventoryScreen
    {
        private static bool lean = true;

        // Off draws natively; the isolated client switches it for paired measurements.
        internal static bool Lean { get => lean; set { lean = value; InventoryTooltips.Forget(); } }
    }

    internal static class MagicBackgroundRedraws
    {
        // Cells whose backgrounds Epic Loot hides at the start of the grid's update, and those whose item drew them again.
        private static readonly List<InventoryElement> cleared = new List<InventoryElement>();
        private static readonly HashSet<GameObject> drawn = new HashSet<GameObject>();

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var patch = assembly.GetType("EpicLoot.InventoryGrid_UpdateGui_MagicItemComponent_Patch", true);
            var api = assembly.GetType("EpicLoot.API", true);
            harmony.Patch(AccessTools.Method(patch, "UpdateGuiElements", new[] { typeof(InventoryElement), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(MagicBackgroundRedraws), nameof(Clear)));
            harmony.Patch(AccessTools.Method(api, "ApplyMagicItemBackground", new[] { typeof(GameObject), typeof(GameObject), typeof(ItemDrop.ItemData), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(MagicBackgroundRedraws), nameof(Drawn)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGrid), "UpdateGui"), prefix: new HarmonyMethod(typeof(MagicBackgroundRedraws), nameof(Begin)),
                postfix: new HarmonyMethod(typeof(MagicBackgroundRedraws), nameof(Finish)));
        }

        private static void Begin() { cleared.Clear(); drawn.Clear(); }

        // Epic Loot hides both backgrounds of every cell here; the cells whose item does not draw them again are hidden
        // when the grid's update ends instead.
        private static bool Clear(InventoryElement element, bool used)
        {
            if (!InventoryScreen.Lean) return true;
            element.m_used = used;
            cleared.Add(element);
            return false;
        }

        private static void Drawn(GameObject slotRoot, bool __result)
        {
            if (__result && slotRoot) drawn.Add(slotRoot);
        }

        private static void Finish()
        {
            foreach (var element in cleared)
                if (element && !drawn.Contains(element.gameObject)) { Hide(element.transform, "magicItem"); Hide(element.transform, "setItem"); }
            Begin();
        }

        private static void Hide(Transform cell, string name)
        {
            var child = cell.Find(name);
            var image = child ? child.GetComponent<Image>() : null;
            if (image) image.enabled = false;
        }
    }

    internal static class QualityAndDurabilityRedraws
    {
        private static ConfigEntry<bool> stars;
        private static ConfigEntryBase palette;
        private static FieldInfo activeColor;
        private static Color[] colors;
        private static readonly AccessTools.FieldRef<InventoryGrid, Inventory> inventory = AccessTools.FieldRefAccess<InventoryGrid, Inventory>("m_inventory");
        private static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> elements = AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
        private static readonly AccessTools.FieldRef<InventoryGrid, int> width = AccessTools.FieldRefAccess<InventoryGrid, int>("m_width");
        private static readonly AccessTools.FieldRef<InventoryGrid, int> height = AccessTools.FieldRefAccess<InventoryGrid, int>("m_height");
        // The labels and bars BetterUI writes over after the game's update in this frame.
        private static readonly HashSet<TMP_Text> starred = new HashSet<TMP_Text>();
        private static readonly HashSet<GuiBar> coloured = new HashSet<GuiBar>();

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.BetterUi, out var plugin)) return;
            var main = plugin.Instance.GetType();
            stars = AccessTools.Field(main, "showItemStars")?.GetValue(null) as ConfigEntry<bool>;
            palette = AccessTools.Field(main, "durabilityBarColorPalette")?.GetValue(null) as ConfigEntryBase;
            var bar = main.Assembly.GetType("BetterUI.Patches.DurabilityBar", true);
            activeColor = AccessTools.Field(bar, "activeColor");
            if (stars == null || palette == null || activeColor == null) throw new InvalidOperationException("BetterUI's inventory settings changed.");
            harmony.Patch(AccessTools.Method(typeof(InventoryGrid), "UpdateGui"), prefix: new HarmonyMethod(typeof(QualityAndDurabilityRedraws), nameof(Cover)),
                transpiler: new HarmonyMethod(typeof(QualityAndDurabilityRedraws), nameof(WriteFinalState)));
            harmony.Patch(AccessTools.Method(bar, "UpdateColor", new[] { typeof(GuiBar), typeof(float) }), prefix: new HarmonyMethod(typeof(QualityAndDurabilityRedraws), nameof(Colour)));
        }

        // BetterUI's postfix walks the grid's items in order and stops at the first one without a cell; it stars the
        // label of each upgradable item and colours the bar of each item with durability left.
        private static void Cover(InventoryGrid __instance)
        {
            starred.Clear();
            coloured.Clear();
            var items = inventory(__instance);
            // The game makes new cells when the grid's size changes; that frame stays as it is.
            if (!InventoryScreen.Lean || items == null || items.GetWidth() != width(__instance) || items.GetHeight() != height(__instance)) return;
            bool star = stars.Value, colour = palette.BoxedValue.ToString() != "Disabled";
            if (!star && !colour) return;
            var cells = elements(__instance);
            int columns = items.GetWidth();
            foreach (var item in items.GetAllItems())
            {
                int index = item.m_gridPos.y * columns + item.m_gridPos.x;
                var cell = index >= 0 && index < cells.Count ? cells[index] : null;
                if (cell == null) break;
                if (star && item.m_shared.m_maxQuality > 1) starred.Add(cell.m_quality);
                if (colour && item.m_shared.m_useDurability && item.m_durability > 0f) coloured.Add(cell.m_durability);
            }
        }

        private static IEnumerable<CodeInstruction> WriteFinalState(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var quality = AccessTools.Field(typeof(InventoryElement), nameof(InventoryElement.m_quality));
            var text = AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text));
            // The quality label's write is the text write a few instructions after loading the label.
            var writes = new HashSet<int>();
            for (int index = 0; index < code.Count; index++)
            {
                if (!code[index].LoadsField(quality)) continue;
                int write = code.FindIndex(index + 1, instruction => instruction.Calls(text));
                if (write > 0 && write - index <= 6) writes.Add(write);
            }
            if (writes.Count != 1) throw new InvalidOperationException("The game's inventory quality label changed.");
            code[writes.Single()].opcode = OpCodes.Call;
            code[writes.Single()].operand = AccessTools.Method(typeof(QualityAndDurabilityRedraws), nameof(SetQuality));
            return Transpilers.CallInstead(code, instruction => instruction.Calls(AccessTools.Method(typeof(GuiBar), nameof(GuiBar.ResetColor))),
                AccessTools.Method(typeof(QualityAndDurabilityRedraws), nameof(ResetColour)), 1, "The game's inventory durability colour changed.");
        }

        private static void SetQuality(TMP_Text label, string value)
        {
            if (!starred.Contains(label)) label.text = value;
        }

        private static void ResetColour(GuiBar bar)
        {
            if (!coloured.Contains(bar)) bar.ResetColor();
        }

        // BetterUI resets the bar's colour before setting its own; the bar now gets only the final colour.
        private static bool Colour(GuiBar durabilityBar, float durability)
        {
            if (!InventoryScreen.Lean) return true;
            durabilityBar.SetValue(durability);
            if (durability < 0f) { durabilityBar.ResetColor(); return false; }
            colors = colors ?? (Color[])activeColor.GetValue(null);
            durabilityBar.SetColor(colors[durability >= .75f ? 0 : durability >= .5f ? 1 : durability >= .25f ? 2 : 3]);
            return false;
        }
    }

    // Quick Stack Store's border writes are held until its update ends and then written once with their final values.
    internal static class FavoriteBorderRedraws
    {
        private struct Pending
        {
            internal Color Color;
            internal bool Coloured, Enabled, Switched;
        }

        private static readonly Dictionary<Behaviour, Pending> pending = new Dictionary<Behaviour, Pending>();

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.QuickStack, out var plugin)) return;
            var renderer = plugin.Instance.GetType().Assembly.GetType("QuickStackStore.BorderRenderer", true);
            harmony.Patch(AccessTools.Method(renderer, "UpdateGui"), transpiler: new HarmonyMethod(typeof(FavoriteBorderRedraws), nameof(Hold)),
                postfix: new HarmonyMethod(typeof(FavoriteBorderRedraws), nameof(Write)));
        }

        private static IEnumerable<CodeInstruction> Hold(IEnumerable<CodeInstruction> instructions)
        {
            const string changed = "Quick Stack Store's favourite borders changed.";
            var code = Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.PropertySetter(typeof(Graphic), nameof(Graphic.color))),
                AccessTools.Method(typeof(FavoriteBorderRedraws), nameof(SetColor)), 5, changed);
            code = Transpilers.CallInstead(code, instruction => instruction.Calls(AccessTools.PropertySetter(typeof(Behaviour), nameof(Behaviour.enabled))),
                AccessTools.Method(typeof(FavoriteBorderRedraws), nameof(SetEnabled)), 3, changed);
            return Transpilers.CallInstead(code, instruction => instruction.Calls(AccessTools.PropertyGetter(typeof(Behaviour), nameof(Behaviour.enabled))),
                AccessTools.Method(typeof(FavoriteBorderRedraws), nameof(GetEnabled)), 4, changed);
        }

        private static void SetColor(Graphic graphic, Color color)
        {
            if (!InventoryScreen.Lean) { graphic.color = color; return; }
            pending.TryGetValue(graphic, out var state);
            state.Color = color;
            state.Coloured = true;
            pending[graphic] = state;
        }

        private static void SetEnabled(Behaviour behaviour, bool enabled)
        {
            if (!InventoryScreen.Lean) { behaviour.enabled = enabled; return; }
            pending.TryGetValue(behaviour, out var state);
            state.Enabled = enabled;
            state.Switched = true;
            pending[behaviour] = state;
        }

        private static bool GetEnabled(Behaviour behaviour) => pending.TryGetValue(behaviour, out var state) && state.Switched ? state.Enabled : behaviour.enabled;

        private static void Write()
        {
            foreach (var entry in pending)
            {
                if (!entry.Key) continue;
                if (entry.Value.Coloured) ((Graphic)entry.Key).color = entry.Value.Color;
                if (entry.Value.Switched) entry.Key.enabled = entry.Value.Enabled;
            }
            pending.Clear();
        }
    }

    // The crafting panel generated the selected recipe's description, and the grid the hovered item's tooltip, again every
    // frame; with Epic Loot's and Progression Fix's additions those were the open inventory's largest script cost. Each is
    // generated again as soon as the recipe, its quality or amount, the hovered item, the held Shift, Ctrl or Alt keys, the
    // world level, any inventory or the player's equipment changes or an item is repaired, and at least twice a second for
    // anything else it shows.
    internal static class InventoryTooltips
    {
        private const float Refresh = .5f;
        private static Action<InventoryGrid, ItemDrop.ItemData, UITooltip> createItemTooltip;
        private static ItemDrop.ItemData recipeItem, hoveredItem;
        private static UITooltip hoveredTooltip;
        private static int quality, amount, version, describedVersion, hoveredVersion, describedKeys, hoveredKeys;
        private static bool crafting, appended;
        private static float worldLevel, describedUntil, hoveredUntil;
        private static string description, localizedFrom, localized, hoveredText;

        internal static void Initialize(PerformancePatches harmony)
        {
            var create = AccessTools.Method(typeof(InventoryGrid), "CreateItemTooltip", new[] { typeof(ItemDrop.ItemData), typeof(UITooltip) });
            createItemTooltip = AccessTools.MethodDelegate<Action<InventoryGrid, ItemDrop.ItemData, UITooltip>>(create);
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "UpdateRecipe"), transpiler: new HarmonyMethod(typeof(InventoryTooltips), nameof(ReuseDescription)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGrid), "UpdateGui"), transpiler: new HarmonyMethod(typeof(InventoryTooltips), nameof(ReuseHovered)));
            harmony.Patch(AccessTools.Method(typeof(Inventory), "Changed"), postfix: new HarmonyMethod(typeof(InventoryTooltips), nameof(Changed)));
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "SetupEquipment"), postfix: new HarmonyMethod(typeof(InventoryTooltips), nameof(Changed)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "RepairOneItem"), postfix: new HarmonyMethod(typeof(InventoryTooltips), nameof(Changed)));
        }

        internal static void Forget() { description = null; hoveredItem = null; }

        private static void Changed() => version++;

        // Tooltips show more while a player holds Shift, Ctrl or Alt.
        private static int HeldKeys() => (ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift) ? 1 : 0) |
            (ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl) ? 2 : 0) | (ZInput.GetKey(KeyCode.LeftAlt) || ZInput.GetKey(KeyCode.RightAlt) ? 4 : 0);

        private static IEnumerable<CodeInstruction> ReuseDescription(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var tooltip = AccessTools.Method(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
                new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) });
            var localize = AccessTools.Method(typeof(Localization), nameof(Localization.Localize), new[] { typeof(string) });
            var sites = Enumerable.Range(0, code.Count - 1).Where(index => code[index].Calls(tooltip) && code[index + 1].Calls(localize)).ToList();
            if (sites.Count != 1) throw new InvalidOperationException("The game's recipe description changed.");
            code[sites[0]].opcode = OpCodes.Call;
            code[sites[0]].operand = AccessTools.Method(typeof(InventoryTooltips), nameof(Describe));
            code[sites[0] + 1].opcode = OpCodes.Call;
            code[sites[0] + 1].operand = AccessTools.Method(typeof(InventoryTooltips), nameof(Localize));
            return code;
        }

        private static IEnumerable<CodeInstruction> ReuseHovered(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(InventoryGrid), "CreateItemTooltip")),
                AccessTools.Method(typeof(InventoryTooltips), nameof(CreateItemTooltip)), 1, "The game's hovered item tooltip changed.");

        private static string Describe(ItemDrop.ItemData template, int qualityLevel, bool forCrafting, float level, int stack, bool appending)
        {
            if (!InventoryScreen.Lean) return ItemDrop.ItemData.GetTooltip(template, qualityLevel, forCrafting, level, stack, appending);
            float now = Time.unscaledTime;
            int keys = HeldKeys();
            if (description == null || template != recipeItem || qualityLevel != quality || forCrafting != crafting || level != worldLevel || stack != amount ||
                appending != appended || keys != describedKeys || version != describedVersion || now >= describedUntil)
            {
                description = ItemDrop.ItemData.GetTooltip(template, qualityLevel, forCrafting, level, stack, appending);
                recipeItem = template; quality = qualityLevel; crafting = forCrafting; worldLevel = level; amount = stack; appended = appending;
                describedKeys = keys; describedVersion = version;
                describedUntil = now + Refresh;
            }
            return description;
        }

        private static string Localize(Localization localization, string text)
        {
            if (!InventoryScreen.Lean || text == null || !ReferenceEquals(text, description)) return localization.Localize(text);
            if (!ReferenceEquals(text, localizedFrom)) { localized = localization.Localize(text); localizedFrom = text; }
            return localized;
        }

        // The tooltip keeps the text it was given, so an unchanged item needs no new one.
        private static void CreateItemTooltip(InventoryGrid grid, ItemDrop.ItemData item, UITooltip tooltip)
        {
            float now = Time.unscaledTime;
            int keys = HeldKeys();
            if (InventoryScreen.Lean && item == hoveredItem && tooltip == hoveredTooltip && keys == hoveredKeys && version == hoveredVersion && now < hoveredUntil &&
                ReferenceEquals(tooltip.m_text, hoveredText))
                return;
            createItemTooltip(grid, item, tooltip);
            hoveredItem = item; hoveredTooltip = tooltip; hoveredKeys = keys; hoveredVersion = version; hoveredText = tooltip.m_text;
            hoveredUntil = now + Refresh;
        }
    }
}
