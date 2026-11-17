using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SoftReferenceableAssets;

namespace WorldPerformance
{
    // Locations and dungeon rooms come from asset bundles the game loads when one is first needed and unloads as soon as the
    // last one leaves the loaded area. Unloading a large location's bundle holds the main thread for one frame, 40-52 ms for
    // a Mistlands guard tower on the test client, most of it Unity shrinking its table of every loaded object, and the next
    // location of the same kind loads and wakes the whole prefab again, up to 40 ms more. The game now keeps each such asset
    // loaded until the next natural pause, as the hourly unload waits for one: a frame in which the loading screen covers the
    // view or the game menu is open, when they all unload as the game would have. At most 64 are kept; past that the one kept
    // longest unloads then.
    internal static class KeptStreamedAssets
    {
        private const int Most = 64;
        // Off releases each asset when the game does; the isolated client switches it for paired measurements.
        internal static bool Keeping = true;
        internal static bool Running { get; private set; }
        internal static long Kept, Released, Overflowed;
        internal static int Holding => held.Count;
        private static readonly Harmony patches = new Harmony(Plugin.Id + ".kept-streamed-assets");
        // Oldest first.
        private static readonly List<AssetID> held = new List<AssetID>();
        private static readonly HashSet<AssetID> heldSet = new HashSet<AssetID>();
        private static FieldInfo loadersField, indicesField;
        private static PropertyInfo referenceCount;
        private static MethodInfo release;
        private static object loader;
        private static bool releasing;

        internal static void Start()
        {
            if (Running) return;
            var type = AccessTools.TypeByName("SoftReferenceableAssets.AssetBundleLoader");
            var assetLoader = AccessTools.TypeByName("SoftReferenceableAssets.AssetLoader");
            release = type == null ? null : AccessTools.Method(type, "Release", new[] { typeof(AssetID) });
            loadersField = type == null ? null : AccessTools.Field(type, "m_assetLoaders");
            indicesField = type == null ? null : AccessTools.Field(type, "m_assetIDToLoaderIndex");
            referenceCount = assetLoader == null ? null : AccessTools.Property(assetLoader, "ReferenceCount");
            if (release == null || loadersField == null || indicesField == null || referenceCount == null)
                throw new InvalidOperationException("The game's asset bundle loader changed.");
            patches.Patch(release, prefix: new HarmonyMethod(typeof(KeptStreamedAssets), nameof(Releasing)));
            Running = true;
        }

        internal static void Stop()
        {
            if (!Running) return;
            ReleaseAll();
            patches.UnpatchSelf();
            Running = false;
        }

        // The game's last reference to an asset is kept instead of released; any other release runs as the game asks.
        private static bool Releasing(object __instance, AssetID assetID)
        {
            if (releasing || !Keeping || ZNet.instance == null || ZNet.instance.IsDedicated() || heldSet.Contains(assetID) || DeferredAssetUnload.NaturalPause())
                return true;
            if (!((IDictionary<AssetID, int>)indicesField.GetValue(__instance)).TryGetValue(assetID, out int index) ||
                (uint)referenceCount.GetValue(((Array)loadersField.GetValue(__instance)).GetValue(index)) != 1) return true;
            loader = __instance;
            held.Add(assetID);
            heldSet.Add(assetID);
            Kept++;
            if (held.Count > Most)
            {
                Overflowed++;
                ReleaseKept(held[0]);
                held.RemoveAt(0);
            }
            return false;
        }

        // Every kept asset unloads in the first frame of a natural pause, or when the world closes or keeping is switched off.
        internal static void LateUpdate()
        {
            if (held.Count == 0 || Keeping && ZNet.instance != null && !DeferredAssetUnload.NaturalPause()) return;
            ReleaseAll();
        }

        private static void ReleaseAll()
        {
            foreach (var asset in held) ReleaseKept(asset);
            held.Clear();
        }

        private static void ReleaseKept(AssetID asset)
        {
            heldSet.Remove(asset);
            if (loader == null) return;
            releasing = true;
            try { release.Invoke(loader, new object[] { asset }); Released++; }
            finally { releasing = false; }
        }
    }
}
