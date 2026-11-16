using System.IO;
using I1rr.UnityMcp.Shared;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    public sealed class McpBuildProcessor : BuildPlayerProcessor, IPostprocessBuildWithReport, IUnityLinkerProcessor
    {
        private const string TokenName = "mcp_runtime.token";
        private static string StagePath => Path.GetFullPath(Path.Combine("Temp", "i1rr-unity-mcp", TokenName));

        public override int callbackOrder => -100;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            PrepareToken(Application.streamingAssetsPath, StagePath,
                (context.BuildPlayerOptions.options & BuildOptions.Development) != 0,
                (source, destination) => context.AddAdditionalPathToStreamingAssets(source, destination));
        }

        internal static void PrepareToken(string streamingAssets, string stagePath, bool development,
            System.Action<string, string> include)
        {
            // Unity copies the consumer's StreamingAssets into every build. Never modify them.
            if (File.Exists(Path.Combine(streamingAssets, TokenName)))
                throw new BuildFailedException($"Move the existing StreamingAssets/{TokenName} out of StreamingAssets before building.");

            CleanStage(stagePath);
            if (!development) return;

            Directory.CreateDirectory(Path.GetDirectoryName(stagePath));
            try
            {
                File.WriteAllText(stagePath, McpAuth.GenerateToken());
                McpAuth.RestrictFilePermissions(stagePath);
                include(stagePath, TokenName);
            }
            catch
            {
                CleanStage(stagePath);
                throw;
            }
        }

        // A project's link.xml is discovered automatically; a UPM package's is not.
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityEditor.UnityLinker.UnityLinkerBuildPipelineData data)
        {
            if ((report.summary.options & BuildOptions.Development) == 0) return null;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(McpBuildProcessor).Assembly);
            return Path.Combine(package.resolvedPath, "link.xml");
        }

        public void OnPostprocessBuild(BuildReport report) => CleanStage(StagePath);

        private static void CleanStage(string path)
        {
            // Temp is project-local. A failed or cancelled build may leave this one staged file.
            if (McpPathValidator.ContainsSymlink(path))
                throw new BuildFailedException("The build token staging path must not contain links.");
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
