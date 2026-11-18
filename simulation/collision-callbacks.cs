using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;

namespace WorldPerformance
{
    // Valheim has Unity pass every collision callback a new Collision object, which the characters' ground checks alone ask for
    // over 600 times a second. No collision handler in the game or the pack keeps the object beyond its call, so the mod lets
    // Unity reuse one, as Unity's own default does; the contact arrays the handlers read stay new each call. Each world's scene
    // decides it once every mod has patched. Progression Fix's patches on the characters' and impact effects' handlers read
    // the collision during the call and resting creatures' patch never sees it; if any other mod patches a collision handler,
    // or has a collision handler of its own, either of which could keep the object, the game's setting stays.
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
            patches.UnpatchOwner();
            if (Reusing) Physics.reuseCollisionCallbacks = original;
            Reusing = false;
        }

        private static List<string> ownHandlers;
        // Handlers that read the collision only during the call: Progression Fix's item drag on ice reads its contacts.
        private static readonly HashSet<string> knownHandlers = new HashSet<string> { "EpicLootProgressionFix.ItemIceContact" };

        private static void Decide()
        {
            var patched = Harmony.GetAllPatchedMethods().Where(method => method.Name.StartsWith("OnCollision") && Harmony.GetPatchInfo(method).Owners.Any(owner => owner != ProgressionFix && owner != RestingCreatures.PatchOwner))
                .Select(method => method.DeclaringType?.Name + "." + method.Name).ToList();
            if (ownHandlers == null) ownHandlers = OwnHandlers();
            patched.AddRange(ownHandlers);
            Reusing = patched.Count == 0;
            Failure = Reusing ? null : "another mod patches " + string.Join(", ", patched);
            Physics.reuseCollisionCallbacks = Reusing || original;
            if (!Reusing) Plugin.Log.LogInfo("Collision callbacks stay new each call: " + Failure + ".");
        }

        // The collision handlers the plugins' own assemblies declare, read from their metadata once: a handler whose
        // parameter is the collision may keep it. This mod declares none.
        private static List<string> OwnHandlers()
        {
            var found = new List<string>();
            foreach (string path in Directory.EnumerateFiles(BepInEx.Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
                try
                {
                    using (var assembly = AssemblyDefinition.ReadAssembly(path))
                        foreach (var type in assembly.MainModule.GetTypes())
                            foreach (var method in type.Methods)
                                if (method.HasBody && (method.Name == "OnCollisionEnter" || method.Name == "OnCollisionStay" || method.Name == "OnCollisionExit") &&
                                    method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "UnityEngine.Collision" && !knownHandlers.Contains(type.FullName))
                                    found.Add(type.Name + "." + method.Name);
                }
                catch (BadImageFormatException) { }
                catch (IOException) { }
            return found;
        }
    }
}
