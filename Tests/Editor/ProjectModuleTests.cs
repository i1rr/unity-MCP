using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class ProjectModuleTests
    {
        private string _root, _other;
        [SetUp] public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "McpModules-" + Guid.NewGuid().ToString("N"));
            _other = Path.Combine(_root, "OtherProject"); Directory.CreateDirectory(_other);
        }
        [TearDown] public void TearDown() => Directory.Delete(_root, true);

        [Test] public void CreateOwnsEditorSourceAndRefusesDuplicateOrUnsafeNames()
        {
            string folder = (string)McpProjectModules.Create(_root, "PortableSample", "Framework integration.")["path"];
            Assert.That(File.ReadAllText(Path.Combine(folder, "Editor", "PortableSampleModule.cs")), Does.Contain("portablesample_module_info"));
            var asm = JObject.Parse(File.ReadAllText(Path.Combine(folder, "Editor", "PortableSample.Editor.asmdef")));
            Assert.That((string)asm["includePlatforms"][0], Is.EqualTo("Editor"));
            Assert.Throws<IOException>(() => McpProjectModules.Create(_root, "PortableSample", ""));
            Assert.Throws<ArgumentException>(() => McpProjectModules.Create(_root, "../escape", ""));
            Assert.Throws<ArgumentException>(() => McpProjectModules.Create(_root, "Addressables", ""));
        }
        [Test] public void ExportInspectImportPreservesSourceAndMetadataAndStartsDisabled()
        {
            McpProjectModules.Create(_root, "PortableSample", "Reusable tools.");
            var exported = McpProjectModules.Export(_root, "PortableSample", "module.zip");
            File.Copy((string)exported["path"], Path.Combine(_other, "module.zip"));
            var preview = McpProjectModules.InspectArchive(_other, "module.zip");
            Assert.That((string)preview["sha256"], Is.EqualTo((string)exported["sha256"]));
            Assert.That(((JArray)preview["missing_dependencies"]).Count, Is.Zero);
            Assert.That(((JObject)preview["source_preview"]).Count, Is.EqualTo(1));
            string target = (string)McpProjectModules.Import(_other, "module.zip", (string)preview["sha256"])["path"];
            string source = McpProjectModules.Folder(_root, "PortableSample");
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                Assert.That(File.ReadAllBytes(Path.Combine(target, file.Substring(source.Length + 1))), Is.EqualTo(File.ReadAllBytes(file)));
            Assert.That(((JArray)McpProjectModules.ReadPolicy(_other)["disabled"]).Values<string>(), Does.Contain("PortableSample"));
            Assert.Throws<IOException>(() => McpProjectModules.Import(_other, "module.zip", (string)preview["sha256"]));
        }
        [Test] public void WrongHashMissingDependenciesAndGuidConflictsNeverInstallSource()
        {
            McpProjectModules.Create(_root, "PortableSample", ""); McpProjectModules.Export(_root, "PortableSample", "module.zip");
            string path = Path.Combine(_other, "module.zip"); File.Copy(Path.Combine(_root, "module.zip"), path);
            Assert.Throws<IOException>(() => McpProjectModules.Import(_other, "module.zip", new string('0', 64)));
            Rewrite(path, files =>
            {
                string key = "Editor/PortableSample.Editor.asmdef";
                var asm = JObject.Parse(Encoding.UTF8.GetString(files[key])); asm["references"] = new JArray("Missing.Framework.Assembly");
                files[key] = Encoding.UTF8.GetBytes(asm.ToString());
            });
            var preview = McpProjectModules.InspectArchive(_other, "module.zip");
            Assert.That(((JArray)preview["missing_dependencies"]).Values<string>(), Does.Contain("Missing.Framework.Assembly"));
            Assert.Throws<IOException>(() => McpProjectModules.Import(_other, "module.zip", (string)preview["sha256"]));
            File.Copy(Path.Combine(_root, "module.zip"), path, true);
            Rewrite(path, files => { var metas = files.Keys.Where(k => k.EndsWith(".meta")).Take(2).ToArray(); files[metas[1]] = files[metas[0]]; });
            preview = McpProjectModules.InspectArchive(_other, "module.zip");
            Assert.Throws<IOException>(() => McpProjectModules.Import(_other, "module.zip", (string)preview["sha256"]));
            Assert.That(Directory.Exists(McpProjectModules.Folder(_other, "PortableSample")), Is.False);
            Assert.That(File.Exists(McpProjectModules.PolicyPath(_other)), Is.False);
        }
        [TestCase("../escape.cs")] [TestCase("Editor/../../escape.cs")] [TestCase("Editor/payload.dll")] [TestCase("RootScript.cs")]
        public void RejectsUnsafeArchiveEntriesBeforeWriting(string entry)
        {
            McpProjectModules.Create(_root, "PortableSample", ""); McpProjectModules.Export(_root, "PortableSample", "module.zip");
            Rewrite(Path.Combine(_root, "module.zip"), files => files[entry] = Encoding.UTF8.GetBytes("payload"));
            Assert.Throws<IOException>(() => McpProjectModules.InspectArchive(_root, "module.zip"));
            Assert.That(File.Exists(Path.Combine(_root, "escape.cs")), Is.False);
        }
        [Test] public void PolicyPreservesOtherSettingsAndNeverResetsMalformedInput()
        {
            McpProjectModules.SetEnabled(_root, "PortableSample", false);
            string path = McpProjectModules.PolicyPath(_root);
            var policy = JObject.Parse(File.ReadAllText(path)); policy["owner_note"] = "retain"; File.WriteAllText(path, policy.ToString());
            McpProjectModules.SetEnabled(_root, "PortableSample", true);
            Assert.That((string)McpProjectModules.ReadPolicy(_root)["owner_note"], Is.EqualTo("retain"));
            Assert.That(((JArray)McpProjectModules.ReadPolicy(_root)["disabled"]).Count, Is.Zero);
            File.WriteAllText(path, "{invalid");
            Assert.Throws<Newtonsoft.Json.JsonReaderException>(() => McpProjectModules.SetEnabled(_root, "PortableSample", false));
            Assert.That(File.ReadAllText(path), Is.EqualTo("{invalid"));
        }
        private static void Rewrite(string path, Action<Dictionary<string, byte[]>> mutate)
        {
            var files = new Dictionary<string, byte[]>();
            using (var zip = ZipFile.OpenRead(path)) foreach (var entry in zip.Entries)
            { using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); files[entry.FullName] = buffer.ToArray(); }
            mutate(files); File.Delete(path);
            using var output = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in files) { using var stream = output.CreateEntry(file.Key).Open(); stream.Write(file.Value, 0, file.Value.Length); }
        }
    }
}
