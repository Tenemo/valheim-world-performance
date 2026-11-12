using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // About once a second the game rechecks the support of each piece it owns. A piece resting on other pieces remembers
    // the supporters its last full check found and confirms, for each, that it still exists and supports, that its
    // collider is where it was and that its support is unchanged; it then returns without changing anything. At a large
    // base that is about ten supporters a piece and two fifths of the wear updater, each reaching the collider's transform
    // through the engine and the supporter's support through its network view. A collider's transform never changes, so
    // each piece keeps the transforms of the colliders it remembers, matched to the game's own list on every check, and an
    // owned supporter's support is its own field, which is what the game returns for an owner. The check passes only where
    // the game's would, and then returns as the game's does; anything else runs the game's check.
    // Clients only: a dedicated server keeps its own reference position outside the world, so it creates no pieces to check,
    // and the 1.0.20 server's check has no remembered supporter list.
    internal static class SettledSupport
    {
        private sealed class Identity : IEqualityComparer<WearNTear>
        {
            public bool Equals(WearNTear first, WearNTear second) => ReferenceEquals(first, second);
            public int GetHashCode(WearNTear piece) => RuntimeHelpers.GetHashCode(piece);
        }

        private sealed class Record
        {
            internal Collider[] Colliders;
            internal Transform[] Transforms;
        }

        private static readonly Harmony patches = new Harmony(Plugin.Id + ".settled-support");
        private static readonly Dictionary<WearNTear, Record> records = new Dictionary<WearNTear, Record>(new Identity());
        // Bound in Start, so a game without these members fails only this feature's start.
        private static AccessTools.FieldRef<WearNTear, List<Collider>> colliders;
        private static AccessTools.FieldRef<WearNTear, List<WearNTear>> supporters;
        private static AccessTools.FieldRef<WearNTear, List<Vector3>> positions;
        private static AccessTools.FieldRef<WearNTear, List<float>> values;
        private static AccessTools.FieldRef<WearNTear, float> support;
        private static AccessTools.FieldRef<WearNTear, ZNetView> view;
        private static System.Func<WearNTear, float> supportOf;
        internal static bool Running { get; private set; }
        // Off runs every check natively; the isolated client switches it for comparisons and paired measurements.
        internal static bool Enabled = true;
        // Set by the isolated client's comparison: a check that would pass still runs the game's, which the comparison
        // then confirms returned without changing anything.
        internal static bool Shadow { get; set; }
        internal static bool LastPassed;
        internal static long Passed;

        internal static void Start()
        {
            if (Running) return;
            colliders = AccessTools.FieldRefAccess<WearNTear, List<Collider>>("m_supportColliders");
            supporters = AccessTools.FieldRefAccess<WearNTear, List<WearNTear>>("m_supportWearNTear");
            positions = AccessTools.FieldRefAccess<WearNTear, List<Vector3>>("m_supportPositions");
            values = AccessTools.FieldRefAccess<WearNTear, List<float>>("m_supportValue");
            support = AccessTools.FieldRefAccess<WearNTear, float>("m_support");
            view = AccessTools.FieldRefAccess<WearNTear, ZNetView>("m_nview");
            supportOf = AccessTools.MethodDelegate<System.Func<WearNTear, float>>(AccessTools.Method(typeof(WearNTear), "GetSupport"));
            try
            {
                // Forgetting destroyed pieces comes first, so no record can outlive its piece.
                patches.Patch(AccessTools.Method(typeof(WearNTear), "OnDestroy"), postfix: new HarmonyMethod(typeof(SettledSupport), nameof(Destroyed)));
                patches.Patch(AccessTools.Method(typeof(WearNTear), "UpdateSupport"), prefix: new HarmonyMethod(typeof(SettledSupport), nameof(Prefix)) { priority = Priority.Low });
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
            records.Clear();
        }

        private static bool Prefix(WearNTear __instance, bool __runOriginal)
        {
            LastPassed = false;
            if (!__runOriginal || !Enabled) return true;
            var remembered = colliders(__instance);
            int count = remembered.Count;
            var places = positions(__instance);
            if (count == 0 || places.Count != count) return true;
            var pieces = supporters(__instance);
            var supports = values(__instance);
            if (pieces.Count < count || supports.Count < count) return true;
            var record = Remembered(__instance, remembered, count);
            if (record == null) return true;
            var transforms = record.Transforms;
            float strongest = 0f;
            for (int index = 0; index < count; index++)
            {
                if (remembered[index] == null) return true;
                var piece = pieces[index];
                if (piece == null || !piece.m_supports || transforms[index].position != places[index]) return true;
                var network = view(piece);
                float value = network.IsOwner() && network.HasOwner() ? support(piece) : supportOf(piece);
                if (value > strongest) strongest = value;
                if (!value.Equals(supports[index])) return true;
            }
            if (!(strongest >= support(__instance))) return true;
            LastPassed = true;
            Passed++;
            return Shadow;
        }

        // The transforms of the colliders the piece remembers, kept until its list holds other colliders.
        private static Record Remembered(WearNTear piece, List<Collider> remembered, int count)
        {
            if (records.TryGetValue(piece, out var record) && record.Colliders.Length == count)
            {
                var kept = record.Colliders;
                int index = 0;
                while (index < count && ReferenceEquals(kept[index], remembered[index])) index++;
                if (index == count) return record;
            }
            var found = new Collider[count];
            var transforms = new Transform[count];
            for (int index = 0; index < count; index++)
            {
                var collider = remembered[index];
                if (collider == null) return null;
                found[index] = collider;
                transforms[index] = collider.transform;
            }
            if (record == null) records[piece] = record = new Record();
            record.Colliders = found;
            record.Transforms = transforms;
            return record;
        }

        private static void Destroyed(WearNTear __instance) => records.Remove(__instance);

        internal static int Records => records.Count;
    }
}
