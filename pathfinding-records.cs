using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace WorldPerformance
{
    // Unity keeps every navigation link in one array of 208-byte records, chained newest first. Removing a link walks that
    // chain from its head to the link's predecessor, one record for each link added after it, and adding or removing a tile's
    // mesh walks all of it: at Tenemo's base these walks were most of Pathfinding.Update. The mod now keeps each live
    // record's predecessor, its place in the chain and the 16 m cells its bounds cover, by agent type:
    // - just before the engine removes a link, its record moves to the head of the chain, so the engine's walk ends at its
    //   first step and leaves the chain exactly as its own walk would have;
    // - while the engine adds or removes a tile's mesh, the chain holds only the records of that agent type near the mesh,
    //   in their order, and is restored afterwards. The engine connects a record only to meshes of its own agent type that
    //   its bounds overlap within the agent's radius, and reconnects or disconnects only those, so it acts on the same
    //   records in the same order as with the whole chain.
    // Additions and removals by other code show in the engine's change counter and head, and the records are then read again
    // from the chain; while any record is not one of NavMesh.AddLink's, every mesh sees the whole chain.
    // A freed record goes on top of a free list that hands records out last in, first out, so through a session each new
    // tile's links landed in records scattered by earlier removals, and every step of a walk missed the cache: at Tenemo's
    // base the cost of each removal per link held rose from 8.6 to 13.5 ns within an hour. Before links are added after any
    // removal, the free list is now chained again in ascending record order, so new links take neighbouring records.
    // Every link keeps its data, its record, its place in the chain and its routes.
    // The record layout is read from UnityPlayer.dll's own code, so this runs only on the client build whose code matches.
    internal static unsafe class PathfindingRecords
    {
        private const int Record = 0xD0, Salt = 0xC0, Next = 0xC4;
        // The client's UnityPlayer.dll of Unity 6000.0.75f1 (file sha256 4d161e15d8ccdb32...): NavMesh_CUSTOM_RemoveLinkInternal,
        // which loads the navigation manager from a global and checks a link's handle, the record allocator,
        // NavMesh::RemoveOffMeshConnection, NavMesh::AddOffMeshConnection, NavMesh::AddTile, NavMesh::RemoveTile and
        // NavMesh::ConnectOffMeshConnection, as relative addresses and lengths. None of these bytes are relocated.
        private static readonly (int start, int length)[] Code =
        {
            (0x110130, 0xAE), (0xA75BF0, 0x1EE), (0xA6E390, 0x140), (0xA6DB70, 0x74B), (0xA6B3B0, 0x8F1), (0xA6BF70, 0x473), (0xA6D1A0, 0x9CB),
        };
        private const string CodeHash = "e2ba89bdfbc17a3f1948dc594f7f261f53573dd73fe0d12061d9366a7f101b44";
        private const int ManagerLoad = 0x110136;
        // The manager's handle count and table (16-byte entries: salt, then the engine's link reference at +8) and its NavMesh,
        // and in that the free head, record capacity, record array, live head and the change counter that every addition and
        // removal advances. A record holds its agent type at +0 and its bounds from +4 (x, y, z, then the maximum).
        private const int HandleCount = 0x84, Handles = 0x88, NavMeshField = 0xA8;
        private const int FreeHead = 0x80, Capacity = 0x84, Records = 0x88, LiveHead = 0x90, Changes = 0x94;
        private const float Cell = 16f;
        private static readonly AccessTools.StructFieldRef<NavMeshLinkInstance, int> Handle =
            AccessTools.StructFieldRefAccess<NavMeshLinkInstance, int>("<id>k__BackingField");
        private static readonly AccessTools.StructFieldRef<NavMeshDataInstance, int> MeshHandle =
            AccessTools.StructFieldRefAccess<NavMeshDataInstance, int>("<id>k__BackingField");
        private static readonly Func<NavMeshData, NavMeshBuildSettings> BuildSettings =
            AccessTools.MethodDelegate<Func<NavMeshData, NavMeshBuildSettings>>(AccessTools.PropertyGetter(typeof(NavMeshData), "buildSettings"));

        private static IntPtr manager;
        private static bool active, dirty;
        private static bool[] free = new bool[0];

        // The isolated client switches the link index and the meshes' shortened chains off and on for paired measurements.
        internal static bool Indexing = true, MeshIndexing = true;
        // Each live record's predecessor in the chain, -1 at the head, and its place, higher nearer the head, valid while the
        // engine's NavMesh, live head and change counter are the ones recorded here.
        private static int[] before = new int[0];
        private static long[] place = new long[0];
        private static long clock;
        private static byte* indexed;
        private static int indexedHead, indexedChanges, foreign;
        internal static int Readings, Presentations;
        private static readonly Dictionary<(int agent, int x, int z), List<int>> cells = new Dictionary<(int, int, int), List<int>>();
        // The area, agent type and margin of each mesh added here, by handle.
        private static readonly Dictionary<int, (int agent, float x0, float z0, float x1, float z1)> meshes = new Dictionary<int, (int, float, float, float, float)>();
        // The records the chain holds while a mesh is added or removed, their next records and salts before, and the head.
        internal static readonly List<int> Presented = new List<int>();
        private static int[] nexts = new int[0], salts = new int[0], marks = new int[0];
        private static long[] keys = new long[0];
        private static int mark, presentedHead;
        private static bool[] linked = new bool[0];

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        internal static void Start()
        {
            var module = GetModuleHandleW("UnityPlayer.dll");
            if (module == IntPtr.Zero) throw new InvalidOperationException("UnityPlayer.dll is not loaded");
            int header = Marshal.ReadInt32(module + 0x3C), imageSize = Marshal.ReadInt32(module + header + 0x50);
            byte[] code = new byte[0];
            foreach (var (start, length) in Code)
            {
                if (start + length > imageSize) throw new InvalidOperationException("UnityPlayer.dll is not the build whose navigation records this reads");
                int at = code.Length;
                Array.Resize(ref code, at + length);
                Marshal.Copy(module + start, code, at, length);
            }
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(code)).Replace("-", "").ToLowerInvariant();
            if (hash != CodeHash) throw new InvalidOperationException("UnityPlayer.dll is not the build whose navigation records this reads (code " + hash + ")");
            manager = module + ManagerLoad + 7 + Marshal.ReadInt32(module + ManagerLoad + 3);
            active = true;
        }

        private static byte* Owner() => *(byte**)manager;
        private static byte* NavMeshOf(byte* owner) => owner == null ? null : *(byte**)(owner + NavMeshField);
        private static byte* RecordOf(byte* navMesh, int slot) => *(byte**)(navMesh + Records) + (long)slot * Record;
        private static int* NextOf(byte* navMesh, int slot) => (int*)(RecordOf(navMesh, slot) + Next);
        // The engine's counter skips zero when it wraps.
        private static int Advanced(int changes) => changes == -1 ? 1 : changes + 1;

        // Called after links were removed.
        internal static void Removed() => dirty = true;

        // Called before links are added.
        internal static void BeforeAdding()
        {
            if (!active || !dirty) return;
            dirty = false;
            var navMesh = NavMeshOf(Owner());
            if (navMesh == null) return;
            int capacity = *(int*)(navMesh + Capacity);
            if (free.Length < capacity) free = new bool[capacity];
            else Array.Clear(free, 0, capacity);
            int count = 0;
            for (int slot = *(int*)(navMesh + FreeHead); slot != -1; slot = *NextOf(navMesh, slot))
            {
                if ((uint)slot >= (uint)capacity || free[slot])
                {
                    active = false;
                    Plugin.Fallback("PathfindingRecords stopped: the free navigation records do not form a list");
                    return;
                }
                free[slot] = true;
                count++;
            }
            int previous = -1;
            for (int slot = capacity - 1; slot >= 0 && count > 0; slot--)
            {
                if (!free[slot]) continue;
                *NextOf(navMesh, slot) = previous;
                previous = slot;
                count--;
            }
            *(int*)(navMesh + FreeHead) = previous;
        }

        // NavMesh.AddLink, recording the new record at the head of the chain.
        internal static NavMeshLinkInstance Add(NavMeshLinkData data)
        {
            var navMesh = active && Indexing ? NavMeshOf(Owner()) : null;
            bool current = navMesh != null && Current(navMesh);
            var link = NavMesh.AddLink(data);
            if (!current) return link;
            int head = *(int*)(navMesh + LiveHead), changes = *(int*)(navMesh + Changes);
            if (head == indexedHead && changes == indexedChanges) return link;
            if (changes != Advanced(indexedChanges) || head == -1 || *NextOf(navMesh, head) != indexedHead)
            {
                indexed = null;
                return link;
            }
            Fit(navMesh);
            before[head] = -1;
            if (indexedHead != -1) before[indexedHead] = head;
            place[head] = ++clock;
            Enter(navMesh, head, true);
            indexedHead = head;
            indexedChanges = changes;
            return link;
        }

        // NavMesh.RemoveLink, with the link's record first moved to the head of the chain.
        internal static void Remove(NavMeshLinkInstance link)
        {
            var navMesh = active && Indexing ? NavMeshOf(Owner()) : null;
            int slot = navMesh == null ? -1 : Slot(navMesh, Handle(ref link));
            if (slot < 0 || !Current(navMesh) && !Read(navMesh) || !Lift(navMesh, slot) && !(Read(navMesh) && Lift(navMesh, slot)))
            {
                NavMesh.RemoveLink(link);
                return;
            }
            int rest = *NextOf(navMesh, slot), salt = *(int*)(RecordOf(navMesh, slot) + Salt);
            Enter(navMesh, slot, false);
            NavMesh.RemoveLink(link);
            int head = *(int*)(navMesh + LiveHead), changes = *(int*)(navMesh + Changes);
            if (head != rest || changes != Advanced(indexedChanges) || *(int*)(RecordOf(navMesh, slot) + Salt) == salt)
            {
                indexed = null;
                return;
            }
            if (head != -1) before[head] = -1;
            indexedHead = head;
            indexedChanges = changes;
        }

        // NavMesh.AddNavMeshData, with only the records near the mesh in the chain.
        internal static NavMeshDataInstance AddMesh(NavMeshData data)
        {
            var area = Area(data);
            var navMesh = area.HasValue ? Present(area.Value) : null;
            var instance = NavMesh.AddNavMeshData(data);
            if (navMesh != null) Restore(navMesh);
            if (area.HasValue && instance.valid) meshes[MeshHandle(ref instance)] = area.Value;
            return instance;
        }

        // The game removed its meshes natively; their handles are never removed here.
        internal static void Forget() => meshes.Clear();

        // NavMesh.RemoveNavMeshData, with only the records near the mesh in the chain.
        internal static void RemoveMesh(NavMeshDataInstance instance)
        {
            int handle = MeshHandle(ref instance);
            var navMesh = meshes.TryGetValue(handle, out var area) ? Present(area) : null;
            meshes.Remove(handle);
            NavMesh.RemoveNavMeshData(instance);
            if (navMesh != null) Restore(navMesh);
        }

        // The mesh's area with the margin within which the engine connects a record to its tiles: their overhang past the
        // area, at most one tile, and the agent's radius, by which the engine widens a tile's bounds for that test.
        private static (int agent, float x0, float z0, float x1, float z1)? Area(NavMeshData data)
        {
            if (!active || !Indexing || !MeshIndexing || data == null || data.rotation != Quaternion.identity) return null;
            var settings = BuildSettings(data);
            float voxel = settings.overrideVoxelSize ? settings.voxelSize : settings.agentRadius / 3f;
            float tile = (settings.overrideTileSize ? settings.tileSize : 256) * voxel, margin = tile + settings.agentRadius + 1f;
            if (!(margin > 0f && margin < 1024f)) return null;
            var bounds = data.sourceBounds;
            var center = data.position + bounds.center;
            return (settings.agentTypeID, center.x - bounds.extents.x - margin, center.z - bounds.extents.z - margin,
                center.x + bounds.extents.x + margin, center.z + bounds.extents.z + margin);
        }

        // Leaves only the records of the area's agent type whose cells it covers in the chain, in their order.
        private static byte* Present((int agent, float x0, float z0, float x1, float z1) area)
        {
            var navMesh = NavMeshOf(Owner());
            if (navMesh == null || !Current(navMesh) && !Read(navMesh) || foreign > 0) return null;
            Presented.Clear();
            mark++;
            for (int x = Floor(area.x0); x <= Floor(area.x1); x++)
                for (int z = Floor(area.z0); z <= Floor(area.z1); z++)
                {
                    if (!cells.TryGetValue((area.agent, x, z), out var cell)) continue;
                    foreach (int slot in cell)
                    {
                        if (marks[slot] == mark) continue;
                        marks[slot] = mark;
                        Presented.Add(slot);
                    }
                }
            int count = Presented.Count;
            if (keys.Length < count) { keys = new long[count * 2]; nexts = new int[count * 2]; salts = new int[count * 2]; }
            for (int index = 0; index < count; index++) keys[index] = -place[Presented[index]];
            var sorted = Presented.ToArray();
            Array.Sort(keys, sorted, 0, count);
            Presented.Clear();
            Presented.AddRange(sorted);
            for (int index = 0; index < count; index++)
            {
                int slot = Presented[index];
                nexts[index] = *NextOf(navMesh, slot);
                salts[index] = *(int*)(RecordOf(navMesh, slot) + Salt);
                *NextOf(navMesh, slot) = index + 1 < count ? Presented[index + 1] : -1;
            }
            presentedHead = count > 0 ? Presented[0] : -1;
            *(int*)(navMesh + LiveHead) = presentedHead;
            Presentations++;
            return navMesh;
        }

        // Puts the whole chain back. The engine adds and removes no records while it adds or removes a generated mesh; if it
        // did, the chain is rebuilt with its additions in front and without the records it freed, and meshes see the whole
        // chain from then on.
        private static void Restore(byte* navMesh)
        {
            int head = *(int*)(navMesh + LiveHead), changes = *(int*)(navMesh + Changes), count = Presented.Count;
            var added = new List<int>();
            if (changes != indexedChanges)
                for (int slot = head; slot != -1 && marks[slot] != mark && added.Count <= count + 1024; slot = *NextOf(navMesh, slot)) added.Add(slot);
            for (int index = 0; index < count; index++)
                if (*(int*)(RecordOf(navMesh, Presented[index]) + Salt) == salts[index]) *NextOf(navMesh, Presented[index]) = nexts[index];
            *(int*)(navMesh + LiveHead) = indexedHead;
            if (changes == indexedChanges && head == presentedHead) return;
            MeshIndexing = false;
            Plugin.Fallback("PathfindingRecords stopped shortening the chain for meshes: the engine changed links while adding or removing a mesh");
            // Freed records lie in the chain where their predecessors still point; skip them.
            var freed = new HashSet<int>();
            for (int index = 0; index < count; index++)
                if (*(int*)(RecordOf(navMesh, Presented[index]) + Salt) != salts[index]) freed.Add(Presented[index]);
            int previous = -1, first = -1, steps = 0, capacity = *(int*)(navMesh + Capacity);
            for (int slot = indexedHead; slot != -1 && steps++ <= capacity; )
            {
                int next = freed.Contains(slot) ? nexts[Presented.IndexOf(slot)] : *NextOf(navMesh, slot);
                if (!freed.Contains(slot))
                {
                    if (previous == -1) first = slot; else *NextOf(navMesh, previous) = slot;
                    previous = slot;
                }
                slot = next;
            }
            if (previous != -1) *NextOf(navMesh, previous) = -1;
            if (added.Count > 0) *NextOf(navMesh, added[added.Count - 1]) = first;
            *(int*)(navMesh + LiveHead) = added.Count > 0 ? added[0] : first;
            indexed = null;
        }

        private static int Floor(float value) => (int)Math.Floor(value / Cell);

        // Adds a live record to the cells its bounds cover, or takes it out.
        private static void Enter(byte* navMesh, int slot, bool add)
        {
            var record = RecordOf(navMesh, slot);
            int agent = *(int*)record;
            float* bounds = (float*)(record + 4);
            for (int x = Floor(bounds[0]); x <= Floor(bounds[3]); x++)
                for (int z = Floor(bounds[2]); z <= Floor(bounds[5]); z++)
                {
                    var key = (agent, x, z);
                    if (add)
                    {
                        if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new List<int>();
                        cell.Add(slot);
                    }
                    else if (cells.TryGetValue(key, out var cell))
                    {
                        int at = cell.IndexOf(slot);
                        if (at < 0) continue;
                        cell[at] = cell[cell.Count - 1];
                        cell.RemoveAt(cell.Count - 1);
                        if (cell.Count == 0) cells.Remove(key);
                    }
                }
        }

        // The live record a link handle names, with the checks NavMesh_CUSTOM_RemoveLinkInternal and
        // NavMesh::RemoveOffMeshConnection make before removing it, or -1.
        private static int Slot(byte* navMesh, int handle)
        {
            var owner = Owner();
            int index = handle & 0xFFFF;
            if ((uint)index >= *(uint*)(owner + HandleCount)) return -1;
            var entry = *(byte**)(owner + Handles) + index * 16;
            if (*(int*)entry != ((handle >> 16) & 0xFFFF)) return -1;
            long reference = *(long*)(entry + 8);
            if (((reference >> 16) & 0xF) != 1) return -1;
            int slot = (int)(reference & 0xFFFF);
            if ((uint)slot >= *(uint*)(navMesh + Capacity)) return -1;
            if (*(int*)(RecordOf(navMesh, slot) + Salt) != (int)((ulong)reference >> 48)) return -1;
            return slot;
        }

        private static bool Current(byte* navMesh) =>
            navMesh == indexed && *(int*)(navMesh + LiveHead) == indexedHead && *(int*)(navMesh + Changes) == indexedChanges;

        private static void Fit(byte* navMesh)
        {
            int capacity = *(int*)(navMesh + Capacity);
            if (before.Length >= capacity) return;
            int length = Math.Max(capacity, before.Length * 2);
            Array.Resize(ref before, length);
            Array.Resize(ref place, length);
            Array.Resize(ref marks, length);
            Array.Resize(ref linked, length);
        }

        // Reads every live record's predecessor, place and cells from the chain, and counts the records that are not
        // NavMesh.AddLink's, which the engine may add or remove while it adds or removes a mesh.
        private static bool Read(byte* navMesh)
        {
            indexed = null;
            Fit(navMesh);
            int capacity = *(int*)(navMesh + Capacity), steps = 0, previous = -1;
            var owner = Owner();
            Array.Clear(linked, 0, linked.Length);
            var table = *(byte**)(owner + Handles);
            for (int index = *(int*)(owner + HandleCount) - 1; index >= 0; index--)
            {
                long reference = *(long*)(table + index * 16 + 8);
                if (reference != 0 && ((reference >> 16) & 0xF) == 1 && (int)(reference & 0xFFFF) < capacity) linked[(int)(reference & 0xFFFF)] = true;
            }
            cells.Clear();
            foreign = 0;
            long top = clock + capacity;
            for (int slot = *(int*)(navMesh + LiveHead); slot != -1; slot = *NextOf(navMesh, slot))
            {
                if ((uint)slot >= (uint)capacity || ++steps > capacity)
                {
                    Indexing = false;
                    Plugin.Fallback("PathfindingRecords stopped indexing: the live navigation records do not form a list");
                    return false;
                }
                before[slot] = previous;
                place[slot] = top - steps;
                if (!linked[slot]) foreign++;
                Enter(navMesh, slot, true);
                previous = slot;
            }
            clock = top;
            indexed = navMesh;
            indexedHead = *(int*)(navMesh + LiveHead);
            indexedChanges = *(int*)(navMesh + Changes);
            Readings++;
            return true;
        }

        // Moves a live record to the head of the chain, keeping the order of the others.
        private static bool Lift(byte* navMesh, int slot)
        {
            int previous = before[slot];
            if (previous == -1) return indexedHead == slot;
            if (*NextOf(navMesh, previous) != slot) return false;
            int rest = *NextOf(navMesh, slot);
            *NextOf(navMesh, previous) = rest;
            if (rest != -1) before[rest] = previous;
            *NextOf(navMesh, slot) = indexedHead;
            before[indexedHead] = slot;
            before[slot] = -1;
            *(int*)(navMesh + LiveHead) = slot;
            indexedHead = slot;
            return true;
        }
    }
}
