using System;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // A player's game keeps the generated world map beside a local world's save, but a client of a dedicated server has no
    // save folder and a world record that never passes the cache's version check, so every join generates the whole map
    // again on the main thread: 5.1 s of Tenemo's join on 2026-11-03. Clients now keep the game's own cache files under
    // BepInEx/cache, one folder per world identity, seed, generator version, game version and map size, and load them
    // as the game loads a local world's map. Hosts and single-player worlds keep the game's own cache.
    internal static class ClientMapCache
    {
        // The isolated test client sets false to compare a fresh generation.
        internal static bool Caching = true;
        internal static long Loaded, Generated;
        private static readonly AccessTools.FieldRef<Minimap, string> saveDirectory = AccessTools.FieldRefAccess<Minimap, string>("m_saveDirectory");
        private static readonly AccessTools.FieldRef<Minimap, string> maskPath = AccessTools.FieldRefAccess<Minimap, string>("m_cachedMinimapMaskTexturePath");
        private static readonly AccessTools.FieldRef<Minimap, string> biomePath = AccessTools.FieldRefAccess<Minimap, string>("m_cachedMinimapBiomeTexturePath");
        private static readonly AccessTools.FieldRef<Minimap, string> heightPath = AccessTools.FieldRefAccess<Minimap, string>("m_cachedMinimapHeightTexturePath");
        private static readonly AccessTools.FieldRef<Minimap, string> metaPath = AccessTools.FieldRefAccess<Minimap, string>("m_cachedMinimapMetaPath");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> forestMask = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> heightTexture = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");

        internal static string Folder(Minimap map)
        {
            var world = ZNet.World;
            string key = string.Join("_", world.m_uid, world.m_seed, world.m_worldGenVersion, global::Version.CurrentVersion, map.m_textureSize, map.m_pixelSize.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            foreach (char invalid in Path.GetInvalidFileNameChars()) key = key.Replace(invalid, '-');
            return Path.Combine(Paths.CachePath, "WorldPerformance", "world-maps", key);
        }

        [HarmonyPatch(typeof(Minimap), "TryLoadMinimapTextureData")]
        private static class Load
        {
            private static bool Prefix(Minimap __instance, int worldSeed, ref bool __result)
            {
                if (!Caching || ZNet.instance == null || ZNet.instance.IsServer() || ZNet.World == null) return true;
                string directory = Folder(__instance);
                Directory.CreateDirectory(directory);
                // The game's own generation writes its cache files to these paths.
                saveDirectory(__instance) = directory;
                maskPath(__instance) = directory + "/cacheMinimapMask";
                biomePath(__instance) = directory + "/cacheMinimapBiome";
                heightPath(__instance) = directory + "/cacheMinimapHeight";
                metaPath(__instance) = directory + "/cacheMinimapMeta";
                __result = Read(__instance, worldSeed);
                if (__result) Loaded++;
                else Generated++;
                return false;
            }
        }

        private static bool Read(Minimap map, int worldSeed)
        {
            if (!File.Exists(maskPath(map)) || !File.Exists(biomePath(map)) || !File.Exists(heightPath(map)) || !File.Exists(metaPath(map))) return false;
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                byte[] meta = File.ReadAllBytes(metaPath(map));
                if (meta.Length < 8 || BitConverter.ToInt32(meta, 0) != worldSeed || (global::Version.CachedMinimap)BitConverter.ToInt32(meta, 4) != global::Version.CachedMinimap.Original) return false;
                forestMask(map).SetPixels32(Utils.CompressedBufferToColors(File.ReadAllBytes(maskPath(map))));
                forestMask(map).Apply();
                map.m_mapTexture.SetPixels32(Utils.CompressedBufferToColors(File.ReadAllBytes(biomePath(map))));
                map.m_mapTexture.Apply();
                heightTexture(map).SetPixels(Utils.CompressedHalfBufferToRedChannel(File.ReadAllBytes(heightPath(map))));
                heightTexture(map).Apply();
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("The cached world map could not be read and is generated again: " + error.Message);
                return false;
            }
            ZLog.Log("Loading cached client minimap textures done [" + started.ElapsedMilliseconds + "ms]");
            return true;
        }
    }
}
