using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace WorldPerformance
{
    // WieldEquipmentWhileSwimming 1.1.4 answers each swimming check of a swimming player by building a stack trace and
    // looking for a method whose description contains UpdateEquipment or EquipItem among its eight callers: about 300
    // checks a second while swimming, 77-85 microseconds each, 24-26 ms of every second on the main thread. Every method
    // whose description could match, in the game and the installed mods, now marks while it runs. A check with none of them
    // running cannot match, so it skips the mod's search and gets the game's own answer, as the mod would give it; checks
    // inside them still go through the mod unchanged. The methods are found from the assemblies' metadata names first, so
    // reflection reads only the few types those name: 0.4 s of the start against 2.6 s for reading every type.
    internal static class SwimmingEquipmentChecks
    {
        // The isolated test client sets false to compare the mod's own search on every check.
        internal static bool Skipping = true;
        internal static long Skipped, Searched;
        internal static int Marked;
        [ThreadStatic] private static int running;
        private static readonly string[] Names = { "UpdateEquipment", "EquipItem" };

        internal static void Initialize(PerformancePatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.WieldWhileSwimming, out var plugin)) return;
            var check = AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("WieldEquipmentWhileSwimming.Plugin+Patches"), "IsSwimming_Prefix")
                ?? throw new MissingMethodException("WieldEquipmentWhileSwimming's swimming check was not found.");
            harmony.Patch(check, prefix: new HarmonyMethod(typeof(SwimmingEquipmentChecks), nameof(Skip)));
            var enter = new HarmonyMethod(typeof(SwimmingEquipmentChecks), nameof(Enter)) { priority = Priority.First };
            var leave = new HarmonyMethod(typeof(SwimmingEquipmentChecks), nameof(Leave)) { priority = Priority.Last };
            foreach (var method in Matching()) { harmony.Patch(method, prefix: enter, finalizer: leave); Marked++; }
        }

        // The game's and the plugins' methods whose stack trace description names either method: their declaring type,
        // name, return type, parameter types or parameter names contain it. Members that refer to an assembly that is not
        // installed cannot run, so they are left out. The isolated test client compares the scan of every type, prefilter
        // false.
        internal static List<MethodBase> Matching(bool prefilter = true)
        {
            var found = new List<MethodBase>();
            var assemblies = new HashSet<Assembly> { typeof(Humanoid).Assembly };
            foreach (var info in Chainloader.PluginInfos.Values) if (info.Instance != null) assemblies.Add(info.Instance.GetType().Assembly);
            // Reflection writes the assemblies of generic arguments into type names; an assembly whose name holds either
            // name could make such a type match without its metadata naming it.
            if (AppDomain.CurrentDomain.GetAssemblies().Any(assembly => Matches(assembly.FullName))) prefilter = false;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var assembly in assemblies)
            {
                var candidates = prefilter ? Candidates(assembly) : null;
                foreach (var type in candidates == null ? AllTypes(assembly) : Resolve(assembly, candidates))
                {
                    MethodBase[] methods;
                    try
                    {
                        if (type.ContainsGenericParameters) continue;
                        methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)).ToArray();
                    }
                    catch (Exception error) when (Unresolved(error)) { continue; }
                    bool typeMatches = Matches(type.FullName);
                    foreach (var method in methods)
                        try
                        {
                            if (!typeMatches && !Matches(method.Name) && !(method is MethodInfo info && Matches(info.ReturnType.FullName)) &&
                                !method.GetParameters().Any(parameter => Matches(parameter.Name) || Matches(parameter.ParameterType.FullName))) continue;
                            if (!method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null) found.Add(method);
                        }
                        catch (Exception error) when (Unresolved(error)) { }
                }
            }
            return found;
        }

        private static IList<Type> AllTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException error) { return error.Types.Where(type => type != null).ToArray(); }
        }

        // The candidate types by name. A type that cannot load is one the full listing leaves out as well; a name the
        // runtime cannot answer, or one the type-name grammar would read differently, leaves the assembly to the listing.
        private static IList<Type> Resolve(Assembly assembly, List<string> names)
        {
            var types = new List<Type>();
            foreach (string name in names)
            {
                if (name.IndexOfAny(new[] { ',', '[', ']', '&', '*', '\\' }) >= 0) return AllTypes(assembly);
                Type type;
                try { type = assembly.GetType(name, false); }
                catch (Exception error) when (Unresolved(error)) { continue; }
                if (type == null || type.Assembly != assembly) return AllTypes(assembly);
                types.Add(type);
            }
            return types;
        }

        // The full names of the types whose metadata names either method in the type's own name or in one of its methods' names,
        // return types, parameter names or parameter types: every type the reflection scan could match, and seldom more,
        // since metadata names hold the same namespaces, type names and generic arguments without assembly names. Null
        // when the assembly's file cannot be read, which leaves every type to the reflection scan.
        private static List<string> Candidates(Assembly assembly)
        {
            try
            {
                string path = assembly.Location;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                var candidates = new List<string>();
                using (var definition = AssemblyDefinition.ReadAssembly(path))
                    foreach (var module in definition.Modules)
                        foreach (var type in module.GetTypes())
                            if (Matches(type.FullName) || type.Methods.Any(method => Matches(method.Name) || Matches(method.ReturnType.FullName) ||
                                method.Parameters.Any(parameter => Matches(parameter.Name) || Matches(parameter.ParameterType.FullName))))
                                candidates.Add(type.FullName.Replace('/', '+'));
                return candidates;
            }
            catch (Exception) { return null; }
        }

        private static bool Unresolved(Exception error) => error is TypeLoadException || error is System.IO.FileNotFoundException || error is System.IO.FileLoadException;

        private static bool Matches(string text) => text != null && (text.Contains(Names[0]) || text.Contains(Names[1]));

        private static void Enter(out bool __state) { running++; __state = true; }
        private static void Leave(bool __state) { if (__state) running--; }

        // Answers for the mod's prefix: true runs the game's own check, which the mod does whenever no matching caller runs.
        private static bool Skip(ref bool __result)
        {
            if (!Skipping || running > 0) { Searched++; return true; }
            Skipped++;
            __result = true;
            return false;
        }
    }
}
