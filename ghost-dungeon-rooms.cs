using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SoftReferenceableAssets;
using UnityEngine;

namespace WorldPerformance
{
    // When the dedicated server generates a dungeon for a zone nobody has visited, it copies every candidate room's whole
    // model to lay the interior out, saves the room list and destroys the copies: 100-170 ms of a server frame for a crypt
    // or a Dvergr town, which every player feels. The layout reads only each room's size, its connections and the network
    // objects the game copies separately, so the server now places a stand-in of each room with just those. The room list,
    // the network objects and the random order come out as they would with the models.
    internal static class GhostDungeonRooms
    {
        // The isolated test server's helper sets false to compare the native copies.
        internal static bool StandIns = true;
        private static readonly FieldInfo[] roomFields = AccessTools.GetDeclaredFields(typeof(Room)).Where(field => !field.IsStatic && !field.IsNotSerialized &&
            (field.IsPublic || field.IsDefined(typeof(SerializeField), false))).ToArray();

        [HarmonyPatch(typeof(DungeonGenerator), "PlaceRoom", typeof(DungeonDB.RoomData), typeof(Vector3), typeof(Quaternion), typeof(RoomConnection), typeof(ZoneSystem.SpawnMode))]
        private static class Placement
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var native = AccessTools.Method(typeof(SoftReferenceableAssets.Utils), nameof(SoftReferenceableAssets.Utils.Instantiate),
                    new[] { typeof(SoftReference<GameObject>), typeof(Vector3), typeof(Quaternion), typeof(Transform) });
                int call = code.FindIndex(instruction => instruction.Calls(native));
                if (call < 0) throw new System.InvalidOperationException("The game's room placement changed.");
                // The spawn mode is the method's fifth argument.
                code[call] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GhostDungeonRooms), nameof(Instantiate)));
                code.Insert(call, new CodeInstruction(OpCodes.Ldarg_S, (byte)5));
                return code;
            }
        }

        private static GameObject Instantiate(SoftReference<GameObject> prefab, Vector3 position, Quaternion rotation, Transform parent, ZoneSystem.SpawnMode mode)
        {
            if (!StandIns || mode != ZoneSystem.SpawnMode.Ghost || ZNet.instance == null || !ZNet.instance.IsDedicated())
                return SoftReferenceableAssets.Utils.Instantiate(prefab, position, rotation, parent);
            return StandIn(prefab.Asset, position, rotation, parent);
        }

        // The room's own component and its active connections at the moment of placement, flattened under the root at the
        // poses they have in the model and in the order the room lists them, placed as Unity places a copy.
        private static GameObject StandIn(GameObject asset, Vector3 position, Quaternion rotation, Transform parent)
        {
            var stand = new GameObject(asset.name + "(Clone)");
            stand.SetActive(false);
            stand.transform.SetParent(parent, false);
            stand.transform.SetPositionAndRotation(position, rotation);
            stand.transform.localScale = asset.transform.localScale;
            var source = asset.GetComponent<Room>();
            var room = stand.AddComponent<Room>();
            foreach (var field in roomFields) field.SetValue(room, field.GetValue(source));
            room.m_musicPrefab = null;
            var root = asset.transform;
            foreach (var connection in asset.GetComponentsInChildren<RoomConnection>(false))
            {
                var child = new GameObject(connection.name);
                child.transform.SetParent(stand.transform, false);
                child.transform.localPosition = root.InverseTransformPoint(connection.transform.position);
                child.transform.localRotation = Quaternion.Inverse(root.rotation) * connection.transform.rotation;
                var copy = child.AddComponent<RoomConnection>();
                copy.m_type = connection.m_type;
                copy.m_entrance = connection.m_entrance;
                copy.m_allowDoor = connection.m_allowDoor;
                copy.m_doorOnlyIfOtherAlsoAllowsDoor = connection.m_doorOnlyIfOtherAlsoAllowsDoor;
            }
            stand.SetActive(true);
            return stand;
        }
    }
}
