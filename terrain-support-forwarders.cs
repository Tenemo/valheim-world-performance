using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace WorldPerformance
{
    // Every building piece adds its own handler to its terrain patch's support event when it starts and removes it when it is
    // destroyed, and each change copies the patch's whole handler list: a zone with n pieces copies about n squared over two
    // handlers on arrival and again on departure, an estimated 16-36 MB of garbage for the base's 2,000-3,000 pieces. Each
    // terrain patch now has one handler that clears the cached support of the pieces registered with it, in the order they
    // registered, as the game's own list would. A piece registered while forwarding was off keeps its own handler.
    internal static class TerrainSupportForwarders
    {
        // The isolated test client sets false to compare the game's own handlers.
        internal static bool Forwarding = true;
        private static readonly Dictionary<Heightmap, Pieces> patches = new Dictionary<Heightmap, Pieces>();

        private sealed class Pieces
        {
            // Removed pieces leave a gap until half the list is gaps, so the remaining order and each removal's cost hold.
            internal readonly List<Action> Handlers = new List<Action>();
            internal readonly Dictionary<object, int> Index = new Dictionary<object, int>();
            internal int Gaps;
            internal readonly Action Forward;

            internal Pieces() => Forward = Clear;

            private void Clear()
            {
                for (int index = 0, count = Handlers.Count; index < count && index < Handlers.Count; index++) Handlers[index]?.Invoke();
            }

            internal void Remove(int index)
            {
                Handlers[index] = null;
                if (++Gaps * 2 < Handlers.Count) return;
                Handlers.RemoveAll(handler => handler == null);
                Gaps = 0;
                Index.Clear();
                for (int position = 0; position < Handlers.Count; position++) Index[Handlers[position].Target] = position;
            }
        }

        private static readonly MethodInfo add = AccessTools.Method(typeof(Heightmap), "add_m_clearConnectedWearNTearCache");
        private static readonly MethodInfo remove = AccessTools.Method(typeof(Heightmap), "remove_m_clearConnectedWearNTearCache");

        private static void Add(Heightmap map, Action handler)
        {
            if (!Forwarding)
            {
                map.m_clearConnectedWearNTearCache += handler;
                return;
            }
            if (!patches.TryGetValue(map, out var pieces))
            {
                pieces = new Pieces();
                map.m_clearConnectedWearNTearCache += pieces.Forward;
                patches[map] = pieces;
            }
            if (pieces.Index.ContainsKey(handler.Target))
            {
                map.m_clearConnectedWearNTearCache += handler;
                return;
            }
            pieces.Index[handler.Target] = pieces.Handlers.Count;
            pieces.Handlers.Add(handler);
        }

        private static void Remove(Heightmap map, Action handler)
        {
            if (patches.TryGetValue(map, out var pieces) && pieces.Index.TryGetValue(handler.Target, out int index))
            {
                pieces.Index.Remove(handler.Target);
                pieces.Remove(index);
            }
            else map.m_clearConnectedWearNTearCache -= handler;
        }

        [HarmonyPatch(typeof(WearNTear), "Start")]
        private static class Started
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(add), AccessTools.Method(typeof(TerrainSupportForwarders), nameof(Add)), 1,
                    "The game's piece terrain subscription changed.");
        }

        [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
        private static class Destroyed
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(remove), AccessTools.Method(typeof(TerrainSupportForwarders), nameof(Remove)), 1,
                    "The game's piece terrain unsubscription changed.");
        }

        [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
        private static class PatchDestroyed
        {
            private static void Postfix(Heightmap __instance) => patches.Remove(__instance);
        }
    }
}
