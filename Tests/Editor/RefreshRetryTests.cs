using System;
using System.Reflection;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace I1rr.UnityMcp.Editor.Tests
{
    public class RefreshRetryTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void RetainedRefreshMatchesImportOptionsBeforeDispatch(bool legacyRecord)
        {
            var jobs = (JObject)typeof(RefreshRequests).GetField("Jobs", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            string id = Guid.NewGuid().ToString("N");
            var record = new JObject { ["request_id"] = id, ["status"] = "settled", ["compilation_complete"] = true };
            if (!legacyRecord) record["import_options"] = (int)ImportAssetOptions.Default;
            jobs[id] = record;
            try
            {
                var args = new JObject { ["request_id"] = id };
                var result = RefreshRequests.Request(args, ImportAssetOptions.Default);
                if (legacyRecord)
                {
                    Assert.That((string)result["error_code"], Is.EqualTo(McpErrorCodes.InvalidState));
                    Assert.That((string)result["error_message"], Does.Contain("get_refresh_status"));
                }
                else
                {
                    Assert.That(JToken.DeepEquals(result, record), Is.True);
                    Assert.That(ReferenceEquals(result, record), Is.False);
                    result["status"] = "modified clone";
                    Assert.That((string)record["status"], Is.EqualTo("settled"));
                    result = RefreshRequests.Request(args, ImportAssetOptions.ForceSynchronousImport);
                    Assert.That((string)result["error_code"], Is.EqualTo(McpErrorCodes.InvalidParameter));
                }
                Assert.That(ReferenceEquals(jobs[id], record), Is.True);
                Assert.That((string)RefreshRequests.Status(args)["status"], Is.EqualTo("settled"));
            }
            finally { jobs.Remove(id); }
        }
    }
}
