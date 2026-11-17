using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Loading a saved item, as a chest's first load does for every stack, makes a throwaway copy of the item prefab, fills it
    // with the saved values and clones its item data into the inventory. Epic Loot 0.15.6 parses the saved magic text on the
    // throwaway copy in Indestructible's AddItem prefix and again on the clone at the next weight update, with Progression
    // Fix's converter both times. The clone now takes over the throwaway's parsed magic data when that data is exactly what
    // parsing the clone's text gives: parsed from the very string the clone holds and not set or changed since, and the
    // throwaway is never read again. Epic Loot's own end of a parse, the Indestructible sync, runs on the clone as before.
    internal static class SavedItemMagic
    {
        // The isolated test client switches this off to compare Epic Loot's second parse.
        internal static bool HandOver = true;
        internal static bool Active { get; private set; }
        internal static long HandedOver;
        private static ComponentAccess access;
        private static Func<ItemDrop.ItemData, string> rawOf;
        private static Action<ItemDrop.ItemData> sync;
        private static bool loadingSaved;
        private static ItemDrop.ItemData throwaway, adding;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.EpicLoot, out var plugin)) return;
            if (plugin.Metadata.Version.ToString() != "0.15.6")
                throw new InvalidOperationException("Epic Loot " + plugin.Metadata.Version + "'s magic data records are unread.");
            var assembly = plugin.Instance.GetType().Assembly;
            var data = assembly.GetType("EpicLoot.MagicItemData", true);
            var component = assembly.GetType("EpicLoot.MagicItemComponent", true);
            var table = AccessTools.Field(data, "Components")?.GetValue(null) ?? throw new MissingFieldException("Epic Loot's magic data table changed.");
            access = (ComponentAccess)Activator.CreateInstance(typeof(ComponentAccess<>).MakeGenericType(component), table);
            rawOf = AccessTools.MethodDelegate<Func<ItemDrop.ItemData, string>>(AccessTools.Method(data, "RawOf", new[] { typeof(ItemDrop.ItemData) }));
            sync = AccessTools.MethodDelegate<Action<ItemDrop.ItemData>>(AccessTools.Method(assembly.GetType("EpicLoot.MagicItemEffects.Indestructible", true), "Sync", new[] { typeof(ItemDrop.ItemData) }));
            var savedItem = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(int), typeof(int), typeof(float), typeof(Vector2i), typeof(bool), typeof(int), typeof(int),
                typeof(long), typeof(string), typeof(Dictionary<string, string>), typeof(int), typeof(bool), typeof(bool), typeof(bool) });
            var cell = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
            harmony.Patch(savedItem, prefix: new HarmonyMethod(typeof(SavedItemMagic), nameof(SavedBegin)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(SavedItemMagic), nameof(SavedEnd)));
            harmony.Patch(AccessTools.Method(typeof(ItemDrop), "Awake"), postfix: new HarmonyMethod(typeof(SavedItemMagic), nameof(Awakened)) { priority = Priority.Last });
            harmony.Patch(cell, prefix: new HarmonyMethod(typeof(SavedItemMagic), nameof(CellBegin)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(SavedItemMagic), nameof(CellEnd)));
            harmony.Patch(AccessTools.Method(typeof(ItemDrop.ItemData), "Clone"), postfix: new HarmonyMethod(typeof(SavedItemMagic), nameof(Cloned)));
            // A parse is handed over only while nothing has set the magic data since: Epic Loot's own fixes go through SetMagicItem.
            harmony.Patch(AccessTools.Method(component, "Reload", new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(SavedItemMagic), nameof(Parsing)),
                postfix: new HarmonyMethod(typeof(SavedItemMagic), nameof(Parsed)));
            harmony.Patch(AccessTools.Method(component, "SetMagicItem"), prefix: new HarmonyMethod(typeof(SavedItemMagic), nameof(Setting)));
            Active = true;
            log.LogInfo("Loaded items take over the magic data Epic Loot parsed on their throwaway copies.");
        }

        private static void SavedBegin(out (bool, ItemDrop.ItemData, ItemDrop.ItemData) __state)
        {
            __state = (loadingSaved, throwaway, adding);
            loadingSaved = true;
            throwaway = null;
            adding = null;
        }
        private static Exception SavedEnd((bool, ItemDrop.ItemData, ItemDrop.ItemData) __state, Exception __exception)
        {
            (loadingSaved, throwaway, adding) = __state;
            return __exception;
        }
        // The saved-item load wakes exactly one item copy before it adds that copy's data.
        private static void Awakened(ItemDrop __instance) => Copied(__instance.m_itemData);
        // World Performance's own copy of the prefab's item data stands in for the woken copy.
        internal static void Copied(ItemDrop.ItemData data)
        {
            if (loadingSaved && throwaway == null && adding == null) throwaway = data;
        }
        private static void CellBegin(ItemDrop.ItemData item, out ItemDrop.ItemData __state)
        {
            __state = adding;
            adding = item;
        }
        private static Exception CellEnd(ItemDrop.ItemData __state, Exception __exception)
        {
            adding = __state;
            return __exception;
        }

        private static readonly ConditionalWeakTable<object, object> fresh = new ConditionalWeakTable<object, object>();
        private static readonly object mark = new object();
        [ThreadStatic] private static object reloading;
        private static void Parsing(object __instance) => reloading = __instance;
        private static void Parsed(object __instance)
        {
            fresh.Remove(__instance);
            if (ReferenceEquals(reloading, __instance)) fresh.Add(__instance, mark);
            reloading = null;
        }
        private static void Setting(object __instance)
        {
            fresh.Remove(__instance);
            if (ReferenceEquals(reloading, __instance)) reloading = null;
        }

        private static void Cloned(ItemDrop.ItemData __instance, ItemDrop.ItemData __result)
        {
            if (!HandOver || !loadingSaved || throwaway == null || !ReferenceEquals(__instance, throwaway) || !ReferenceEquals(adding, throwaway)) return;
            var component = access.Get(__instance);
            string raw = rawOf(__result);
            if (component == null || raw == null || !fresh.TryGetValue(component, out _) || !access.Owns(component, __instance, raw)) return;
            access.Move(component, __instance, __result);
            fresh.Remove(component);
            // What Epic Loot's parse of the clone would end with.
            sync(__result);
            HandedOver++;
            // The throwaway is destroyed with its copy; nothing reads it after its clone is added.
            throwaway = null;
        }

        private abstract class ComponentAccess
        {
            internal abstract object Get(ItemDrop.ItemData item);
            internal abstract bool Owns(object component, ItemDrop.ItemData item, string raw);
            internal abstract void Move(object component, ItemDrop.ItemData from, ItemDrop.ItemData to);
        }

        private sealed class ComponentAccess<T> : ComponentAccess where T : class
        {
            private readonly ConditionalWeakTable<ItemDrop.ItemData, T> table;
            private static readonly AccessTools.FieldRef<T, string> raw = AccessTools.FieldRefAccess<T, string>("Raw");
            private static readonly AccessTools.FieldRef<T, object> magic = AccessTools.FieldRefAccess<T, object>(AccessTools.Field(typeof(T), "MagicItem"));
            private static readonly FieldInfo item = AccessTools.Field(typeof(T), "<Item>k__BackingField") ?? throw new MissingFieldException("Epic Loot's magic data owner changed.");

            public ComponentAccess(object table) => this.table = (ConditionalWeakTable<ItemDrop.ItemData, T>)table;
            internal override object Get(ItemDrop.ItemData key) => table.TryGetValue(key, out var value) ? value : null;
            internal override bool Owns(object component, ItemDrop.ItemData owner, string text)
            {
                var value = (T)component;
                return ReferenceEquals(raw(value), text) && magic(value) != null && ReferenceEquals(item.GetValue(value), owner);
            }
            internal override void Move(object component, ItemDrop.ItemData from, ItemDrop.ItemData to)
            {
                var value = (T)component;
                table.Remove(from);
                item.SetValue(value, to);
                table.Add(to, value);
            }
        }
    }
}
