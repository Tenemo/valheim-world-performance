using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Drop That 3.1.7 sends each joining player its drop tables and the world's location list and packed them again for
    // every join, on the server's main thread: the pack's 6,400 star-drop entries became 7.8 MB of text and 125 KB
    // compressed, which held up the server for everyone for about 1.3 seconds a join. The packed creature and container
    // drop tables are now kept and sent again until Drop That loads new tables, as its reload command does, and the packed
    // location list until a join's list differs from it. Its general settings are still packed for each join. A server
    // packs the kept messages once its world has loaded, so the first join after a start waits for none of them. What
    // players receive is unchanged.
    internal static class DropThatSyncCache
    {
        // Per message type: what it was packed from, and the packed bytes.
        private static readonly Dictionary<Type, KeyValuePair<object, byte[]>> Packed = new Dictionary<Type, KeyValuePair<object, byte[]>>();
        private static readonly Dictionary<Type, Func<object, object>> TablesOf = new Dictionary<Type, Func<object, object>>();
        private static readonly Dictionary<Type, Func<object, object, bool>> SameAs = new Dictionary<Type, Func<object, object, bool>>();
        private static readonly Dictionary<MethodBase, Type> ChangedTables = new Dictionary<MethodBase, Type>();
        private static IDictionary handlers;
        private static MethodInfo packer, initialize, serializerFor, packWith;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.DropThat, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var serializer = assembly.GetType("ThatCore.Network.Serializer");
            var pack = serializer == null ? null : AccessTools.Method(serializer, "SerializeAndCompress", new[] { typeof(object) });
            if (pack == null || pack.ReturnType != typeof(byte[])) throw new InvalidOperationException("Drop That's config packing changed.");
            foreach (string name in new[] { "DropThat.Drop.CharacterDropSystem.Sync.CharacterDropConfigMessage",
                "DropThat.Drop.DropTableSystem.Sync.DropTableConfigMessage" })
            {
                var message = assembly.GetType(name);
                var tables = message == null ? null : AccessTools.PropertyGetter(message, "Templates");
                if (tables == null || !typeof(IDictionary).IsAssignableFrom(tables.ReturnType))
                    throw new InvalidOperationException($"Drop That's {name} changed.");
                TablesOf[message] = sent => tables.Invoke(sent, null);
                SameAs[message] = Same;
                string managerName = name.Replace(".Sync.", ".Managers.").Replace("ConfigMessage", "TemplateManager");
                var manager = assembly.GetType(managerName);
                foreach (var changed in new[] { AccessTools.PropertySetter(manager, "Templates"), AccessTools.Method(manager, "SetTemplate") })
                {
                    if (changed == null) throw new InvalidOperationException("Drop That's template updates changed.");
                    ChangedTables[changed] = message;
                    harmony.Patch(changed, postfix: new HarmonyMethod(typeof(DropThatSyncCache), nameof(Invalidate)));
                }
            }
            // A location message lists every location of the world by name and zone; it is the same while the world keeps them.
            var locations = assembly.GetType("DropThat.Locations.Sync.SimpleLocationMessage");
            var names = locations == null ? null : AccessTools.Field(locations, "LocationNames");
            var places = locations == null ? null : AccessTools.Field(locations, "Locations");
            if (names?.FieldType != typeof(string[]) || places == null || !places.FieldType.IsArray || !places.FieldType.GetElementType().IsValueType ||
                places.FieldType.GetElementType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(field => !field.FieldType.IsPrimitive))
                throw new InvalidOperationException("Drop That's location message changed.");
            TablesOf[locations] = sent => sent;
            SameAs[locations] = (current, previous) => SameItems((Array)names.GetValue(current), (Array)names.GetValue(previous)) &&
                SameItems((Array)places.GetValue(current), (Array)places.GetValue(previous));
            // Drop That's own message builders and packing, as a join uses them.
            handlers = AccessTools.PropertyGetter(assembly.GetType("DropThat.Core.SyncManager"), "PackageHandlers")?.Invoke(null, null) as IDictionary;
            packer = AccessTools.PropertyGetter(assembly.GetType("DropThat.Core.SyncManager+HandlerPair"), "Packer");
            initialize = AccessTools.Method(assembly.GetType("ThatCore.Network.IMessage"), "Initialize");
            serializerFor = AccessTools.Method(assembly.GetType("ThatCore.Network.SerializerManager"), "GetSerializer", new[] { typeof(Type) });
            packWith = pack;
            if (handlers == null || packer == null || initialize == null || serializerFor == null) throw new InvalidOperationException("Drop That's join synchronization changed.");
            harmony.Patch(AccessTools.Method(typeof(ZoneSystem), "Start"), postfix: new HarmonyMethod(typeof(DropThatSyncCache), nameof(WarmWhenLoaded)));
            harmony.Patch(pack, prefix: new HarmonyMethod(typeof(DropThatSyncCache), nameof(UsePacked)),
                postfix: new HarmonyMethod(typeof(DropThatSyncCache), nameof(KeepPacked)));
            log.LogInfo("Drop That's drop tables and location list are packed once for joining players instead of for each join.");
        }

        private static void WarmWhenLoaded(ZoneSystem __instance)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer()) __instance.StartCoroutine(Warm(__instance));
        }

        // Waits for the world's locations, then packs each kept message the way a join would.
        private static IEnumerator Warm(ZoneSystem zones)
        {
            float deadline = Time.realtimeSinceStartup + 120f;
            while (zones && zones.m_locationInstances.Count == 0 && Time.realtimeSinceStartup < deadline) yield return null;
            yield return null;
            if (!zones) yield break;
            foreach (var handler in handlers.Values.Cast<object>().ToArray())
            {
                try
                {
                    var message = ((Delegate)packer.Invoke(handler, null)).DynamicInvoke();
                    if (message == null || !TablesOf.ContainsKey(message.GetType())) continue;
                    initialize.Invoke(message, null);
                    packWith.Invoke(serializerFor.Invoke(null, new object[] { message.GetType() }), new[] { message });
                }
                catch (Exception error) { Plugin.Fallback("DropThatSyncCache could not pack a message ahead of the first join: " + error.GetBaseException().Message); }
            }
        }

        private static void Invalidate(MethodBase __originalMethod) => Packed.Remove(ChangedTables[__originalMethod]);

        // Creature messages share the current dictionary. Container messages copy it on every join; compare that shallow
        // snapshot in its original enumeration order, preserving the serializer's bytes. Native template edits and reloads
        // invalidate both caches, including an in-place SetTemplate call on an existing dictionary.
        private static bool Same(object current, object previous)
        {
            if (ReferenceEquals(current, previous)) return true;
            var left = (IDictionary)current;
            var right = (IDictionary)previous;
            if (left.Count != right.Count) return false;
            var other = right.GetEnumerator();
            foreach (DictionaryEntry entry in left)
                if (!other.MoveNext() || !Equals(entry.Key, other.Key) || !ReferenceEquals(entry.Value, other.Value)) return false;
            return true;
        }

        // Strings and location records, whose fields are all numbers, compare by value.
        private static bool SameItems(Array current, Array previous)
        {
            if (current == null || previous == null) return current == previous;
            if (current.Length != previous.Length) return false;
            for (int index = 0; index < current.Length; index++)
                if (!Equals(current.GetValue(index), previous.GetValue(index))) return false;
            return true;
        }

        private static bool UsePacked(object __0, ref byte[] __result, out object __state)
        {
            __state = __0 != null && TablesOf.TryGetValue(__0.GetType(), out var tables) ? tables(__0) : null;
            if (__state == null || !Packed.TryGetValue(__0.GetType(), out var packed) || !SameAs[__0.GetType()](__state, packed.Key)) return true;
            // Drop That splits the bytes into copies and never changes them.
            __result = packed.Value;
            return false;
        }

        private static void KeepPacked(object __0, byte[] __result, object __state)
        {
            if (__state != null && __result != null) Packed[__0.GetType()] = new KeyValuePair<object, byte[]>(__state, __result);
        }
    }
}
