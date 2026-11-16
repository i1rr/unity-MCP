using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// MCP resource: editor://project
    /// Provides project-level metadata: Unity version, .NET API level, scripting backend, and installed packages.
    /// </summary>
    public static class ProjectInfoResource
    {
        [InitializeOnLoadMethod]
        private static void Register()
        {
            McpResourceRegistry.Register(
                "editor://project",
                "Project Info",
                "Unity project metadata: version, .NET API level, scripting backend, and installed packages from manifest.json.",
                GetProjectInfo);
        }

        private static JObject GetProjectInfo()
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
            var result = new JObject
            {
                ["unityVersion"] = Application.unityVersion,
                ["apiCompatibilityLevel"] = PlayerSettings.GetApiCompatibilityLevel(target).ToString(),
                ["scriptingBackend"] = PlayerSettings.GetScriptingBackend(target).ToString(),
                ["productName"] = Application.productName,
                ["companyName"] = Application.companyName,
                ["dataPath"] = Application.dataPath
            };

            var packages = ReadManifestPackages();
            if (packages != null)
                result["packages"] = packages;

            return result;
        }

        private static JObject ReadManifestPackages()
        {
            var manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");
            manifestPath = Path.GetFullPath(manifestPath);

            if (!File.Exists(manifestPath))
                return null;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JObject.Parse(json);
                return manifest["dependencies"] as JObject;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
