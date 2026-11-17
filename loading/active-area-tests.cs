using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Whether a point lies in the active area is a Chebyshev distance from the area's center zone, which the game's
    // Utils.ChebyshevDistance takes by passing the three axis distances to Mathf.Max(params float[]), building a new array in
    // every call: 40 bytes of garbage each time wear, static physics, spawning or object creation tests a point, about 175
    // times a frame at the base. The mod returns the same largest distance, compared in the same order, without the array.
    internal static class ActiveAreaTests
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".active-area-tests");
        internal static bool Running { get; private set; }
        // Off computes natively, with the array; the isolated client switches it for paired measurements.
        internal static bool Lean { get; set; } = true;
        internal static long Tests { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            patches.Patch(AccessTools.Method(typeof(Utils), nameof(Utils.ChebyshevDistance), new[] { typeof(Vector3), typeof(Vector3) }),
                prefix: new HarmonyMethod(typeof(ActiveAreaTests), nameof(Distance)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
        }

        // Mathf.Max(params float[]) keeps the first value and takes each later one that is greater.
        private static bool Distance(Vector3 a, Vector3 b, ref float __result)
        {
            Tests++;
            if (!Lean) return true;
            float largest = Mathf.Abs(a.x - b.x), y = Mathf.Abs(a.y - b.y), z = Mathf.Abs(a.z - b.z);
            if (y > largest) largest = y;
            if (z > largest) largest = z;
            __result = largest;
            return false;
        }
    }
}
