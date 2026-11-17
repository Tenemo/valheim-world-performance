using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Several mods ask every frame whether the player may use input, which runs Player.TakeInput's ten window checks and
    // its patches, before the cheap test that decides whether they have anything to do:
    // - Equipment and Quick Slots 3.2.1 asks before reading its quick slots' controller buttons, which the pack leaves unset,
    //   and before checking each filled quick slot's key. It now reads the buttons first, and the key first.
    // - Epic Loot 0.15.6's ability bar asks before its keyboard keys, and twice more before its controller buttons, though a
    //   slot without an ability, or without an activated ability bound to a button, does nothing. It now asks only when
    //   a slot could act.
    // - Quick Stack Store 1.4.16 checks a dozen windows before reading its seven keys, and AzuClock 1.1.1 five before its
    //   clock key. Each now reads its keys first and checks the windows only when one is pressed.
    // Every check left out or moved only reads the game's state, as do the patches of the other mods on them, read once the
    // player is in the world: any other patch on them turns this off and fails mod-compatibility. Epic Loot's spirit animal patch
    // on Player.TakeInput clears its naming popup's flag after the popup closes, so while that flag is set the checks run
    // as before; Equipment and Quick Slots' patches on the game's button reads keep a cache for buttons that share a key
    // with a quick slot, so while the controller modifier is one of those, Epic Loot's controller checks run as before.
    // Progression Fix's chest search reads keys and buttons as not pressed while its field has the keyboard; a key that
    // reads as not pressed makes these checks do nothing in either order.
    internal static class InputCheckOrder
    {
        internal static long Skipped, Asked;
        private static readonly Func<Character, bool> takeInput = AccessTools.MethodDelegate<Func<Character, bool>>(AccessTools.Method(typeof(Character), "TakeInput"));
        private static readonly List<MethodBase> skippable = new List<MethodBase>();
        // The other mods' patches on them, each only reading or, as described above, changing what is described; the isolated
        // test client adds its input simulation's.
        internal static readonly List<string> Known = new List<string>();
        private static AccessTools.FieldRef<bool> popupOpen;
        private static HashSet<string> similarNames;
        private static Func<MonoBehaviour, int> abilityCount;
        private static Func<MonoBehaviour, int, bool> slotTakesButton;
        private static Func<int, int> slotButton;
        private static int noButton;
        private static Func<bool> canUseCombos, canUseHotkeys, quickStackIgnores;
        private static Func<KeyboardShortcut, bool> shortcutDown;
        private static AccessTools.FieldRef<ConfigEntry<KeyboardShortcut>>[] quickStackKeys;
        private static AccessTools.FieldRef<ConfigEntry<bool>> hardcodedController;
        private static AccessTools.FieldRef<ConfigEntry<KeyboardShortcut>> clockKey;
        private static Func<bool, bool> clockIgnores;
        // 0 until the first call reads the patches, then 1 when the checks are reordered, -1 when they run as before.
        private static int ready;

        internal static void Initialize(PerformancePatches harmony)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            bool epicLoot = PluginIds.TryGet(PluginIds.EpicLoot, out var epicLootPlugin), slots = PluginIds.TryGet(PluginIds.EquipmentAndQuickSlots, out var slotsPlugin),
                quickStack = PluginIds.TryGet(PluginIds.QuickStack, out var quickStackPlugin), clock = PluginIds.TryGet(PluginIds.AzuClock, out var clockPlugin);
            if (!epicLoot && !slots && !quickStack && !clock) return;
            Skippable(typeof(Player), "TakeInput", "InCutscene", "IsTeleporting", "IsDead", "InPlaceMode");
            Skippable(typeof(Hud), "IsPieceSelectionVisible", "InRadial");
            Skippable(typeof(ZInput), "GetButton", "TryGetButtonState", "GetKeyDown", "GetKey", "TryGetKeyStateLowLevel", "IsGamepadActive");
            Skippable(typeof(Minimap), "IsOpen"); Skippable(typeof(Menu), "IsVisible"); Skippable(typeof(global::Console), "IsVisible");
            Skippable(typeof(StoreGui), "IsVisible"); Skippable(typeof(TextInput), "IsVisible"); Skippable(typeof(Chat), "HasFocus");
            Skippable(typeof(ZNet), "InPasswordDialog"); Skippable(typeof(TextViewer), "IsVisible"); Skippable(typeof(InventoryGui), "IsVisible");
            Skippable(typeof(GameCamera), "InFreeFly"); Skippable(typeof(PlayerCustomizaton), "IsBarberGuiVisible"); Skippable(typeof(BuildUi), "get_SearchFieldFocused");
            Skippable(typeof(KeyboardShortcut), "IsDown");
            // Server Characters resets its idle timer when a button reads as held; the camera reads the controller's modifier in the
            // same frames as Epic Loot's controller checks, so leaving theirs out leaves the timer as it was.
            Known.AddRange(new[] { "CombatStats.Patches.BlockPlayerInput.Postfix", "EpicLoot.MagicItemEffects.SpiritAnimal+Player_TakeInput_Patch.Postfix",
                "EpicLoot.PlayerTakeInput_Patch.Postfix", "Jotunn.Managers.GUIManager+Patches.TakeInputPatch", "UIManager.InputBlocker.PlayerTakeInputPostfix",
                "Jotunn.Managers.GUIManager+Patches.TextInputPatch", "UIManager.InputBlocker.TextInputIsVisiblePostfix",
                "EpicLoot.CraftingV2.EnchantingUI_Patches.Minimap_IsOpen_Postfix", "Jotunn.Managers.SynchronizationManager+Patches.Menu_IsVisible",
                "ServerCharacters.ClientSide+MonitorPlayerActivityCheck.Postfix",
                "EpicLoot.Chat_HasFocus_Patch.Postfix", "AzuAntiArthriticCrafting.Patches.PaginatorPatches+MenuIsVisiblePatch.Postfix",
                "Jotunn.Managers.InputManager+Patches.ZInput_GetButton",
                "EquipmentAndQuickSlots.PreventSimilarHotkeys+ZInput_GetButton_PreventSimilarHotkeys.Prefix",
                "EquipmentAndQuickSlots.PreventSimilarHotkeys+ZInput_TryGetButtonState_PreventSimilarHotkeys.Postfix",
                "EquipmentAndQuickSlots.PreventSimilarHotkeys+ZInput_GetKey_PreventSimilarHotkeys.Prefix",
                "EquipmentAndQuickSlots.PreventSimilarHotkeys+ZInput_TryGetKeyStateLowLevel_PreventSimilarHotkeys.Postfix",
                "EquipmentAndQuickSlots.GamepadQuickSlots+ZInput_GetButton_Swallow.Postfix",
                "EpicLootProgressionFix.ChestSearch+TypedKeysStayInField.Postfix", "EpicLootProgressionFix.ChestSearch+TypedButtonsStayInField.Postfix" });
            if (epicLoot) EpicLootAbilities(harmony, epicLootPlugin.Instance.GetType().Assembly);
            if (slots) QuickSlots(harmony, slotsPlugin.Instance.GetType().Assembly);
            if (quickStack) QuickStack(harmony, quickStackPlugin.Instance.GetType().Assembly);
            if (clock) Clock(harmony, clockPlugin.Instance.GetType().Assembly);
        }

        private static void Skippable(Type type, params string[] names)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (string name in names)
            {
                var found = type.GetMethods(all).Where(method => method.Name == name).ToList();
                if (found.Count == 0) throw new MissingMethodException(type.Name + "." + name + " was not found.");
                skippable.AddRange(found);
            }
        }

        // Read once the player is in the world, when every mod has added its patches; until then the checks run as before.
        private static bool Ready()
        {
            if (ready != 0) return ready > 0;
            if (Player.m_localPlayer == null) return false;
            var problems = skippable.Select(method => PatchSets.Unknown(method, Known.ToArray())).Where(problem => problem != null).ToList();
            ready = problems.Count == 0 ? 1 : -1;
            if (problems.Count > 0) Plugin.Fallback("InputCheckOrder unavailable: " + string.Join("; ", problems));
            return ready > 0;
        }

        // Whether a left-out Player.TakeInput would have run a patch that changes something.
        private static bool Popup() => popupOpen != null && popupOpen();

        private static bool Ask(Character player)
        {
            Asked++;
            return takeInput(player);
        }

        private static void EpicLootAbilities(PerformancePatches harmony, Assembly assembly)
        {
            var controller = assembly.GetType("EpicLoot.Abilities.AbilityController");
            var abilities = controller == null ? null : AccessTools.Field(controller, "_currentAbilities");
            var takes = controller == null ? null : AccessTools.Method(controller, "SlotTakesGamepadButton");
            var popup = assembly.GetType("EpicLoot.MagicItemEffects.SpiritAnimal") is Type spirit ? AccessTools.Field(spirit, "_popupOpen") : null;
            if (abilities == null || takes == null || popup == null || popup.FieldType != typeof(bool) || !popup.IsStatic) throw new InvalidOperationException("Epic Loot's ability bar changed.");
            popupOpen = AccessTools.StaticFieldRefAccess<bool>(popup);
            var count = AccessTools.PropertyGetter(abilities.FieldType, "Count");
            abilityCount = Bridges.Make<Func<MonoBehaviour, int>>(typeof(int), new[] { typeof(MonoBehaviour) }, il =>
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, controller); il.Emit(OpCodes.Ldfld, abilities); il.Emit(OpCodes.Callvirt, count); il.Emit(OpCodes.Ret);
            });
            slotTakesButton = Bridges.Make<Func<MonoBehaviour, int, bool>>(typeof(bool), new[] { typeof(MonoBehaviour), typeof(int) }, il =>
            {
                il.DeclareLocal(typeof(KeyCode));
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, controller); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloca_S, (byte)0); il.Emit(OpCodes.Call, takes); il.Emit(OpCodes.Ret);
            });
            harmony.Patch(AccessTools.Method(controller, "Update"), transpiler: new HarmonyMethod(typeof(InputCheckOrder), nameof(AbilityKeysTranspiler)));
            var buttons = new HarmonyMethod(typeof(InputCheckOrder), nameof(AbilityButtonsTranspiler));
            harmony.Patch(AccessTools.Method(controller, "CheckGamepadInput"), transpiler: buttons);
            harmony.Patch(AccessTools.Method(controller, "ConsumeHeldGamepadButtons"), transpiler: buttons);
        }

        // _player.TakeInput() becomes AbilityKeys(_player, this), or AbilityButtons(_player, this).
        private static IEnumerable<CodeInstruction> AbilityKeysTranspiler(IEnumerable<CodeInstruction> instructions) => WithController(instructions, nameof(AbilityKeys));

        private static IEnumerable<CodeInstruction> AbilityButtonsTranspiler(IEnumerable<CodeInstruction> instructions) => WithController(instructions, nameof(AbilityButtons));

        private static List<CodeInstruction> WithController(IEnumerable<CodeInstruction> instructions, string helper)
        {
            var take = AccessTools.Method(typeof(Character), "TakeInput");
            var code = instructions.ToList();
            int call = code.FindIndex(instruction => instruction.Calls(take));
            if (call < 0 || code.FindLastIndex(instruction => instruction.Calls(take)) != call) throw new InvalidOperationException("Epic Loot's ability input changed.");
            code[call].opcode = OpCodes.Ldarg_0;
            code[call].operand = null;
            code.Insert(call + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InputCheckOrder), helper)));
            return code;
        }

        // With no abilities, the key checks that follow find no slot to use.
        internal static bool AbilityKeys(Character player, MonoBehaviour controller)
        {
            if (abilityCount(controller) == 0 && Ready() && !Popup()) { Skipped++; return false; }
            return Ask(player);
        }

        // With no slot holding an activated ability bound to a controller button, the button checks that follow use none.
        internal static bool AbilityButtons(Character player, MonoBehaviour controller)
        {
            if (Ready() && !Popup() && !(similarNames != null && similarNames.Contains("JoyAltKeys")))
            {
                bool any = false;
                for (int index = 0; index < 3 && !any; index++) any = slotTakesButton(controller, index);
                if (!any) { Skipped++; return false; }
            }
            return Ask(player);
        }

        private static void QuickSlots(PerformancePatches harmony, Assembly assembly)
        {
            var combos = assembly.GetType("EquipmentAndQuickSlots.GamepadQuickSlots");
            var slot = assembly.GetType("EquipmentAndQuickSlots.Slots+Slot");
            var prevent = assembly.GetType("EquipmentAndQuickSlots.PreventSimilarHotkeys");
            var button = combos == null ? null : AccessTools.Method(combos, "GetSlotButton", new[] { typeof(int) });
            var canCombos = combos == null ? null : AccessTools.Method(combos, "CanUseCombos");
            var canHotkeys = slot == null ? null : AccessTools.Method(slot, "CanUseHotkeys");
            var down = prevent == null ? null : AccessTools.Method(prevent, "IsShortcutDown", new[] { typeof(KeyboardShortcut) });
            var similar = prevent == null ? null : AccessTools.Field(prevent, "similarName");
            if (button == null || !button.ReturnType.IsEnum || canCombos == null || canHotkeys == null || down == null || similar == null)
                throw new InvalidOperationException("Equipment and Quick Slots' hotkeys changed.");
            noButton = Convert.ToInt32(Enum.Parse(button.ReturnType, "None"));
            slotButton = Bridges.Make<Func<int, int>>(typeof(int), new[] { typeof(int) }, il => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, button); il.Emit(OpCodes.Ret); });
            canUseCombos = AccessTools.MethodDelegate<Func<bool>>(canCombos);
            canUseHotkeys = AccessTools.MethodDelegate<Func<bool>>(canHotkeys);
            shortcutDown = AccessTools.MethodDelegate<Func<KeyboardShortcut, bool>>(down);
            similarNames = (HashSet<string>)similar.GetValue(null);
            harmony.Patch(AccessTools.Method(combos, "ReadCombos"), transpiler: new HarmonyMethod(typeof(InputCheckOrder), nameof(CombosTranspiler)));
            harmony.Patch(AccessTools.Method(slot, "IsShortcutDown"), transpiler: new HarmonyMethod(typeof(InputCheckOrder), nameof(HotkeyTranspiler)));
        }

        private static IEnumerable<CodeInstruction> CombosTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            Transpilers.CallInstead(instructions, instruction => instruction.operand is MethodInfo method && method.Name == "CanUseCombos" && method.DeclaringType == original.DeclaringType,
                AccessTools.Method(typeof(InputCheckOrder), nameof(Combos)), 1, "Equipment and Quick Slots' controller buttons changed.");

        // With no quick slot bound to a controller button, the combo reading that follows uses none.
        internal static bool Combos()
        {
            if (Ready() && !Popup())
            {
                bool any = false;
                for (int index = 0; index < 6 && !any; index++) any = slotButton(index) != noButton;
                if (!any) { Skipped++; return false; }
            }
            Asked++;
            return canUseCombos();
        }

        // CanUseHotkeys() becomes HotkeyAllowed(_getShortcut()); the key test that follows it runs again when this allows it.
        private static IEnumerable<CodeInstruction> HotkeyTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            var shortcut = AccessTools.Field(original.DeclaringType, "_getShortcut");
            int call = code.FindIndex(instruction => instruction.operand is MethodInfo method && method.Name == "CanUseHotkeys");
            if (call < 0 || shortcut == null || code.FindLastIndex(instruction => instruction.operand is MethodInfo method && method.Name == "CanUseHotkeys") != call)
                throw new InvalidOperationException("Equipment and Quick Slots' hotkeys changed.");
            code[call].opcode = OpCodes.Ldarg_0;
            code[call].operand = null;
            code.InsertRange(call + 1, new[] { new CodeInstruction(OpCodes.Ldfld, shortcut), new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(shortcut.FieldType, "Invoke")),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InputCheckOrder), nameof(HotkeyAllowed))) });
            return code;
        }

        // A quick slot key that is not pressed uses nothing, whatever the windows.
        internal static bool HotkeyAllowed(KeyboardShortcut shortcut)
        {
            if (Ready() && !Popup() && !shortcutDown(shortcut)) { Skipped++; return false; }
            Asked++;
            return canUseHotkeys();
        }

        private static void QuickStack(PerformancePatches harmony, Assembly assembly)
        {
            var checker = assembly.GetType("QuickStackStore.KeybindChecker");
            var update = assembly.GetType("QuickStackStore.KeybindChecker+Player_Update_Patch");
            var ignores = checker == null ? null : AccessTools.Method(checker, "IgnoreKeyPresses", Type.EmptyTypes);
            FieldInfo Setting(string section, string name) => assembly.GetType("QuickStackStore.QSSConfig+" + section) is Type type ? AccessTools.Field(type, name) : null;
            var keys = new[] { Setting("QuickStackConfig", "QuickStackKeybind"), Setting("RestockConfig", "RestockKeybind"), Setting("SortConfig", "SortKeybind"),
                Setting("TrashConfig", "QuickTrashKeybind"), Setting("TrashConfig", "TrashKeybind"), Setting("StoreTakeAllConfig", "TakeAllKeybind"), Setting("StoreTakeAllConfig", "StoreAllKeybind") };
            var hardcoded = Setting("ControllerConfig", "UseHardcodedControllerSupport");
            if (update == null || ignores == null || hardcoded?.FieldType != typeof(ConfigEntry<bool>) || keys.Any(key => key?.FieldType != typeof(ConfigEntry<KeyboardShortcut>)))
                throw new InvalidOperationException("Quick Stack Store's keys changed.");
            quickStackIgnores = AccessTools.MethodDelegate<Func<bool>>(ignores);
            quickStackKeys = keys.Select(key => AccessTools.StaticFieldRefAccess<ConfigEntry<KeyboardShortcut>>(key)).ToArray();
            hardcodedController = AccessTools.StaticFieldRefAccess<ConfigEntry<bool>>(hardcoded);
            harmony.Patch(AccessTools.Method(update, "Postfix_Patch"), transpiler: new HarmonyMethod(typeof(InputCheckOrder), nameof(QuickStackTranspiler)));
        }

        private static IEnumerable<CodeInstruction> QuickStackTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.operand is MethodInfo method && method.Name == "IgnoreKeyPresses",
                AccessTools.Method(typeof(InputCheckOrder), nameof(QuickStackIgnored)), 1, "Quick Stack Store's keys changed.");

        // On the keyboard, with none of its keys pressed this frame, Quick Stack Store has nothing to do.
        internal static bool QuickStackIgnored()
        {
            if (Ready() && !(ZInput.IsGamepadActive() && hardcodedController().Value))
            {
                bool pressed = false;
                foreach (var key in quickStackKeys)
                {
                    var main = key().Value.MainKey;
                    if (main != KeyCode.None && Input.GetKeyDown(main)) { pressed = true; break; }
                }
                if (!pressed) { Skipped++; return true; }
            }
            Asked++;
            return quickStackIgnores();
        }

        private static void Clock(PerformancePatches harmony, Assembly assembly)
        {
            var plugin = assembly.GetType("AzuClock.AzuClockPlugin");
            var key = plugin == null ? null : AccessTools.Field(plugin, "ToggleClockKey");
            if (key?.FieldType != typeof(ConfigEntry<KeyboardShortcut>) || !key.IsStatic) throw new InvalidOperationException("AzuClock's clock key changed.");
            clockKey = AccessTools.StaticFieldRefAccess<ConfigEntry<KeyboardShortcut>>(key);
            var ignores = assembly.GetType("AzuClock.Utilities") is Type utilities ? AccessTools.Method(utilities, "IgnoreKeyPresses", new[] { typeof(bool) }) : null;
            if (ignores == null) throw new InvalidOperationException("AzuClock's clock key changed.");
            clockIgnores = AccessTools.MethodDelegate<Func<bool, bool>>(ignores);
            harmony.Patch(AccessTools.Method(plugin, "Update"), transpiler: new HarmonyMethod(typeof(InputCheckOrder), nameof(ClockTranspiler)));
        }

        // !IgnoreKeyPresses() && ToggleClockKeyOnPress.Value == Off && ToggleClockKey.Value.IsDown(): the windows check
        // becomes ClockIgnored, which answers that keys are ignored while the clock key is not down.
        private static IEnumerable<CodeInstruction> ClockTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.operand is MethodInfo method && method.Name == "IgnoreKeyPresses",
                AccessTools.Method(typeof(InputCheckOrder), nameof(ClockIgnored)), 1, "AzuClock's clock key changed.");

        // Without the clock key down the toggle does nothing, whatever the windows.
        internal static bool ClockIgnored(bool extra)
        {
            if (Ready() && !clockKey().Value.IsDown()) { Skipped++; return true; }
            Asked++;
            return clockIgnores(extra);
        }
    }
}
