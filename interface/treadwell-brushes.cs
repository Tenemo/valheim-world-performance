using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // Treadwell 1.0.1 looks for the hammer's Level ground, Pathen and Paved road pieces every time the game lists the
    // pieces it can build, which it does after every placement and every other inventory change: for each piece in the
    // table it lists every Piece, TerrainOp and TerrainModifier in the piece's prefab, about 6 ms. Its classifier accepts
    // only a piece whose own Piece is named $piece_levelground, $piece_path or $piece_pavedroad, and both of its searches
    // skip every piece it does not classify, so it is now handed only those pieces and finds the same brushes.
    internal static class TreadwellBrushes
    {
        // The isolated test client sets false to compare Treadwell's full search.
        internal static bool Filtering = true;
        private static readonly string[] brushNames = { "$piece_levelground", "$piece_path", "$piece_pavedroad" };

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.Treadwell, out var plugin)) return;
            var module = plugin.Instance.GetType().Assembly.GetType("Treadwell.RoadFeatureModule");
            var inspect = module == null ? null : AccessTools.Method(module, "InspectPieceTable", new[] { typeof(PieceTable) });
            if (inspect == null) throw new InvalidOperationException("Treadwell's piece table search changed.");
            harmony.Patch(inspect, transpiler: new HarmonyMethod(typeof(TreadwellBrushes), nameof(Transpiler)));
            log.LogInfo("Treadwell looks for its terrain brushes only among the pieces it can recognize.");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var pieces = AccessTools.Field(typeof(PieceTable), nameof(PieceTable.m_pieces));
            var enumerate = AccessTools.Method(typeof(List<GameObject>), nameof(List<GameObject>.GetEnumerator));
            var loads = Enumerable.Range(0, code.Count - 1).Where(index => code[index].LoadsField(pieces) && code[index + 1].Calls(enumerate)).ToList();
            if (loads.Count != 1) throw new InvalidOperationException("Treadwell's piece table search changed.");
            code[loads[0]].opcode = OpCodes.Call;
            code[loads[0]].operand = AccessTools.Method(typeof(TreadwellBrushes), nameof(Candidates));
            return code;
        }

        // Treadwell checks the list for null first, so it is never null here.
        private static List<GameObject> Candidates(PieceTable table)
        {
            if (!Filtering) return table.m_pieces;
            var result = new List<GameObject>();
            foreach (var prefab in table.m_pieces)
            {
                if (!prefab) continue;
                var piece = prefab.GetComponent<Piece>();
                if (piece && Array.IndexOf(brushNames, piece.m_name) >= 0) result.Add(prefab);
            }
            return result;
        }
    }
}
