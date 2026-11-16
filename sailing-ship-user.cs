using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;

namespace WorldPerformance
{
    // Sailing 1.1.12 finds each ship's steering player on every physics step with a search that makes a new delegate and
    // reads who steers the ship again for every player, and reads and writes the player's sailing skill by name. The search
    // now reads the steering player once and walks the player list in the same order, and the skill is read and written
    // through its precomputed name hash, which is all the named calls do with the name.
    internal static class SailingShipUser
    {
        private static readonly int skill = "Sailing Skill".GetStableHashCode();

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.Sailing, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var speed = assembly.GetType("Sailing.Sailing+ChangeShipSpeed");
            var update = assembly.GetType("Sailing.Sailing+PlayerUpdate");
            harmony.Patch(speed == null ? null : AccessTools.Method(speed, "Postfix"), transpiler: new HarmonyMethod(typeof(SailingShipUser), nameof(SpeedTranspiler)));
            harmony.Patch(update == null ? null : AccessTools.Method(update, "Postfix"), transpiler: new HarmonyMethod(typeof(SailingShipUser), nameof(SkillTranspiler)));
            log.LogInfo("Sailing finds a ship's steering player without a new search delegate and reads the sailing skill by its hash.");
        }

        // Player.s_players.FirstOrDefault(p => p.GetPlayerID() == __instance.m_shipControlls.GetUser()) becomes
        // User(Player.s_players, __instance), with __instance read from the closure the method already filled.
        private static IEnumerable<CodeInstruction> SpeedTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = Hashed(instructions, AccessTools.Method(typeof(ZDO), nameof(ZDO.GetFloat), new[] { typeof(string), typeof(float) }),
                AccessTools.Method(typeof(ZDO), nameof(ZDO.GetFloat), new[] { typeof(int), typeof(float) }));
            int call = code.FindIndex(instruction => instruction.operand is MethodInfo method && method.Name == nameof(Enumerable.FirstOrDefault) && method.DeclaringType == typeof(Enumerable));
            if (call < 3 || code[call - 1].opcode != OpCodes.Newobj || code[call - 2].opcode != OpCodes.Ldftn || !code[call - 3].IsLdloc() ||
                code[call - 1].labels.Count > 0 || code[call - 2].labels.Count > 0 || code[call].labels.Count > 0)
                throw new InvalidOperationException("Sailing's ship speed changed.");
            var closure = ((MethodInfo)code[call - 2].operand).DeclaringType;
            var ship = AccessTools.Field(closure, "__instance") ?? throw new InvalidOperationException("Sailing's ship speed changed.");
            code[call - 2].opcode = OpCodes.Ldfld;
            code[call - 2].operand = ship;
            code[call - 1].opcode = OpCodes.Nop;
            code[call - 1].operand = null;
            code[call].operand = AccessTools.Method(typeof(SailingShipUser), nameof(User));
            return code;
        }

        private static IEnumerable<CodeInstruction> SkillTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Hashed(instructions, AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(string), typeof(float) }),
                AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(float) }));

        // ldstr "Sailing Skill" ... call named becomes ldc.i4 hash ... call hashed.
        private static List<CodeInstruction> Hashed(IEnumerable<CodeInstruction> instructions, MethodInfo named, MethodInfo hashed)
        {
            var code = instructions.ToList();
            int call = code.FindIndex(instruction => instruction.Calls(named));
            int name = call < 0 ? -1 : code.FindLastIndex(call, instruction => instruction.opcode == OpCodes.Ldstr && (string)instruction.operand == "Sailing Skill");
            if (name < 0 || code.FindLastIndex(instruction => instruction.Calls(named)) != call ||
                code.Skip(name + 1).Take(call - name - 1).Any(instruction => instruction.operand is MethodInfo method && method.ReturnType == typeof(string)))
                throw new InvalidOperationException("Sailing's skill value changed.");
            code[name].opcode = OpCodes.Ldc_I4;
            code[name].operand = skill;
            code[call].operand = hashed;
            return code;
        }

        // The search's answer: the first player whose id is the ship's steering player's.
        internal static Player User(List<Player> players, Ship ship)
        {
            if (players.Count == 0) return null;
            long user = ship.m_shipControlls.GetUser();
            foreach (var player in players)
                if (player.GetPlayerID() == user) return player;
            return null;
        }
    }
}
