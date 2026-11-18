using System.Collections.Generic;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;

namespace WorldPerformance
{
    // A location's scenery, such as a stone tower ruin's stones, is copied from its prefab in the frame its proxy loads:
    // 13 to 25 ms for a house or a ruin on the test client and up to about 50 ms in a loading frame. In play the copy now
    // runs as Unity's asynchronous instantiation over the next few frames, during which the zone counts as loading, as it
    // does while the game waits for a location's assets. The prefab keeps the seed's choices until its copy completes, so
    // a location of the same kind waits for the one before it. Behind the loading screen, where a zone marked loading would
    // hold back every object, and on the server, whose zone generation changes the same prefabs, the native copy stays.
    // Scripts look for their network object when they start, as an altar does to register its offerings, so a copy joins its
    // location as soon as it exists, before any Start, as the game's own copy does.
    internal static class AsyncLocationSpawns
    {
        // The isolated test client sets false to compare the native copy.
        internal static bool Asynchronous = true;
        private static readonly AccessTools.FieldRef<LocationProxy, ZNetView> view = AccessTools.FieldRefAccess<LocationProxy, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<LocationProxy, bool> needsSpawn = AccessTools.FieldRefAccess<LocationProxy, bool>("m_locationNeedsSpawn");
        private static readonly AccessTools.FieldRef<LocationProxy, ZDO> loadingMark = AccessTools.FieldRefAccess<LocationProxy, ZDO>("m_zdoSetToBeLoadingInZone");
        private static readonly System.Func<ZoneSystem, int, ZoneSystem.ZoneLocation> find =
            AccessTools.MethodDelegate<System.Func<ZoneSystem, int, ZoneSystem.ZoneLocation>>(AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) }));
        private static readonly Dictionary<LocationProxy, Copy> copies = new Dictionary<LocationProxy, Copy>();
        private static readonly HashSet<ZoneSystem.ZoneLocation> busy = new HashSet<ZoneSystem.ZoneLocation>();
        private static readonly List<Copy> orphans = new List<Copy>();

        // A copy in flight and the prefab state the game restores after its own copy.
        private sealed class Copy
        {
            internal ZoneSystem.ZoneLocation Location;
            // The proxy this copy belongs to, until that proxy unloads.
            internal LocationProxy Proxy;
            internal AsyncInstantiateOperation<GameObject> Operation;
            internal CompactLocationJob Compact;
            internal RandomSpawn[] Spawns;
            internal RandomObject[] Objects;
            internal ZNetView[] Views;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Location Component;
            internal bool Interior;
            internal Vector3 GeneratorPosition, InteriorPosition;
            internal Quaternion InteriorRotation;
        }

        [HarmonyPatch(typeof(Game), "SpawnPlayer")]
        private static class PrepareLoadedLocations
        {
            private static void Postfix()
            {
                if (!CompactLocationJob.Enabled || !ZoneSystem.instance || !ZNet.instance || ZNet.instance.IsServer()) return;
                foreach (string name in new[] { "TheHole01", "StoneTower1", "CharredTowerRuins1", "CharredRuins1" })
                {
                    var location = find(ZoneSystem.instance, name.GetStableHashCode());
                    if (location != null && location.m_prefab.IsLoaded)
                        ZoneSystem.instance.StartCoroutine(CompactLocationJob.Prepare(location.m_prefab.Asset));
                }
            }
        }

        [HarmonyPatch(typeof(LocationProxy), "SpawnLocation")]
        private static class Proxy
        {
            private static bool Prefix(LocationProxy __instance, ref bool __result, ref GameObject ___m_instance)
            {
                if (copies.TryGetValue(__instance, out var copy))
                    {
                    Complete(__instance, copy, ref __result, ref ___m_instance);
                    return false;
                }
                if (ZNet.instance == null || ZNet.instance.IsServer()) return true;
                var data = view(__instance)?.GetZDO();
                if (data == null) return true;
                int hash = data.GetInt(ZDOVars.s_location);
                var location = hash == 0 ? null : find(ZoneSystem.instance, hash);
                if (location == null) return true;
                bool held = busy.Contains(location);
                // Behind the loading screen the copy is hidden, and a zone marked loading holds back every object's creation.
                var player = Player.m_localPlayer;
                if (!held && (!Asynchronous || player == null || player.IsTeleporting() || ZoneSystem.instance.ShouldDelayProxyLocationSpawning(hash))) return true;
                if (loadingMark(__instance) == null)
                {
                    loadingMark(__instance) = data;
                    ZoneSystem.instance.SetLoadingInZone(data);
                }
                needsSpawn(__instance) = true;
                __result = false;
                // Another of the same kind holds the prefab; this one tries again next frame, as a delayed spawn does.
                if (held) return false;
                copy = Start(location, data.GetInt(ZDOVars.s_seed), __instance.transform.position, __instance.transform.rotation);
                copy.Proxy = __instance;
                copies[__instance] = copy;
                if (copy.Compact != null) Complete(__instance, copy, ref __result, ref ___m_instance);
                return false;
            }

            private static void Complete(LocationProxy proxy, Copy copy, ref bool result, ref GameObject instance)
            {
                result = false;
                if (!Ready(copy)) return;
                copies.Remove(proxy);
                instance = Finish(copy);
                if (!instance) return;
                view(proxy).LoadFields();
                needsSpawn(proxy) = false;
                if (loadingMark(proxy) != null)
                {
                    ZoneSystem.instance.UnsetLoadingInZone(loadingMark(proxy));
                    loadingMark(proxy) = null;
                }
                result = true;
            }
        }

        [HarmonyPatch(typeof(LocationProxy), "OnDestroy")]
        private static class Unloaded
        {
            private static void Prefix(LocationProxy __instance)
            {
                if (copies.TryGetValue(__instance, out var copy)) { copies.Remove(__instance); copy.Proxy = null; orphans.Add(copy); }
            }
        }

        // Copies whose proxy unloaded first are finished and destroyed as they complete, which frees their prefab.
        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        private static class Orphans
        {
            private static void Postfix()
            {
                for (int index = orphans.Count - 1; index >= 0; index--)
                    if (orphans[index].Compact != null || orphans[index].Operation.isDone)
                    {
                        orphans[index].Compact?.Cancel();
                        var instance = Finish(orphans[index]);
                        orphans.RemoveAt(index);
                        if (instance) Object.Destroy(instance);
                    }
            }
        }

        // The game's client copy up to its instantiation: the seed's random choices on the prefab, its network objects
        // switched off, its transform at the origin and a custom interior placed for this position.
        private static Copy Start(ZoneSystem.ZoneLocation location, int seed, Vector3 position, Quaternion rotation)
        {
            location.m_prefab.Load();
            var asset = location.m_prefab.Asset;
            var copy = new Copy
            {
                Location = location,
                Views = PrefabComponentQueries.EnabledChildren<ZNetView>(asset),
                Objects = PrefabComponentQueries.EnabledChildren<RandomObject>(asset),
                Spawns = PrefabComponentQueries.EnabledChildren<RandomSpawn>(asset),
                Component = asset.GetComponent<Location>(),
                Position = asset.transform.position,
                Rotation = asset.transform.rotation,
            };
            foreach (var spawn in copy.Spawns) spawn.Prepare();
            var component = copy.Component;
            Vector3 interior = Vector3.zero, generator = Vector3.zero;
            if (component.m_interiorTransform && component.m_generator)
            {
                interior = component.m_interiorTransform.localPosition;
                generator = component.m_generator.transform.localPosition;
            }
            asset.transform.position = Vector3.zero;
            asset.transform.rotation = Quaternion.identity;
            copy.Interior = component && component.m_useCustomInteriorTransform && component.m_interiorTransform && component.m_generator;
            if (copy.Interior)
            {
                copy.GeneratorPosition = component.m_generator.transform.localPosition;
                copy.InteriorPosition = component.m_interiorTransform.localPosition;
                copy.InteriorRotation = component.m_interiorTransform.localRotation;
                var zone = ZoneSystem.GetZonePos(ZoneSystem.GetZone(position));
                component.m_generator.transform.localPosition = Vector3.zero;
                Vector3 offset = (Vector3)(Matrix4x4.Rotate(Quaternion.Inverse(rotation)) * Matrix4x4.Translate(zone + interior + generator - position)).GetColumn(3);
                offset.y = component.m_interiorTransform.localPosition.y;
                component.m_interiorTransform.localPosition = offset;
                component.m_interiorTransform.localRotation = Quaternion.Inverse(rotation);
            }
            Random.InitState(seed);
            foreach (var spawn in copy.Spawns) spawn.Randomize(position + rotation * spawn.gameObject.transform.position, component);
            foreach (var choice in copy.Objects) choice.Randomize(position + rotation * choice.gameObject.transform.position, component);
            foreach (var network in copy.Views) network.gameObject.SetActive(false);
            location.m_prefab.Load();
            if (CompactLocationJob.Supports(location.m_prefabName))
                copy.Compact = new CompactLocationJob(asset, position, rotation);
            else
                StartNative(copy, asset, position, rotation);
            busy.Add(location);
            return copy;
        }

        private static void StartNative(Copy copy, GameObject asset, Vector3 position, Quaternion rotation)
        {
            copy.Operation = Object.InstantiateAsync(asset, position, rotation);
            copy.Operation.completed += _ => Attach(copy);
        }

        private static bool Ready(Copy copy)
        {
            if (copy.Compact == null) return copy.Operation.isDone;
            try
            {
                if (!copy.Compact.Advance()) return false;
                if (!copy.Compact.Unsupported) return true;
            }
            catch (System.Exception error)
            {
                Debug.LogException(error);
            }
            copy.Compact.Cancel();
            copy.Compact = null;
            StartNative(copy, copy.Location.m_prefab.Asset, copy.Proxy.transform.position, copy.Proxy.transform.rotation);
            return false;
        }

        private static GameObject Instance(Copy copy)
        {
            if (copy.Compact != null) return copy.Compact.Result;
            var results = copy.Operation.Result;
            return results != null && results.Length > 0 ? results[0] : null;
        }

        private static void Attach(Copy copy)
        {
            var instance = Instance(copy);
            if (instance && copy.Proxy) instance.transform.SetParent(copy.Proxy.transform, worldPositionStays: true);
        }

        // The rest of the game's client copy once the instance exists.
        private static GameObject Finish(Copy copy)
        {
            var location = copy.Location;
            var instance = Instance(copy);
            if (instance)
            {
                // Unity names an asynchronous copy "<name> (Clone)"; the game's own copy has no space.
                instance.name = location.m_prefab.Asset.name + "(Clone)";
                instance.TransferReference(location.m_prefab);
                Attach(copy);
                instance.SetActive(true);
            }
            else location.m_prefab.Release();
            foreach (var spawn in copy.Spawns) spawn.Reset();
            foreach (var choice in copy.Objects) choice.Reset();
            foreach (var network in copy.Views) network.gameObject.SetActive(true);
            var asset = location.m_prefab.Asset;
            asset.transform.position = copy.Position;
            asset.transform.rotation = copy.Rotation;
            if (copy.Interior)
            {
                copy.Component.m_generator.transform.localPosition = copy.GeneratorPosition;
                copy.Component.m_interiorTransform.localPosition = copy.InteriorPosition;
                copy.Component.m_interiorTransform.localRotation = copy.InteriorRotation;
            }
            location.m_prefab.Release();
            busy.Remove(location);
            SnapToGround.SnappAll();
            return instance;
        }
    }
}
