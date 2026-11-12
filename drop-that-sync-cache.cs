using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Drop That 3.1.7 sends each joining player its drop tables and packed them again for every join, on the server's main
    // thread: the pack's 6,400 star-drop entries became 7.8 MB of text and 125 KB compressed, which held up the server for
    // everyone for about 1.3 seconds a join. The packed creature and container drop tables are now kept and sent again
    // until Drop That loads new tables, as its reload command does. Its general settings and the world's location list
    // are still packed for each join. What players receive is unchanged.
    internal static class DropThatSyncCache
    {
        // Per message type: the drop table dictionary it was packed from, and the packed bytes.
        private static readonly Dictionary<Type, KeyValuePair<object, byte[]>> Packed = new Dictionary<Type, KeyValuePair<object, byte[]>>();
        private static readonly Dictionary<Type, Func<object, object>> TablesOf = new Dictionary<Type, Func<object, object>>();
        private static readonly Dictionary<MethodBase, Type> ChangedTables = new Dictionary<MethodBase, Type>();

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
                string managerName = name.Replace(".Sync.", ".Managers.").Replace("ConfigMessage", "TemplateManager");
                var manager = assembly.GetType(managerName);
                foreach (var changed in new[] { AccessTools.PropertySetter(manager, "Templates"), AccessTools.Method(manager, "SetTemplate") })
                {
                    if (changed == null) throw new InvalidOperationException("Drop That's template updates changed.");
                    ChangedTables[changed] = message;
                    harmony.Patch(changed, postfix: new HarmonyMethod(typeof(DropThatSyncCache), nameof(Invalidate)));
                }
            }
            harmony.Patch(pack, prefix: new HarmonyMethod(typeof(DropThatSyncCache), nameof(UsePacked)),
                postfix: new HarmonyMethod(typeof(DropThatSyncCache), nameof(KeepPacked)));
            log.LogInfo("Drop That's drop tables are packed once for joining players instead of for each join.");
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

        private static bool UsePacked(object __0, ref byte[] __result, out object __state)
        {
            __state = __0 != null && TablesOf.TryGetValue(__0.GetType(), out var tables) ? tables(__0) : null;
            if (__state == null || !Packed.TryGetValue(__0.GetType(), out var packed) || !Same(__state, packed.Key)) return true;
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
