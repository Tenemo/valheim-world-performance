using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WorldPerformance
{
    // Unity keeps every navigation link in one array of 208-byte records, chained newest first, and removing a link walks
    // that chain from its head. A freed record goes on top of a free list that hands records out last in, first out, so
    // through a session each new tile's links landed in records scattered by earlier removals, and every step of the walk
    // missed the cache: at Tenemo's base the cost of each removal per link held rose from 8.6 to 13.5 ns within an hour.
    // Before links are added after any removal, the free list is now chained again in ascending record order, so new links
    // take neighbouring records. Only unused records change; every link keeps its data, its order and its routes.
    // The record layout is read from UnityPlayer.dll's own code, so this runs only on the client build whose code matches.
    internal static class PathfindingRecords
    {
        private const int Record = 0xD0, Next = 0xC4;
        // The client's UnityPlayer.dll of Unity 6000.0.75f1 (file sha256 4d161e15d8ccdb32...): NavMesh_CUSTOM_RemoveLinkInternal,
        // which loads the navigation manager from a global, the record allocator, NavMesh::RemoveOffMeshConnection and the
        // start of NavMesh::AddOffMeshConnection, as relative addresses and lengths. None of these bytes are relocated.
        private static readonly (int start, int length)[] Code = { (0x110130, 0xAE), (0xA75BF0, 0x1EE), (0xA6E390, 0x140), (0xA6DB70, 0x1C0) };
        private const string CodeHash = "f84aa64e272ab5a004a31c0679b2e684078c7be41297e50e747ca9d0f3ddc366";
        private const int ManagerLoad = 0x110136;
        // The manager's NavMesh, and in it the free head, record capacity, record array.
        private const int NavMeshField = 0xA8, FreeHead = 0x80, Capacity = 0x84, Records = 0x88;

        private static IntPtr manager;
        private static bool active, dirty;
        private static bool[] free = new bool[0];

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

        // Called after links were removed.
        internal static void Removed() => dirty = true;

        // Called before links are added.
        internal static void BeforeAdding()
        {
            if (!active || !dirty) return;
            dirty = false;
            var owner = Marshal.ReadIntPtr(manager);
            if (owner == IntPtr.Zero) return;
            var navMesh = Marshal.ReadIntPtr(owner + NavMeshField);
            if (navMesh == IntPtr.Zero) return;
            int capacity = Marshal.ReadInt32(navMesh + Capacity);
            var records = Marshal.ReadIntPtr(navMesh + Records);
            if (free.Length < capacity) free = new bool[capacity];
            else Array.Clear(free, 0, capacity);
            int count = 0;
            for (int slot = Marshal.ReadInt32(navMesh + FreeHead); slot != -1; slot = Marshal.ReadInt32(records + slot * Record + Next))
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
                Marshal.WriteInt32(records + slot * Record + Next, previous);
                previous = slot;
                count--;
            }
            Marshal.WriteInt32(navMesh + FreeHead, previous);
        }
    }
}
