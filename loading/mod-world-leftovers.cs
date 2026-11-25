using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace WorldPerformance
{
    // Other mods and the game keep a left world's objects in static fields until the next world replaces them, and some never
    // let them go, so every session after a logout started larger than the last:
    // - ServerSync, which many mods carry, keeps the last network call it saw, and through it the left world's network, record
    //   manager and routed calls. It reads that call only while handling a call, after setting it to that call.
    // - Dyrr keeps each connection's report, verdict and greeting by connection, and a client's connection to the server stays
    //   in them after logout, keeping that session's network as well, one more for every session.
    // - Inventory screens subscribe to the touch long-press events again when they are disabled instead of unsubscribing, so
    //   every left session's inventory screen stays subscribed with its whole interface, and the rested effect keeps the last
    //   comfort pieces it counted, which it clears before every count.
    // - EpicLoot's adventure watchdog keeps the left network and its watches, which it replaces when it next finds a network.
    // - AzuAntiArthriticCrafting keeps its crafting window parts, ComfortMe its last comfort summary with the pieces counted,
    //   FastLink the start menu buttons of every visit, and StarLevelSystem the map it registered its pin for.
    // At the start menu these are set back to what a fresh start has. Every reader of the kept window parts, watchdog network
    // and map compares them with Unity's equality, which takes a destroyed object for none, and the next world makes them
    // again; nothing reads a left connection or call again, the comfort summary is made again after its 0.2 second reuse and
    // FastLink looks up only the current buttons.
    internal static class ModWorldLeftovers
    {
        private static readonly AccessTools.FieldRef<Action> longPressStarted = AccessTools.StaticFieldRefAccess<Action>(AccessTools.Field(typeof(TouchLongPress), "Started"));
        private static readonly AccessTools.FieldRef<Action> longPressPerformed = AccessTools.StaticFieldRefAccess<Action>(AccessTools.Field(typeof(TouchLongPress), "Performed"));
        private static readonly List<Piece> countedPieces = AccessTools.StaticFieldRefAccess<List<Piece>>(AccessTools.Field(typeof(SE_Rested), "s_tempPieces"))();
        private static readonly List<FieldInfo> handledCalls = new List<FieldInfo>();
        private static readonly List<Func<int>> connectionTables = new List<Func<int>>();
        // Fields that hold one of the world's objects, by mod: type, then field names.
        private static readonly (string mod, string type, string[] fields)[] worldObjects =
        {
            (PluginIds.AzuAntiArthriticCrafting, "AzuAntiArthriticCrafting.ScrollableInputField", new[] { "instance" }),
            (PluginIds.AzuAntiArthriticCrafting, "TutorialOverlay", new[] { "instance" }),
            (PluginIds.AzuAntiArthriticCrafting, "AzuAntiArthriticCrafting.AzuAntiArthriticCraftingPlugin", new[] { "RecipeTrackerUIPrefab" }),
            (PluginIds.AzuAntiArthriticCrafting, "AzuAntiArthriticCrafting.Patches.DescriptionExpander", new[] { "scrollViewGameObject", "scrollViewGameObjectRt", "viewport", "scrollRect" }),
            (PluginIds.AzuAntiArthriticCrafting, "AzuAntiArthriticCrafting.Patches.PaginatorPatches", new[] { "CraftingWindowPaginator", "CraftingWindowInputField", "MaxPageText" }),
            (PluginIds.AzuAntiArthriticCrafting, "AzuAntiArthriticCrafting.RecipeTracking.ButtonCreator", new[] { "AddButton", "RemoveButton" }),
            (PluginIds.AzuAntiArthriticCrafting, "AzuAntiArthriticCrafting.RecipeTracking.RecipeTrackerUI", new[] { "<Instance>k__BackingField" }),
            (PluginIds.AzuAntiArthriticCrafting, "AzuAntiArthriticCrafting.RecipeTracking.HudAwakePatch", new[] { "RecipeTrackerUI" }),
            (PluginIds.StarLevelSystem, "StarLevelSystem.modules.NemesisSystem.NemesisMinimap", new[] { "registeredFor" }),
        };
        private static readonly List<FieldInfo> worldObjectFields = new List<FieldInfo>();
        private static FieldInfo watchdog, watchedNet, watches;
        private static FieldInfo comfortSummary, comfortTime, comfortPosition, comfortShelter;
        private static Dictionary<Button, Navigation> menuButtons;
        internal static bool Running { get; private set; }
        // Set once the start menu first shows, when every plugin has started.
        private static bool resolved;
        internal static int Released { get; private set; }

        internal static void Start() => Running = true;

        internal static void LateUpdate()
        {
            if (!Running || resolved || FejdStartup.instance == null) return;
            resolved = true;
            Resolve();
        }

        private static void Resolve()
        {
            foreach (var plugin in Chainloader.PluginInfos.Values)
            {
                var assembly = plugin.Instance?.GetType().Assembly;
                if (assembly == null || assembly.GetType("ServerSync.ConfigSync") == null) continue;
                var handling = assembly.GetType("ServerSync.ConfigSync+SnatchCurrentlyHandlingRPC");
                var field = handling?.GetField("currentRpc", BindingFlags.Public | BindingFlags.Static);
                if (handling == null || !handling.IsNestedPrivate || field == null || field.FieldType != typeof(ZRpc))
                    throw new InvalidOperationException(plugin.Metadata.Name + "'s ServerSync no longer keeps the handled call as reviewed.");
                if (!handledCalls.Contains(field)) handledCalls.Add(field);
            }
            if (PluginIds.TryGet(PluginIds.Dyrr, out var dyrr))
            {
                var assembly = dyrr.Instance.GetType().Assembly;
                connectionTables.Add(Table(assembly, "Dyrr.Doorman", "Received"));
                connectionTables.Add(Table(assembly, "Dyrr.Doorman", "Verdicts"));
                var greeted = assembly.GetType("Dyrr.Inventories")?.GetField("Greeted", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as HashSet<ZRpc>
                    ?? throw new InvalidOperationException("Dyrr's Inventories.Greeted changed.");
                connectionTables.Add(() => { int count = greeted.Count; greeted.Clear(); return count; });
            }
            foreach (var (mod, type, fields) in worldObjects)
                if (PluginIds.TryGet(mod, out var plugin))
                    foreach (var name in fields) worldObjectFields.Add(Static(plugin, type, name, typeof(UnityEngine.Object)));
            if (PluginIds.TryGet(PluginIds.EpicLoot, out var epicLoot))
            {
                watchdog = Static(epicLoot, "EpicLoot.Adventure.AdventureSpawnWatchdog", "_instance", typeof(MonoBehaviour));
                watchedNet = watchdog.FieldType.GetField("_trackedNet", BindingFlags.NonPublic | BindingFlags.Instance);
                watches = watchdog.FieldType.GetField("_watches", BindingFlags.NonPublic | BindingFlags.Instance);
                if (watchedNet?.FieldType != typeof(ZNet) || !typeof(IDictionary).IsAssignableFrom(watches?.FieldType))
                    throw new InvalidOperationException("EpicLoot's adventure watchdog changed.");
            }
            if (PluginIds.TryGet(PluginIds.ComfortMe, out var comfort))
            {
                comfortSummary = Static(comfort, "ComfortMe.ComfortSnapshot", "cached", null);
                comfortTime = Static(comfort, "ComfortMe.ComfortSnapshot", "cachedTime", typeof(float));
                comfortPosition = Static(comfort, "ComfortMe.ComfortSnapshot", "cachedPos", typeof(Vector3));
                comfortShelter = Static(comfort, "ComfortMe.ComfortSnapshot", "cachedShelter", typeof(bool));
                // The summary, made again after 0.2 seconds, starts with no time of making.
                if (comfortSummary.FieldType != comfortSummary.DeclaringType || comfortSummary.GetValue(null) == null && (float)comfortTime.GetValue(null) != -999f)
                    throw new InvalidOperationException("ComfortMe's comfort summary changed.");
            }
            if (PluginIds.TryGet(PluginIds.FastLink, out var fastLink))
                menuButtons = Static(fastLink, "FastLink.Util.Functions", "ButtonNavigationCache", typeof(Dictionary<Button, Navigation>)).GetValue(null) as Dictionary<Button, Navigation>
                    ?? throw new InvalidOperationException("FastLink's menu button list changed.");
        }

        internal static void Stop()
        {
            Running = false;
            resolved = false;
            handledCalls.Clear();
            connectionTables.Clear();
            worldObjectFields.Clear();
            watchdog = watchedNet = watches = comfortSummary = comfortTime = comfortPosition = comfortShelter = null;
            menuButtons = null;
        }

        private static FieldInfo Static(BepInEx.PluginInfo plugin, string type, string name, Type fieldType)
        {
            var field = plugin.Instance.GetType().Assembly.GetType(type)?.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null || fieldType != null && field.FieldType != fieldType && !fieldType.IsAssignableFrom(field.FieldType))
                throw new InvalidOperationException(plugin.Metadata.Name + "'s " + type + "." + name + " changed.");
            return field;
        }

        // A dictionary by connection.
        private static Func<int> Table(Assembly assembly, string type, string name)
        {
            var field = assembly.GetType(type)?.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (!(field?.GetValue(null) is IDictionary table) || field.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>) || field.FieldType.GetGenericArguments()[0] != typeof(ZRpc))
                throw new InvalidOperationException("Dyrr's " + type + "." + name + " changed.");
            return () => { int count = table.Count; table.Clear(); return count; };
        }

        // At the start menu, after every object of the world is destroyed.
        internal static void ForgetWorld()
        {
            if (!Running || !resolved) return;
            foreach (var field in handledCalls)
                if (field.GetValue(null) != null) { field.SetValue(null, null); Released++; }
            foreach (var table in connectionTables) Released += table();
            Released += countedPieces.Count;
            countedPieces.Clear();
            longPressStarted() = WithoutDestroyed(longPressStarted());
            longPressPerformed() = WithoutDestroyed(longPressPerformed());
            foreach (var field in worldObjectFields)
                if (field.GetValue(null) is UnityEngine.Object kept && !kept) { field.SetValue(null, null); Released++; }
            if (watchdog != null && watchdog.GetValue(null) is UnityEngine.Object watcher && watcher && watchedNet.GetValue(watcher) is ZNet net && !net)
            {
                watchedNet.SetValue(watcher, null);
                ((IDictionary)watches.GetValue(watcher)).Clear();
                Released++;
            }
            if (comfortSummary != null && comfortSummary.GetValue(null) != null)
            {
                comfortSummary.SetValue(null, null);
                comfortTime.SetValue(null, -999f);
                comfortPosition.SetValue(null, default(Vector3));
                comfortShelter.SetValue(null, false);
                Released++;
            }
            if (menuButtons != null)
            {
                var gone = new List<Button>();
                foreach (var button in menuButtons.Keys) if (!button) gone.Add(button);
                foreach (var button in gone) menuButtons.Remove(button);
                Released += gone.Count;
            }
        }

        private static Action WithoutDestroyed(Action handlers)
        {
            if (handlers == null) return null;
            foreach (var handler in handlers.GetInvocationList())
                if (handler.Target is UnityEngine.Object target && !target) { handlers -= (Action)handler; Released++; }
            return handlers;
        }
    }
}
