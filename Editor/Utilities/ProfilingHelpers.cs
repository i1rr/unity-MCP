using System;
using Unity.Profiling;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Maps category name strings to Unity <see cref="ProfilerCategory"/> values
    /// for on-demand profiler recorder creation.
    /// </summary>
    internal static class ProfilingHelpers
    {
        public static ProfilerCategory GetProfilerCategory(string name)
        {
            return name?.ToLowerInvariant() switch
            {
                "render" => ProfilerCategory.Render,
                "scripts" => ProfilerCategory.Scripts,
                "memory" => ProfilerCategory.Memory,
                "physics" => ProfilerCategory.Physics,
                "animation" => ProfilerCategory.Animation,
                "audio" => ProfilerCategory.Audio,
                "gc" => ProfilerCategory.Memory,
                "internal" => ProfilerCategory.Internal,
                "input" => ProfilerCategory.Input,
                "gui" or "ui" => ProfilerCategory.Gui,
                "network" => ProfilerCategory.Network,
                "video" => ProfilerCategory.Video,
                "loading" => ProfilerCategory.Loading,
                "vr" => ProfilerCategory.Vr,
                _ => ProfilerCategory.Scripts
            };
        }

        public static Type FindUnityType(string typeName)
        {
            // Try common Unity types first
            var type = typeof(UnityEngine.Object).Assembly.GetType($"UnityEngine.{typeName}");
            if (type != null) return type;

            // Try direct
            type = Type.GetType(typeName);
            if (type != null) return type;

            // Search all loaded assemblies
            foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null && typeof(UnityEngine.Object).IsAssignableFrom(type))
                    return type;

                type = assembly.GetType($"UnityEngine.{typeName}");
                if (type != null && typeof(UnityEngine.Object).IsAssignableFrom(type))
                    return type;
            }

            return null;
        }
    }
}
