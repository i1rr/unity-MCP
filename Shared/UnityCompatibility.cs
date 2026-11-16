using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>Current Unity APIs with compile-time fallbacks for the 2022.3 minimum.</summary>
    public static class UnityCompatibility
    {
        public static IReadOnlyList<Assembly> GetLoadedAssemblies()
        {
#if UNITY_6000_6_OR_NEWER
            // Unity tracks the live load context; AppDomain can include unloaded assemblies.
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }

        public static string GetLoadedAssemblyPath(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
#if UNITY_6000_6_OR_NEWER
            return assembly.GetLoadedAssemblyPath();
#else
            return assembly.Location;
#endif
        }

        /// <summary>Finds scene objects without imposing an ordering, including inactive objects when requested.</summary>
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive = FindObjectsInactive.Exclude)
            where T : UnityEngine.Object
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>(inactive);
#else
            return UnityEngine.Object.FindObjectsByType<T>(inactive, FindObjectsSortMode.None);
#endif
        }
    }
}
