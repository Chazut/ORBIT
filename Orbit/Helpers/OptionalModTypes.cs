using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace Orbit.Helpers;

// Optional integrations use fully qualified names. Assembly.GetType avoids enumerating every
// type in every plugin, which can stall the first Ghost decision even when a lookup succeeds.
internal static class OptionalModTypes
{
    private readonly struct Entry(Type type, int generation)
    {
        internal readonly Type Type = type;
        internal readonly int Generation = generation;
    }

    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.Ordinal);
    private static int _generation;

    static OptionalModTypes()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
        {
            if (!args.LoadedAssembly.IsDynamic) Interlocked.Increment(ref _generation);
        };
    }

    internal static Type Find(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        lock (Cache)
        {
            var generation = Volatile.Read(ref _generation);
            if (Cache.TryGetValue(fullName, out var entry)
                && (entry.Type != null || entry.Generation == generation)) return entry.Type;

            using var timing = PerformanceJournal.Measure(TransitionPhase.OptionalTypeLookup,
                "optional-type-lookup", fullName, always: true);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // A dynamic assembly can gain new types without another AssemblyLoad event.
                // Optional BepInEx plugins are ordinary assemblies; generated wrappers are not bindings.
                if (assembly.IsDynamic) continue;
                Type type;
                try { type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false); }
                catch (TypeLoadException) { continue; }
                catch (ReflectionTypeLoadException) { continue; }
                catch (System.IO.FileNotFoundException) { continue; }
                catch (System.IO.FileLoadException) { continue; }
                catch (BadImageFormatException) { continue; }
                if (type == null) continue;
                Cache[fullName] = new Entry(type, generation);
                return type;
            }
            // A later plugin load invalidates misses, while successful bindings remain reusable.
            Cache[fullName] = new Entry(null, generation);
            return null;
        }
    }
}
