using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Valheim has Unity pass every collision callback a new Collision object, which the characters' ground checks alone ask for
    // over 600 times a second. No collision handler in the game or the pack keeps the object beyond its call, so the mod lets
    // Unity reuse one, as Unity's own default does; the contact arrays the handlers read stay new each call. Each world's scene
    // decides it once every mod has patched. Progression Fix's patches on the characters' and impact effects' handlers read
    // the collision during the call and resting creatures' patch never sees it; if any other mod patches a collision handler,
    // which could keep the object, the game's setting stays.
    internal static class CollisionCallbacks
    {
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".collision-callbacks");
        private const string ProgressionFix = "valheim.epiclootprogressionfix";
        private static bool original;
        internal static bool Running { get; private set; }
        internal static bool Reusing { get; private set; }
        // Why the game's setting stays, or null.
        internal static string Failure { get; private set; }

        internal static void Start()
        {
            if (Running) return;
            Running = true;
            original = Physics.reuseCollisionCallbacks;
            patches.Patch(AccessTools.Method(typeof(ZNetScene), "Awake"), postfix: new HarmonyMethod(typeof(CollisionCallbacks), nameof(Decide)));
        }

        internal static void Stop()
        {
            if (!Running) return;
            Running = false;
            patches.UnpatchSelf();
            if (Reusing) Physics.reuseCollisionCallbacks = original;
            Reusing = false;
        }

        private static void Decide()
        {
            var patched = Harmony.GetAllPatchedMethods().Where(method => method.Name.StartsWith("OnCollision") && Harmony.GetPatchInfo(method).Owners.Any(owner => owner != ProgressionFix && owner != RestingCreatures.PatchOwner))
                .Select(method => method.DeclaringType?.Name + "." + method.Name).ToList();
            Reusing = patched.Count == 0;
            Failure = Reusing ? null : "another mod patches " + string.Join(", ", patched);
            Physics.reuseCollisionCallbacks = Reusing || original;
            if (!Reusing) Plugin.Log.LogInfo("Collision callbacks stay new each call: " + Failure + ".");
        }
    }
}
