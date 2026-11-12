using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // ZoneSystem.UpdateTTL checks each expired zone for a non-distant instance standing in it every 0.1 s, and each check
    // reads the position of every instance in the scene. A zone found occupied is checked again after a second, so a zone
    // something keeps loaded costs one scan a second instead of ten, however long the session lasts. An empty zone, which
    // then unloads, is scanned in full, about twice a second while traveling: 5–7 ms over 14,000 instances in the hidden
    // client, more than half of it asking the engine for each instance's transform. A component's transform never changes, so the scan
    // walks the scene's instance table slot by slot, keeps each slot's transform until another instance takes the slot and
    // asks the engine only for positions.
    [HarmonyPatch(typeof(ZoneSystem), "UpdateTTL")]
    internal static class ZoneExpiryChecks
    {
        private static readonly Dictionary<Vector2s, float> OccupiedUntil = new Dictionary<Vector2s, float>();
        private static readonly List<Vector2s> Stale = new List<Vector2s>();
        private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<ZDO, ZNetView>> instancesOf = AccessTools.FieldRefAccess<ZNetScene, Dictionary<ZDO, ZNetView>>("m_instances");
        // The table's slots in use and the instance in each, read as its enumerator reads them, null in a free slot; both stay
        // null if the runtime's dictionary is laid out differently, and the game's own scan runs.
        private static readonly AccessTools.FieldRef<Dictionary<ZDO, ZNetView>, int> slotsOf;
        private static readonly Func<Dictionary<ZDO, ZNetView>, int, ZNetView> instanceIn;
        private static ZNetView[] views = new ZNetView[0];
        private static Transform[] transforms = new Transform[0];
        private static ZNetScene keptFor;

        static ZoneExpiryChecks()
        {
            try
            {
                var entries = AccessTools.Field(typeof(Dictionary<ZDO, ZNetView>), "_entries");
                var entry = entries.FieldType.GetElementType();
                var read = new DynamicMethod("ZoneExpiryChecks.InstanceIn", typeof(ZNetView), new[] { typeof(Dictionary<ZDO, ZNetView>), typeof(int) }, typeof(ZoneExpiryChecks), true);
                var code = read.GetILGenerator();
                var free = code.DefineLabel();
                code.Emit(OpCodes.Ldarg_0);
                code.Emit(OpCodes.Ldfld, entries);
                code.Emit(OpCodes.Ldarg_1);
                code.Emit(OpCodes.Ldelema, entry);
                code.Emit(OpCodes.Dup);
                code.Emit(OpCodes.Ldfld, AccessTools.Field(entry, "hashCode"));
                code.Emit(OpCodes.Ldc_I4_0);
                code.Emit(OpCodes.Blt_S, free);
                code.Emit(OpCodes.Ldfld, AccessTools.Field(entry, "value"));
                code.Emit(OpCodes.Ret);
                code.MarkLabel(free);
                code.Emit(OpCodes.Pop);
                code.Emit(OpCodes.Ldnull);
                code.Emit(OpCodes.Ret);
                var slots = AccessTools.FieldRefAccess<Dictionary<ZDO, ZNetView>, int>("_count");
                instanceIn = (Func<Dictionary<ZDO, ZNetView>, int, ZNetView>)read.CreateDelegate(typeof(Func<Dictionary<ZDO, ZNetView>, int, ZNetView>));
                slotsOf = slots;
            }
            catch (Exception error)
            {
                instanceIn = null;
                Plugin.Fallback("Zone expiry checks read every instance's transform: " + error.Message);
            }
        }

        // Valheim 1.0.15: if (zone.m_ttl > m_zoneTTL && !ZNetScene.instance.HaveInstanceInSector(zone.Key))
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.HaveInstanceInSector))),
                AccessTools.Method(typeof(ZoneExpiryChecks), nameof(HaveInstanceInSector)), 1,
                "Valheim's zone expiry changed; expected one occupied-zone check.");

        private static bool HaveInstanceInSector(ZNetScene scene, Vector2s sector)
        {
            float now = Time.time;
            if (OccupiedUntil.TryGetValue(sector, out float until) && now < until) return true;
            if (!Occupied(scene, sector))
            {
                OccupiedUntil.Remove(sector);
                return false;
            }
            if (OccupiedUntil.Count >= 64)
            {
                foreach (var entry in OccupiedUntil) if (entry.Value < now) Stale.Add(entry.Key);
                foreach (var stale in Stale) OccupiedUntil.Remove(stale);
                Stale.Clear();
            }
            OccupiedUntil[sector] = now + 1f;
            return true;
        }

        // ZNetScene.HaveInstanceInSector with each slot's transform kept, starting again with each world's scene.
        internal static bool Occupied(ZNetScene scene, Vector2s sector)
        {
            if (instanceIn == null) return scene.HaveInstanceInSector(sector);
            var instances = instancesOf(scene);
            int slots = slotsOf(instances);
            if (keptFor != scene)
            {
                Forget();
                keptFor = scene;
            }
            if (views.Length < slots)
            {
                Array.Resize(ref views, slots + slots / 2);
                Array.Resize(ref transforms, views.Length);
            }
            for (int slot = 0; slot < slots; slot++)
            {
                var view = instanceIn(instances, slot);
                if (!view || view.m_distant) continue;
                if (!ReferenceEquals(views[slot], view))
                {
                    views[slot] = view;
                    transforms[slot] = view.transform;
                }
                if (ZoneSystem.GetZone(transforms[slot].position) == sector) return true;
            }
            return false;
        }

        private static void Forget()
        {
            Array.Clear(views, 0, views.Length);
            Array.Clear(transforms, 0, transforms.Length);
            keptFor = null;
        }

        // The kept instances go with their world's scene, so the menu holds none of them.
        [HarmonyPatch(typeof(ZNetScene), "OnDestroy")]
        private static class SceneDestroyed
        {
            private static void Postfix() => Forget();
        }
    }
}
