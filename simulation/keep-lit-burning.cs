using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WorldPerformance
{
    // NoSmokeStayLit 2.4.4 decides after every Fireplace.IsBurning whether a fire stays lit, burns on its timer and smokes.
    // Each call read the time of day by reflection, looked up the fire's network view it already holds, read the fire's
    // name up to eight times and split both custom fire lists for every setting it checked. The same decision now reads
    // the name and the time of day once and compares the lists in place, with the same order of reads and writes. Calls
    // the mod would fail on, without a world environment, with a name under 7 characters or with a list missing, still
    // run the mod's own code.
    internal static class KeepLitBurning
    {
        // The shipped feature; the isolated client switches it off and on for paired measurements.
        internal static bool Shortcut = true;
        internal static long Decided, Native;
        private static readonly AccessTools.FieldRef<EnvMan, float> dayFraction = AccessTools.FieldRefAccess<EnvMan, float>("m_smoothDayFraction");
        private static readonly AccessTools.FieldRef<Fireplace, bool> wet = AccessTools.FieldRefAccess<Fireplace, bool>("m_wet");
        private static readonly int fuel = "fuel".GetStableHashCode(), torchMode = "TT_toggleOn".GetStableHashCode();
        private static ConfigEntry<bool> firePit, firePitIron, bonfire, hearth, wallTorch, groundTorch, woodTorch, greenTorch, blueTorch, brazierFloor,
            brazierCeiling, jackOTurnip, oven, smelter, blastFurnace, eitrRefinery, bathtub, candle, shieldGenerator;
        private static ConfigEntry<bool> wallTorchTimer, groundTorchTimer, woodTorchTimer, greenTorchTimer, blueTorchTimer, brazierFloorTimer,
            brazierCeilingTimer, jackOTurnipTimer, candleTimer;
        private static ConfigEntry<bool> firePitSmoke, firePitIronSmoke, hearthSmoke, brazierFloorSmoke, brazierCeilingSmoke, smelterSmoke, ovenSmoke,
            bonfireSmoke, keepOnInRain, alwaysOnInDarkBiomes;
        private static ConfigEntry<string> customLit, customTimer;
        private static AccessTools.FieldRef<float> timerOn, timerOff;

        internal static void Initialize(PerformancePatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.NoSmokeStayLit, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var configs = assembly.GetType("NoSmokeStayLit.Patches.Configs");
            var patch = assembly.GetType("NoSmokeStayLit.Patches.FireplaceIsBurning_Patch");
            if (configs == null || patch == null) throw new InvalidOperationException("NoSmokeStayLit's fire settings changed.");
            ConfigEntry<T> Entry<T>(string name) =>
                AccessTools.Field(configs, name)?.GetValue(null) as ConfigEntry<T> ?? throw new InvalidOperationException("NoSmokeStayLit's " + name + " setting changed.");
            firePit = Entry<bool>("fe_fire_pit"); firePitIron = Entry<bool>("fe_fire_pit_iron"); bonfire = Entry<bool>("fe_bonfire"); hearth = Entry<bool>("fe_hearth");
            wallTorch = Entry<bool>("fe_piece_walltorch"); groundTorch = Entry<bool>("fe_piece_groundtorch"); woodTorch = Entry<bool>("fe_piece_groundtorch_wood");
            greenTorch = Entry<bool>("fe_piece_groundtorch_green"); blueTorch = Entry<bool>("fe_piece_groundtorch_blue");
            brazierFloor = Entry<bool>("fe_piece_brazierfloor01"); brazierCeiling = Entry<bool>("fe_piece_brazierceiling01");
            jackOTurnip = Entry<bool>("fe_piece_jackoturnip"); oven = Entry<bool>("fe_piece_oven"); smelter = Entry<bool>("fe_smelter");
            blastFurnace = Entry<bool>("fe_blastfurnace"); eitrRefinery = Entry<bool>("fe_eitrrefinery"); bathtub = Entry<bool>("fe_piece_bathtub");
            candle = Entry<bool>("fe_piece_candle"); shieldGenerator = Entry<bool>("fe_piece_shieldGenerator");
            wallTorchTimer = Entry<bool>("fe_piece_walltorch_timer"); groundTorchTimer = Entry<bool>("fe_piece_groundtorch_timer");
            woodTorchTimer = Entry<bool>("fe_piece_groundtorch_wood_timer"); greenTorchTimer = Entry<bool>("fe_piece_groundtorch_green_timer");
            blueTorchTimer = Entry<bool>("fe_piece_groundtorch_blue_timer"); brazierFloorTimer = Entry<bool>("fe_piece_brazierfloor01_timer");
            brazierCeilingTimer = Entry<bool>("fe_piece_brazierceiling01_timer"); jackOTurnipTimer = Entry<bool>("fe_piece_jackoturnip_timer");
            candleTimer = Entry<bool>("fe_piece_Candle_resin_timer");
            firePitSmoke = Entry<bool>("fe_fire_pit_smoke"); firePitIronSmoke = Entry<bool>("fe_fire_pit_iron_smoke"); hearthSmoke = Entry<bool>("fe_hearth_smoke");
            brazierFloorSmoke = Entry<bool>("fe_piece_brazierfloor01_smoke"); brazierCeilingSmoke = Entry<bool>("fe_piece_brazierceiling01_smoke");
            smelterSmoke = Entry<bool>("fe_smelter_smoke"); ovenSmoke = Entry<bool>("fe_oven"); bonfireSmoke = Entry<bool>("fe_bonfire_smoke");
            keepOnInRain = Entry<bool>("keepOnInRain"); alwaysOnInDarkBiomes = Entry<bool>("configAlwaysOnInDarkBiomes");
            customLit = Entry<string>("fe_custom_instance"); customTimer = Entry<string>("fe_custom_instance_timer");
            FieldInfo Time(string name) => AccessTools.Field(configs, name) is FieldInfo field && field.IsStatic && field.FieldType == typeof(float) ? field :
                throw new InvalidOperationException("NoSmokeStayLit's " + name + " changed.");
            timerOn = AccessTools.StaticFieldRefAccess<float>(Time("timerOnFloatTime"));
            timerOff = AccessTools.StaticFieldRefAccess<float>(Time("timerOffFloatTime"));
            var postfix = AccessTools.Method(patch, "Postfix", new[] { typeof(Fireplace), typeof(bool).MakeByRefType(), typeof(GameObject).MakeByRefType(), typeof(ZNetView).MakeByRefType() });
            harmony.Patch(postfix, prefix: new HarmonyMethod(typeof(KeepLitBurning), nameof(Decide)));
            log.LogInfo("NoSmokeStayLit's lit fires are decided without reflection or repeated name reads.");
        }

        // Fireplace.m_nview is the fire's own network view, which the mod looks up again; the game reads it before this runs.
        private static bool Decide(Fireplace __0, ref bool __1, ref ZNetView __3)
        {
            var fire = __0;
            if (!Shortcut || !EnvMan.instance || !fire || customLit.Value == null || customTimer.Value == null) { Native++; return true; }
            string name = fire.name;
            if (name.Length < 7) { Native++; return true; }
            Decided++;
            float day = dayFraction(EnvMan.instance);
            var view = __3;
            if (!Lit(name))
            {
                if (view == null || !view.IsValid() || view.GetZDO() == null) return false;
                float left = view.GetZDO().GetFloat(fuel, -1f);
                if (left < 0f) return false;
                if (left <= 0f) { __1 = false; return false; }
            }
            bool fueled;
            if (view == null || !view.IsValid()) fueled = true;
            else
            {
                int mode = TorchMode(view, name);
                if (mode == 0) { __1 = false; return false; }
                if (!Lit(name)) fueled = true;
                else
                {
                    __1 = true;
                    if (keepOnInRain.Value) wet(fire) = false;
                    if (mode == 1 || !Timed(name)) { Smoke(fire, name); return false; }
                    fueled = false;
                }
            }
            if (fueled)
            {
                __1 = true;
                float left = __3.GetZDO().GetFloat(fuel, -1f);
                if (left < 0f && fire.m_startFuel > 0f) return false;
                if ((int)Math.Ceiling(left) == 0 && !Lit(name)) { __1 = false; return false; }
            }
            int timer = TorchMode(view, name);
            if (timer != 1)
            {
                bool lit = true;
                if (Timed(name) || timer == 2)
                {
                    bool dark = EnvMan.instance.GetCurrentEnvironment()?.m_alwaysDark ?? false;
                    float on = timerOn(), off = timerOff();
                    if (off > on ? (day <= on && day >= 1f) || day >= off : day <= on && day >= off)
                    {
                        if (!lit || (dark && alwaysOnInDarkBiomes.Value)) { __1 = true; return false; }
                        lit = false;
                        __1 = false;
                    }
                }
            }
            Smoke(fire, name);
            return false;
        }

        private static void Smoke(Fireplace fire, string name)
        {
            if (Smoking(name))
            {
                if (fire.m_smokeSpawner != null) fire.m_smokeSpawner.enabled = true;
                if (name.StartsWith("piece_brazier")) Utils.FindChild(fire.transform, "SmokeSpawner").gameObject.GetComponent<SmokeSpawner>().enabled = true;
                if (keepOnInRain.Value) wet(fire) = false;
            }
            else if (fire.m_smokeSpawner != null)
            {
                fire.m_smokeSpawner.enabled = false;
                if (keepOnInRain.Value) wet(fire) = false;
            }
            else if (name.StartsWith("piece_brazier"))
            {
                Utils.FindChild(fire.transform, "SmokeSpawner").gameObject.GetComponent<SmokeSpawner>().enabled = false;
                if (keepOnInRain.Value) wet(fire) = false;
            }
            else if (keepOnInRain.Value) wet(fire) = false;
        }

        private static int TorchMode(ZNetView view, string name)
        {
            if (view == null || !view.IsValid() || view.GetZDO() == null) return 1;
            bool timed = Timed(name);
            int mode = view.GetZDO().GetInt(torchMode, timed ? 2 : 1);
            return !timed && mode > 1 ? 1 : mode;
        }

        // The mod's Split(',').Contains(name without "(Clone)"), compared in place.
        private static bool Listed(string list, string name)
        {
            int length = name.Length - 7, start = 0;
            while (true)
            {
                int comma = list.IndexOf(',', start), end = comma < 0 ? list.Length : comma;
                if (end - start == length && string.CompareOrdinal(list, start, name, 0, length) == 0) return true;
                if (comma < 0) return false;
                start = comma + 1;
            }
        }

        private static bool Lit(string name)
        {
            bool result = false;
            switch (name)
            {
                case "fire_pit(Clone)": result = firePit.Value; break;
                case "fire_pit_iron(Clone)": result = firePitIron.Value; break;
                case "bonfire(Clone)": result = bonfire.Value; break;
                case "hearth(Clone)": result = hearth.Value; break;
                case "piece_walltorch(Clone)": result = wallTorch.Value; break;
                case "piece_groundtorch(Clone)": result = groundTorch.Value; break;
                case "piece_groundtorch_wood(Clone)": result = woodTorch.Value; break;
                case "piece_groundtorch_green(Clone)": result = greenTorch.Value; break;
                case "piece_groundtorch_blue(Clone)": result = blueTorch.Value; break;
                case "piece_brazierfloor01(Clone)": result = brazierFloor.Value; break;
                case "piece_brazierfloor02(Clone)": result = brazierFloor.Value; break;
                case "piece_brazierceiling01(Clone)": result = brazierCeiling.Value; break;
                case "piece_jackoturnip(Clone)": result = jackOTurnip.Value; break;
                case "piece_oven(Clone)": result = oven.Value; break;
                case "smelter(Clone)": result = smelter.Value; break;
                case "blastfurnace(Clone)": result = blastFurnace.Value; break;
                case "eitrrefinery(Clone)": result = eitrRefinery.Value; break;
                case "piece_bathtub(Clone)": result = bathtub.Value; break;
                case "Candle_resin(Clone)":
                case "Candle_resin_bogwitch(Clone)": result = candle.Value; break;
                case "piece_shieldgenerator(Clone)": result = shieldGenerator.Value; break;
            }
            if (Listed(customLit.Value, name)) result = true;
            return result;
        }

        private static bool Timed(string name)
        {
            bool result = false;
            switch (name)
            {
                case "piece_walltorch(Clone)": result = wallTorchTimer.Value; break;
                case "piece_groundtorch(Clone)": result = groundTorchTimer.Value; break;
                case "piece_groundtorch_wood(Clone)": result = woodTorchTimer.Value; break;
                case "piece_groundtorch_green(Clone)": result = greenTorchTimer.Value; break;
                case "piece_groundtorch_blue(Clone)": result = blueTorchTimer.Value; break;
                case "piece_brazierfloor01(Clone)": result = brazierFloorTimer.Value; break;
                case "piece_brazierceiling01(Clone)": result = brazierCeilingTimer.Value; break;
                case "piece_brazierceiling02(Clone)": result = brazierCeilingTimer.Value; break;
                case "piece_jackoturnip(Clone)": result = jackOTurnipTimer.Value; break;
                case "Candle_resin(Clone)":
                case "Candle_resin_bogwitch(Clone)": result = candleTimer.Value; break;
            }
            if (Listed(customTimer.Value, name)) result = true;
            return result;
        }

        private static bool Smoking(string name)
        {
            switch (name)
            {
                case "fire_pit(Clone)": return firePitSmoke.Value;
                case "fire_pit_iron(Clone)": return firePitIronSmoke.Value;
                case "hearth(Clone)": return hearthSmoke.Value;
                case "piece_brazierfloor01(Clone)": return brazierFloorSmoke.Value;
                case "piece_brazierfloor02(Clone)": return brazierFloorSmoke.Value;
                case "piece_brazierceiling01(Clone)": return brazierCeilingSmoke.Value;
                case "smelter(Clone)": return smelterSmoke.Value;
                case "piece_oven(Clone)": return ovenSmoke.Value;
                case "bonfire(Clone)": return bonfireSmoke.Value;
                default: return false;
            }
        }
    }
}
