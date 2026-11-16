using System;
using System.Collections.Generic;
using System.Linq;
using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace I1rr.UnityMcp.Editor.Addressables
{
    [McpToolCategory("Addressables")]
    public static class AddressablesTools
    {
        private const int DefaultPageSize = 50;
        private const int MaxPageSize = 500;

        #region get_addressable_groups

        [McpTool("get_addressable_groups",
            "Returns all Addressable groups with entry counts and schema information.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetAddressableGroups(JObject parameters)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return Error("Addressable Asset Settings not found. Ensure Addressables is initialized.");

            var groupsArray = new JArray();
            foreach (var group in settings.groups)
            {
                if (group == null) continue;

                var schemas = new JArray();
                foreach (var schema in group.Schemas)
                {
                    if (schema == null) continue;
                    schemas.Add(schema.GetType().Name);
                }

                groupsArray.Add(new JObject
                {
                    ["name"] = group.Name,
                    ["guid"] = group.Guid,
                    ["entry_count"] = group.entries.Count,
                    ["read_only"] = group.ReadOnly,
                    ["is_default"] = group == settings.DefaultGroup,
                    ["schemas"] = schemas
                });
            }

            return new JObject
            {
                ["groups"] = groupsArray,
                ["total_count"] = groupsArray.Count
            };
        }

        #endregion

        #region get_addressable_entries

        [McpTool("get_addressable_entries",
            "Returns Addressable entries in a group, optionally filtered by label. Pages are 1-indexed.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""group"": { ""type"": ""string"", ""description"": ""Group name to list entries from"" },
                    ""label"": { ""type"": ""string"", ""description"": ""Optional label filter — only entries with this label are returned"" },
                    ""page_size"": { ""type"": ""integer"", ""description"": ""Results per page (1-500, default 50)"", ""default"": 50 },
                    ""page"": { ""type"": ""integer"", ""description"": ""Page number (1-indexed, default 1)"", ""default"": 1 }
                },
                ""required"": [""group""]
            }")]
        public static JObject GetAddressableEntries(JObject parameters)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return Error("Addressable Asset Settings not found. Ensure Addressables is initialized.");

            var groupName = parameters["group"]?.Value<string>();
            if (string.IsNullOrEmpty(groupName))
                return Error("Parameter 'group' is required.");

            var group = settings.groups.FirstOrDefault(g => g != null && g.Name == groupName);
            if (group == null)
                return Error($"Group '{groupName}' not found.");

            var labelFilter = parameters["label"]?.Value<string>();
            var pageSize = Math.Clamp(parameters["page_size"]?.Value<int>() ?? DefaultPageSize, 1, MaxPageSize);
            var page = Math.Max(parameters["page"]?.Value<int>() ?? 1, 1);

            IEnumerable<AddressableAssetEntry> entries = group.entries;
            if (!string.IsNullOrEmpty(labelFilter))
                entries = entries.Where(e => e.labels.Contains(labelFilter));

            var allEntries = entries.ToList();
            int totalCount = allEntries.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / pageSize));
            int skip = (page - 1) * pageSize;

            var itemsArray = new JArray();
            foreach (var entry in allEntries.Skip(skip).Take(pageSize))
            {
                var labelsArray = new JArray();
                foreach (var lbl in entry.labels)
                    labelsArray.Add(lbl);

                itemsArray.Add(new JObject
                {
                    ["guid"] = entry.guid,
                    ["address"] = entry.address,
                    ["asset_path"] = entry.AssetPath,
                    ["labels"] = labelsArray,
                    ["read_only"] = entry.ReadOnly
                });
            }

            return new JObject
            {
                ["group"] = groupName,
                ["entries"] = itemsArray,
                ["page"] = page,
                ["page_size"] = pageSize,
                ["total_count"] = totalCount,
                ["total_pages"] = totalPages
            };
        }

        #endregion

        #region get_addressable_labels

        [McpTool("get_addressable_labels",
            "Returns all defined Addressable labels.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject GetAddressableLabels(JObject parameters)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return Error("Addressable Asset Settings not found. Ensure Addressables is initialized.");

            var labels = settings.GetLabels();
            var labelsArray = new JArray();
            foreach (var label in labels)
                labelsArray.Add(label);

            return new JObject
            {
                ["labels"] = labelsArray,
                ["total_count"] = labelsArray.Count
            };
        }

        #endregion

        #region set_addressable_address

        [McpTool("set_addressable_address",
            "Change the address of an Addressable entry by its asset GUID.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""guid"": { ""type"": ""string"", ""description"": ""Asset GUID of the entry"" },
                    ""address"": { ""type"": ""string"", ""description"": ""New address to set"" }
                },
                ""required"": [""guid"", ""address""]
            }")]
        public static JObject SetAddressableAddress(JObject parameters)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return Error("Addressable Asset Settings not found. Ensure Addressables is initialized.");

            var guid = parameters["guid"]?.Value<string>();
            if (string.IsNullOrEmpty(guid))
                return Error("Parameter 'guid' is required.");

            var address = parameters["address"]?.Value<string>();
            if (address == null)
                return Error("Parameter 'address' is required.");

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                return Error($"No Addressable entry found with GUID '{guid}'.");

            if (entry.ReadOnly)
                return Error($"Entry '{entry.address}' is read-only and cannot be modified.");

            var previousAddress = entry.address;
            entry.address = address;
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true);

            return new JObject
            {
                ["guid"] = guid,
                ["previous_address"] = previousAddress,
                ["new_address"] = address
            };
        }

        #endregion

        #region set_addressable_labels

        [McpTool("set_addressable_labels",
            "Add or remove labels from an Addressable entry.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""guid"": { ""type"": ""string"", ""description"": ""Asset GUID of the entry"" },
                    ""labels"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Labels to add or remove"" },
                    ""enable"": { ""type"": ""boolean"", ""description"": ""True to add labels, false to remove them"", ""default"": true }
                },
                ""required"": [""guid"", ""labels""]
            }")]
        public static JObject SetAddressableLabels(JObject parameters)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return Error("Addressable Asset Settings not found. Ensure Addressables is initialized.");

            var guid = parameters["guid"]?.Value<string>();
            if (string.IsNullOrEmpty(guid))
                return Error("Parameter 'guid' is required.");

            var labels = parameters["labels"]?.ToObject<string[]>();
            if (labels == null || labels.Length == 0)
                return Error("Parameter 'labels' is required and must be a non-empty array.");

            var enable = parameters["enable"]?.Value<bool>() ?? true;

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                return Error($"No Addressable entry found with GUID '{guid}'.");

            if (entry.ReadOnly)
                return Error($"Entry '{entry.address}' is read-only and cannot be modified.");

            foreach (var label in labels)
            {
                if (string.IsNullOrEmpty(label)) continue;

                // Ensure the label exists in settings before assigning
                if (!settings.GetLabels().Contains(label))
                    settings.AddLabel(label);

                entry.SetLabel(label, enable);
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true);

            var currentLabels = new JArray();
            foreach (var lbl in entry.labels)
                currentLabels.Add(lbl);

            return new JObject
            {
                ["guid"] = guid,
                ["address"] = entry.address,
                ["labels"] = currentLabels,
                ["action"] = enable ? "added" : "removed"
            };
        }

        #endregion

        #region move_to_group

        [McpTool("move_to_group",
            "Move an Addressable entry to a different group.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""guid"": { ""type"": ""string"", ""description"": ""Asset GUID of the entry to move"" },
                    ""target_group"": { ""type"": ""string"", ""description"": ""Name of the destination group"" }
                },
                ""required"": [""guid"", ""target_group""]
            }")]
        public static JObject MoveToGroup(JObject parameters)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return Error("Addressable Asset Settings not found. Ensure Addressables is initialized.");

            var guid = parameters["guid"]?.Value<string>();
            if (string.IsNullOrEmpty(guid))
                return Error("Parameter 'guid' is required.");

            var targetGroupName = parameters["target_group"]?.Value<string>();
            if (string.IsNullOrEmpty(targetGroupName))
                return Error("Parameter 'target_group' is required.");

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                return Error($"No Addressable entry found with GUID '{guid}'.");

            var targetGroup = settings.groups.FirstOrDefault(g => g != null && g.Name == targetGroupName);
            if (targetGroup == null)
                return Error($"Target group '{targetGroupName}' not found.");

            var previousGroup = entry.parentGroup?.Name ?? "(none)";

            settings.CreateOrMoveEntry(guid, targetGroup, readOnly: false, postEvent: true);

            return new JObject
            {
                ["guid"] = guid,
                ["address"] = entry.address,
                ["previous_group"] = previousGroup,
                ["new_group"] = targetGroupName
            };
        }

        #endregion

        #region trigger_addressables_build

        [McpTool("trigger_addressables_build",
            "Triggers an Addressables content build asynchronously. Returns a job_id that can be polled via get_job_status.",
            InputSchemaJson = @"{
                ""type"": ""object"",
                ""properties"": {}
            }")]
        public static JObject TriggerAddressablesBuild(JObject parameters)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return Error("Addressable Asset Settings not found. Ensure Addressables is initialized.");

            var jobId = JobManager.CreateJob("Addressables Build", ctx =>
            {
                try
                {
                    ctx.ReportProgress(0.1f);

                    AddressableAssetSettings.BuildPlayerContent(out var result);

                    if (result == null)
                    {
                        ctx.Fail("BuildPlayerContent returned null result.");
                        return;
                    }

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        var errors = new JArray { result.Error };
                        ctx.CompleteWithErrors(new JObject
                        {
                            ["duration_seconds"] = result.Duration,
                            ["location_count"] = result.LocationCount
                        }, errors);
                        return;
                    }

                    ctx.Complete(new JObject
                    {
                        ["duration_seconds"] = result.Duration,
                        ["location_count"] = result.LocationCount,
                        ["output_path"] = result.OutputPath ?? ""
                    });
                }
                catch (Exception ex)
                {
                    ctx.Fail($"Addressables build failed: {ex.Message}");
                }
            });

            return new JObject
            {
                ["job_id"] = jobId,
                ["status"] = "running",
                ["message"] = "Addressables build started. Use get_job_status to poll progress."
            };
        }

        #endregion

        private static JObject Error(string message) =>
            McpError.Create(McpErrorCodes.InternalError, message);
    }
}
