using System;
using System.Reflection;
using HarmonyLib;

namespace WorldPerformance
{
    // Harmony's TypeByName finds another mod's type by listing every type of every loaded assembly, about 200 of them in
    // the pack, and each search after another mod loaded makes the runtime load that mod's types: 2.3-2.4 s of each start
    // in the hidden test client, and Star Level System searches again at every join. A plain name is now looked up in each assembly directly, and a type of exactly that full name that only one assembly
    // defines answers, which is the one type the listing can reach. A full name no assembly holds has no type, as
    // after Harmony's listing. Names two assemblies define, names the lookup cannot answer exactly, and short names, which
    // Harmony also compares with each type's own name, still go to Harmony's own search.
    internal static class TypeLookups
    {
        // The isolated test client sets false to compare Harmony's search; it is read at every call.
        internal static bool Direct = true;
        private static readonly System.Version Supported = new System.Version(2, 9, 0, 0);
        private static readonly Assembly harmonyAssembly = typeof(AccessTools).Assembly, coreAssembly = typeof(object).Assembly;

        internal static void Start(Harmony harmony)
        {
            var version = typeof(AccessTools).Assembly.GetName().Version;
            if (version != Supported)
                throw new InvalidOperationException("Harmony " + version + " is not the supported " + Supported + ".");
            harmony.Patch(AccessTools.Method(typeof(AccessTools), nameof(AccessTools.TypeByName), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(TypeLookups), nameof(Prefix)) { priority = Priority.First });
        }

        private static bool Prefix(string name, ref Type __result)
        {
            if (!Direct || !Plain(name)) return true;
            // Harmony first asks the runtime, which looks in Harmony's own assembly and the core library; such a name stays
            // with Harmony, which answers it without the listing.
            if (harmonyAssembly.GetType(name, false) != null || coreAssembly.GetType(name, false) != null) return true;
            Type only = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.FullName.StartsWith("Microsoft.VisualStudio", StringComparison.Ordinal)) continue;
                Type found;
                try { found = assembly.GetType(name, false, false); }
                catch (Exception) { return true; }
                if (found == null) continue;
                // A forwarded name answers with another assembly's type, and a name two assemblies define depends on which
                // of them the listing reaches first; both stay with Harmony.
                if (found.FullName != name || found.Assembly != assembly || only != null) return true;
                only = found;
            }
            // No assembly holds the full name, and Harmony's last step compares short names, which hold no dots or pluses: an
            // optional integration's name of a mod that isn't installed has no type.
            if (only == null && name.IndexOf('.') < 0 && name.IndexOf('+') < 0) return true;
            __result = only;
            return false;
        }

        // Letters, digits, underscores, dots, nested-type pluses and generic arity marks: names whose full-name match a
        // direct lookup gives without the type-name grammar's brackets, commas, escapes or compiler-made angle brackets.
        private static bool Plain(string name)
        {
            if (string.IsNullOrEmpty(name) || name[0] == '.' || name[0] == '+') return false;
            foreach (char letter in name)
                if (!char.IsLetterOrDigit(letter) && letter != '_' && letter != '.' && letter != '+' && letter != '`') return false;
            return true;
        }
    }
}
