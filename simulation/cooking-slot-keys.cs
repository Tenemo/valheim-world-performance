using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace WorldPerformance
{
    // Cooking stations name their slots' saved values "slot" or "slotstatus" followed by the slot number, and build and hash
    // that name on every read: each station's slot checks run every frame for its hover text, its moving parts and its
    // cooking. The game's string-named reads and writes only hash the name and pass it on, so each slot's hashes are now
    // computed once and passed on directly. A station with a moving part also checked whether it was a placement preview
    // and whether all its slots were full before reading its fuel; with no moving part, or no fuel, it now stops there.
    internal static class CookingSlotKeys
    {
        private static readonly int[] slots = new int[64], statuses = new int[64];

        internal static void Initialize(PerformancePatches harmony)
        {
            var keys = new HarmonyMethod(typeof(CookingSlotKeys), nameof(KeysTranspiler));
            foreach (string method in new[] { "GetSlot", "SetSlot", "IsEmpty", "GetFreeSlot" })
                harmony.Patch(AccessTools.Method(typeof(CookingStation), method), transpiler: keys);
            harmony.Patch(AccessTools.Method(typeof(CookingStation), "Update"), transpiler: new HarmonyMethod(typeof(CookingSlotKeys), nameof(UpdateTranspiler)));
            for (int slot = 0; slot < slots.Length; slot++)
            {
                slots[slot] = ("slot" + slot).GetStableHashCode();
                statuses[slot] = ("slotstatus" + slot).GetStableHashCode();
            }
        }

        // ldstr prefix; ldarga or ldloca slot; call int.ToString(); call string.Concat becomes ldarg or ldloc slot; call Key,
        // and the next ZDO call that took the name takes the hash instead.
        private static IEnumerable<CodeInstruction> KeysTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var toString = AccessTools.Method(typeof(int), nameof(int.ToString), Type.EmptyTypes);
            var concat = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) });
            var overloads = new Dictionary<MethodInfo, (MethodInfo hashed, bool owner)>
            {
                { AccessTools.Method(typeof(ZDO), nameof(ZDO.GetString), new[] { typeof(string), typeof(string) }), (AccessTools.Method(typeof(ZDO), nameof(ZDO.GetString), new[] { typeof(int), typeof(string) }), false) },
                { AccessTools.Method(typeof(ZDO), nameof(ZDO.GetFloat), new[] { typeof(string), typeof(float) }), (AccessTools.Method(typeof(ZDO), nameof(ZDO.GetFloat), new[] { typeof(int), typeof(float) }), false) },
                { AccessTools.Method(typeof(ZDO), nameof(ZDO.GetInt), new[] { typeof(string), typeof(int) }), (AccessTools.Method(typeof(ZDO), nameof(ZDO.GetInt), new[] { typeof(int), typeof(int) }), false) },
                { AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(string), typeof(string) }), (AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(string) }), false) },
                { AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(string), typeof(float) }), (AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(float) }), false) },
                // The string-named int write passes okForNotOwner as false.
                { AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(string), typeof(int) }), (AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(int), typeof(bool) }), true) },
            };
            var code = instructions.ToList();
            int replaced = 0;
            for (int index = 0; index + 3 < code.Count; index++)
            {
                if (code[index].opcode != OpCodes.Ldstr || !code[index + 2].Calls(toString) || !code[index + 3].Calls(concat)) continue;
                string prefix = (string)code[index].operand;
                var load = code[index + 1];
                if ((prefix != "slot" && prefix != "slotstatus") || (load.opcode != OpCodes.Ldarga_S && load.opcode != OpCodes.Ldloca_S) ||
                    code[index + 1].labels.Count > 0 || code[index + 2].labels.Count > 0 || code[index + 3].labels.Count > 0)
                    throw new InvalidOperationException("The cooking station's slot names changed.");
                int use = code.FindIndex(index + 4, instruction => instruction.operand is MethodInfo method && overloads.ContainsKey(method));
                var takes = use < 0 ? null : (MethodInfo)code[use].operand;
                if (takes == null || code.Skip(index + 4).Take(use - index - 4).Any(instruction => instruction.operand is MethodBase))
                    throw new InvalidOperationException("The cooking station's slot names changed.");
                // The slot's value now goes where the name went: the first instruction keeps its jump labels.
                code[index].opcode = load.opcode == OpCodes.Ldarga_S ? OpCodes.Ldarg_S : OpCodes.Ldloc_S;
                code[index].operand = load.operand;
                code[index + 1].opcode = OpCodes.Call;
                code[index + 1].operand = AccessTools.Method(typeof(CookingSlotKeys), prefix == "slot" ? nameof(Slot) : nameof(Status));
                code[index + 2].opcode = code[index + 3].opcode = OpCodes.Nop;
                code[index + 2].operand = code[index + 3].operand = null;
                var (hashed, owner) = overloads[takes];
                code[use].opcode = OpCodes.Callvirt;
                code[use].operand = hashed;
                if (owner) code.Insert(use, new CodeInstruction(OpCodes.Ldc_I4_0));
                replaced++;
            }
            if (replaced == 0) throw new InvalidOperationException("The cooking station's slot names changed.");
            return code;
        }

        internal static int Slot(int slot) => slot >= 0 && slot < slots.Length ? slots[slot] : ("slot" + slot).GetStableHashCode();

        internal static int Status(int slot) => slot >= 0 && slot < statuses.Length ? statuses[slot] : ("slotstatus" + slot).GetStableHashCode();

        // !Player.IsPlacementGhost(gameObject) && m_moveObjectWhileCooking, and then IsStationFull() && !IsEverythingCooked() &&
        // GetFuel() > 0f: the field is tested first, and the fuel first when the station's view is valid, where all three
        // only read it.
        private static IEnumerable<CodeInstruction> UpdateTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var gameObject = AccessTools.PropertyGetter(typeof(UnityEngine.Component), nameof(UnityEngine.Component.gameObject));
            var ghost = AccessTools.Method(typeof(Player), nameof(Player.IsPlacementGhost));
            var full = AccessTools.Method(typeof(CookingStation), "IsStationFull");
            var code = instructions.ToList();
            int preview = code.FindIndex(instruction => instruction.Calls(ghost));
            int fullCall = code.FindIndex(instruction => instruction.Calls(full));
            if (preview != 2 || !code[1].Calls(gameObject) || code[1].labels.Count > 0 || code[2].labels.Count > 0 || fullCall < 0 || code.FindLastIndex(instruction => instruction.Calls(full)) != fullCall)
                throw new InvalidOperationException("The cooking station's update changed.");
            code[1].opcode = OpCodes.Nop;
            code[1].operand = null;
            code[2].operand = AccessTools.Method(typeof(CookingSlotKeys), nameof(StillOrPreview));
            code[fullCall].operand = AccessTools.Method(typeof(CookingSlotKeys), nameof(FullAndFuelled));
            return code;
        }

        // True ends the update, as the game's would end or do nothing for a station without a moving part.
        internal static bool StillOrPreview(CookingStation station) => !station.m_moveObjectWhileCooking || Player.IsPlacementGhost(station.gameObject);

        private static readonly AccessTools.FieldRef<CookingStation, ZNetView> view = AccessTools.FieldRefAccess<CookingStation, ZNetView>("m_nview");
        private static readonly Func<CookingStation, bool> isFull = AccessTools.MethodDelegate<Func<CookingStation, bool>>(AccessTools.Method(typeof(CookingStation), "IsStationFull"));
        private static readonly Func<CookingStation, float> fuel = AccessTools.MethodDelegate<Func<CookingStation, float>>(AccessTools.Method(typeof(CookingStation), "GetFuel"));

        // False makes the moving part's target 0, as an empty fuel tank would at the end of the game's test.
        internal static bool FullAndFuelled(CookingStation station)
        {
            var own = view(station);
            if (own != null && own.IsValid() && !(fuel(station) > 0f)) return false;
            return isFull(station);
        }
    }
}
