using System;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace WorldPerformance
{
    // Once a second every container compares its network record's revision with the one it last loaded, and when the
    // revision moved it reloads its whole inventory from the record's item bytes, every item with its enchantments. The
    // storage of a ship or cart shares the vehicle's record, whose position moves the revision every physics step, so a
    // loaded ship's hold was rebuilt every second, about 4 ms each for Tenemo's two ships at his base. A reload whose item
    // bytes equal those the inventory last loaded or saved, with no change made to the inventory since, would rebuild the
    // same items, so it is skipped; the lid and an empty loot bag's removal still follow the record as natively. Any other
    // reload, and any container changed in this game without being saved, loads natively.
    internal static class UnchangedContainers
    {
        private sealed class Load
        {
            internal byte[] bytes;
            internal bool changed;
        }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".unchanged-containers");
        private static readonly ConditionalWeakTable<Container, Load> loads = new ConditionalWeakTable<Container, Load>();
        private static readonly AccessTools.FieldRef<Container, ZNetView> views = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Container, uint> revisions = AccessTools.FieldRefAccess<Container, uint>("m_lastRevision");
        private static readonly AccessTools.FieldRef<Container, bool> inUse = AccessTools.FieldRefAccess<Container, bool>("m_inUse");
        private static readonly AccessTools.FieldRef<Container, bool> loading = AccessTools.FieldRefAccess<Container, bool>("m_loading");
        internal static bool Running { get; private set; }
        // Off reloads every moved record, as natively; the isolated client switches it for paired measurements.
        internal static bool Skipping = true;
        internal static long Skipped, Reloaded;

        internal static void Start()
        {
            if (Running) return;
            var type = typeof(Container);
            try
            {
                // Changes are tracked before any load is skipped, so a failed patch can never skip a load it should not.
                patches.Patch(AccessTools.Method(type, "OnContainerChanged"), prefix: new HarmonyMethod(typeof(UnchangedContainers), nameof(Changing)));
                patches.Patch(AccessTools.Method(type, "Save"), postfix: new HarmonyMethod(typeof(UnchangedContainers), nameof(Saved)));
                patches.Patch(AccessTools.Method(type, "Load"), prefix: new HarmonyMethod(typeof(UnchangedContainers), nameof(Loading)), postfix: new HarmonyMethod(typeof(UnchangedContainers), nameof(Loaded)));
            }
            catch
            {
                patches.UnpatchSelf();
                throw;
            }
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        internal static string Report() => $"loads skipped {Skipped}, reloaded {Reloaded}";

        // A change outside a load leaves the inventory different from its record until the owner saves it.
        private static void Changing(Container __instance)
        {
            if (!loading(__instance)) loads.GetOrCreateValue(__instance).changed = true;
        }

        private static void Saved(Container __instance)
        {
            var state = loads.GetOrCreateValue(__instance);
            state.bytes = views(__instance).GetZDO().GetByteArray(ZDOVars.s_items);
            state.changed = false;
        }

        private static bool Loading(Container __instance, ref bool __result, out bool __state)
        {
            __state = true;
            if (!Skipping) return true;
            var data = views(__instance).GetZDO();
            if (data.DataRevision == revisions(__instance) || inUse(__instance)) return true;
            var bytes = data.GetByteArray(ZDOVars.s_items);
            if (bytes == null || !loads.TryGetValue(__instance, out var state) || state.changed || !Same(bytes, state.bytes)) return true;
            revisions(__instance) = data.DataRevision;
            state.bytes = bytes;
            Skipped++;
            __result = true;
            __state = false;
            return false;
        }

        private static void Loaded(Container __instance, bool __result, bool __state)
        {
            if (!__state || !__result) return;
            var bytes = views(__instance).GetZDO().GetByteArray(ZDOVars.s_items);
            if (bytes == null) return;
            var state = loads.GetOrCreateValue(__instance);
            state.bytes = bytes;
            state.changed = false;
            Reloaded++;
        }

        private static bool Same(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (second == null || first.Length != second.Length) return false;
            for (int index = 0; index < first.Length; index++)
                if (first[index] != second[index]) return false;
            return true;
        }
    }
}
