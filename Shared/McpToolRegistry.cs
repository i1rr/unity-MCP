using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>
    /// Static registry that discovers, caches, and dispatches MCP tools marked with <see cref="McpToolAttribute"/>.
    /// Performs lazy assembly scanning on first access. Thread-safe via lock.
    /// </summary>
    public static class McpToolRegistry
    {
        private sealed class ToolEntry
        {
            public string Name;
            public string Description;
            public bool RequiresPlayMode;
            public bool DeferredResponse;
            public JObject InputSchema;
            public MethodInfo Method;
            public bool AutoRoute;
            public string AutoRouteCanonicalName;
            public string Category;
            public string Version;
            public string DeprecatedBy;
            public string ModuleName;
            public string ModuleAssembly;
        }

        /// <summary>
        /// Groups an editor and runtime tool under a single canonical name for auto-routing.
        /// </summary>
        private sealed class AutoRouteGroup
        {
            public string CanonicalName;
            public ToolEntry EditorTool;
            public ToolEntry RuntimeTool;
        }

        private static Dictionary<string, ToolEntry> _tools;
        private static Dictionary<string, AutoRouteGroup> _autoRouteGroups;
        private static readonly object _lock = new object();
        private static List<string> _scannedAssemblies;
        private static List<IMcpModule> _modules;
        private static float _initTimeSeconds;
        private static Dictionary<string, bool> _moduleAvailability = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _failedModuleAssemblies = new(StringComparer.Ordinal);
        private static int _catalogRevision;
        private static int _availabilityChecks;
        public static int CatalogRevision => Volatile.Read(ref _catalogRevision);
        public static int ModuleAvailabilityChecks => Volatile.Read(ref _availabilityChecks);
        public static Action RefreshModulePolicy { get; set; }
        public static Func<string, bool> ModuleEnabled { get; set; }
        public static void NotifyCatalogChanged() => Interlocked.Increment(ref _catalogRevision);

        /// <summary>
        /// When true, each tool invocation is logged to the Unity console with timing details.
        /// Can be toggled at runtime via McpPreferencesProvider or code.
        /// </summary>
        public static bool VerboseLogging { get; set; }

        /// <summary>
        /// Delegate that returns whether Unity is currently in Play Mode.
        /// Must be set by the hosting assembly (Editor or Runtime server) before invoking play-mode-gated tools.
        /// </summary>
        public static Func<bool> IsInPlayMode { get; set; }
        public static Action<string> ToolInvoked { get; set; }

        /// <summary>
        /// Returns all registered tools formatted as an MCP tools/list response array.
        /// Triggers lazy discovery on first call.
        /// Auto-routed tools are listed under their canonical name; runtime aliases are marked deprecated.
        /// </summary>
        public static JArray ListTools()
        {
            return ListTools(null);
        }

        /// <summary>
        /// Returns registered tools formatted as an MCP tools/list response array,
        /// optionally filtered by category. Pass null to return all tools.
        /// </summary>
        public static JArray ListTools(string category)
        {
            EnsureInitialized();

            var array = new JArray();
            var emittedAutoRouteCanonicals = new HashSet<string>(StringComparer.Ordinal);

            lock (_lock)
            {
                foreach (var entry in _tools.Values)
                {
                    if (!IsExposed(entry)) continue;
                    // Apply category filter if specified
                    if (category != null && !string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.AutoRoute && !string.IsNullOrEmpty(entry.AutoRouteCanonicalName))
                    {
                        // This tool is part of an auto-route group
                        var canonical = entry.AutoRouteCanonicalName;

                        if (entry.Name != canonical)
                        {
                            // This is the runtime alias — emit it as deprecated
                            var aliasObj = new JObject
                            {
                                ["name"] = entry.Name,
                                ["description"] = "[DEPRECATED — use '" + canonical + "' instead. Auto-routed based on play mode.] " + entry.Description,
                                ["inputSchema"] = entry.InputSchema.DeepClone()
                            };
                            AddToolMetadata(aliasObj, entry);
                            array.Add(aliasObj);
                            continue;
                        }

                        // This is the canonical (editor) entry
                        if (!emittedAutoRouteCanonicals.Add(canonical))
                            continue; // Already emitted

                        var desc = entry.Description;
                        if (_autoRouteGroups.TryGetValue(canonical, out var group) && group.RuntimeTool != null && group.EditorTool != null)
                        {
                            desc += " [Auto-routed: uses runtime data in Play Mode, editor data otherwise.]";
                        }

                        var canonObj = new JObject
                        {
                            ["name"] = canonical,
                            ["description"] = desc,
                            ["inputSchema"] = entry.InputSchema.DeepClone()
                        };
                        AddToolMetadata(canonObj, entry);
                        array.Add(canonObj);
                    }
                    else
                    {
                        var toolObj = new JObject
                        {
                            ["name"] = entry.Name,
                            ["description"] = entry.Description,
                            ["inputSchema"] = entry.InputSchema.DeepClone()
                        };
                        AddToolMetadata(toolObj, entry);
                        array.Add(toolObj);
                    }
                }
            }
            return array;
        }

        /// <summary>
        /// Adds category, version, and deprecation metadata to a tool's JSON representation.
        /// </summary>
        private static void AddToolMetadata(JObject toolObj, ToolEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.ModuleName)) toolObj["module"] = entry.ModuleName;
            if (!string.IsNullOrEmpty(entry.Category))
                toolObj["category"] = entry.Category;

            if (!string.IsNullOrEmpty(entry.Version))
                toolObj["version"] = entry.Version;

            if (!string.IsNullOrEmpty(entry.DeprecatedBy))
            {
                toolObj["deprecated"] = true;
                toolObj["deprecatedBy"] = entry.DeprecatedBy;
                toolObj["deprecationNotice"] = $"This tool is deprecated. Use '{entry.DeprecatedBy}' instead.";
            }
        }

        /// <summary>
        /// Finds and invokes a tool by name. Returns the tool's JObject result,
        /// or a JSON-RPC error object on failure.
        /// Auto-routed tools are resolved to editor or runtime implementation based on play mode state.
        /// </summary>
        public static JObject Invoke(string toolName, JObject parameters)
        {
            EnsureInitialized();

            ToolEntry entry;
            lock (_lock)
            {
                entry = ResolveToolEntry(toolName);
                if (entry == null)
                {
                    LogToolResult(toolName, 0, false, 0, "Tool not found");
                    return MakeError(-32601, $"Tool not found: {toolName}");
                }
            }

            ToolInvoked?.Invoke(toolName);
            if (entry.RequiresPlayMode)
            {
                if (IsInPlayMode == null)
                {
                    LogToolResult(toolName, 0, false, 0, "No play mode checker configured");
                    return MakeError(-32000, $"Tool '{toolName}' requires play mode but no play mode checker is configured.");
                }
                if (!IsInPlayMode())
                {
                    LogToolResult(toolName, 0, false, 0, "Not in Play Mode");
                    return MakeError(-32000, $"Tool '{toolName}' requires Unity to be in Play Mode.");
                }
            }

            // Deferred tools must be called individually, not through batch Invoke.
            // InvokeRaw handles them correctly; this path would silently discard the DeferredResult.
            if (entry.DeferredResponse)
            {
                LogToolResult(toolName, 0, false, 0, "Deferred tool called in batch");
                return MakeError(-32000, $"Tool '{toolName}' uses deferred responses and cannot be called in a JSON-RPC batch. Call it individually.");
            }

            var sw = Stopwatch.StartNew();
            long gcBefore = ToolExecutionProfiler.Enabled ? GC.GetTotalMemory(false) : 0;
            try
            {
                var result = entry.Method.Invoke(null, new object[] { parameters ?? new JObject() });
                var jResult = result as JObject ?? new JObject { ["result"] = "ok" };
                sw.Stop();
                long gcAfter = ToolExecutionProfiler.Enabled ? GC.GetTotalMemory(false) : 0;
                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                AppendDeprecationWarning(jResult, entry);
                LogReturnedResult(toolName, elapsedMs, jResult);
                ToolExecutionProfiler.RecordExecution(toolName, elapsedMs, Math.Max(0, gcAfter - gcBefore));
                WarnIfSlow(toolName, elapsedMs);
                return ResponseSizeLimiter.Apply(jResult);
            }
            catch (TargetInvocationException ex)
            {
                sw.Stop();
                long gcAfter = ToolExecutionProfiler.Enabled ? GC.GetTotalMemory(false) : 0;
                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                var inner = ex.InnerException ?? ex;
                Debug.LogError($"[McpToolRegistry] Tool '{toolName}' threw: {inner}");
                LogToolResult(toolName, elapsedMs, false, 0, inner.Message);
                ToolExecutionProfiler.RecordExecution(toolName, elapsedMs, Math.Max(0, gcAfter - gcBefore));
                WarnIfSlow(toolName, elapsedMs);
                return MakeError(-32000, inner.Message);
            }
            catch (Exception ex)
            {
                sw.Stop();
                long gcAfter = ToolExecutionProfiler.Enabled ? GC.GetTotalMemory(false) : 0;
                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                Debug.LogError($"[McpToolRegistry] Failed to invoke tool '{toolName}': {ex}");
                LogToolResult(toolName, elapsedMs, false, 0, ex.Message);
                ToolExecutionProfiler.RecordExecution(toolName, elapsedMs, Math.Max(0, gcAfter - gcBefore));
                WarnIfSlow(toolName, elapsedMs);
                return MakeError(-32000, ex.Message);
            }
        }

        /// <summary>
        /// Returns whether the specified tool requires Play Mode to execute.
        /// Returns false if the tool is not found.
        /// </summary>
        /// <summary>
        /// Returns whether the specified tool requires Play Mode to execute.
        /// For auto-routed tools, returns the requirement of the resolved implementation.
        /// Returns false if the tool is not found.
        /// </summary>
        public static bool IsPlayModeRequired(string toolName)
        {
            EnsureInitialized();

            lock (_lock)
            {
                var entry = ResolveToolEntry(toolName);
                return entry != null && entry.RequiresPlayMode;
            }
        }

        /// <summary>
        /// Returns whether the specified tool uses deferred response.
        /// Returns false if the tool is not found.
        /// </summary>
        public static bool IsDeferredTool(string toolName)
        {
            EnsureInitialized();

            lock (_lock)
            {
                var entry = ResolveToolEntry(toolName);
                return entry != null && entry.DeferredResponse;
            }
        }

        /// <summary>
        /// Invokes a tool that may return either a JObject (immediate) or a DeferredResult.
        /// Callers must check the return type to determine if the response should be deferred.
        /// Auto-routed tools are resolved to editor or runtime implementation based on play mode state.
        /// </summary>
        public static object InvokeRaw(string toolName, JObject parameters)
        {
            EnsureInitialized();

            ToolEntry entry;
            lock (_lock)
            {
                entry = ResolveToolEntry(toolName);
                if (entry == null)
                {
                    LogToolResult(toolName, 0, false, 0, "Tool not found");
                    return MakeError(-32601, $"Tool not found: {toolName}");
                }
            }

            ToolInvoked?.Invoke(toolName);
            if (entry.RequiresPlayMode)
            {
                if (IsInPlayMode == null)
                {
                    LogToolResult(toolName, 0, false, 0, "No play mode checker configured");
                    return MakeError(-32000, $"Tool '{toolName}' requires play mode but no play mode checker is configured.");
                }
                if (!IsInPlayMode())
                {
                    LogToolResult(toolName, 0, false, 0, "Not in Play Mode");
                    return MakeError(-32000, $"Tool '{toolName}' requires Unity to be in Play Mode.");
                }
            }

            var sw = Stopwatch.StartNew();
            long gcBefore = ToolExecutionProfiler.Enabled ? GC.GetTotalMemory(false) : 0;
            try
            {
                var result = entry.Method.Invoke(null, new object[] { parameters ?? new JObject() });
                sw.Stop();
                long gcAfter = ToolExecutionProfiler.Enabled ? GC.GetTotalMemory(false) : 0;
                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                if (result is DeferredResult deferred)
                {
                    StructuredLogger.LogToolInvocation(null, toolName, elapsedMs, true, status: "pending");
                    ToolExecutionProfiler.RecordExecution(toolName, elapsedMs, Math.Max(0, gcAfter - gcBefore));
                    return deferred;
                }
                var jResult = result as JObject ?? new JObject { ["result"] = "ok" };
                AppendDeprecationWarning(jResult, entry);
                LogReturnedResult(toolName, elapsedMs, jResult);
                ToolExecutionProfiler.RecordExecution(toolName, elapsedMs, Math.Max(0, gcAfter - gcBefore));
                WarnIfSlow(toolName, elapsedMs);
                return jResult;
            }
            catch (TargetInvocationException ex)
            {
                sw.Stop();
                long gcAfter = ToolExecutionProfiler.Enabled ? GC.GetTotalMemory(false) : 0;
                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                var inner = ex.InnerException ?? ex;
                Debug.LogError($"[McpToolRegistry] Tool '{toolName}' threw: {inner}");
                LogToolResult(toolName, elapsedMs, false, 0, inner.Message);
                ToolExecutionProfiler.RecordExecution(toolName, elapsedMs, Math.Max(0, gcAfter - gcBefore));
                WarnIfSlow(toolName, elapsedMs);
                return MakeError(-32000, inner.Message);
            }
            catch (Exception ex)
            {
                sw.Stop();
                long gcAfter = ToolExecutionProfiler.Enabled ? GC.GetTotalMemory(false) : 0;
                double elapsedMs = sw.Elapsed.TotalMilliseconds;
                Debug.LogError($"[McpToolRegistry] Failed to invoke tool '{toolName}': {ex}");
                LogToolResult(toolName, elapsedMs, false, 0, ex.Message);
                ToolExecutionProfiler.RecordExecution(toolName, elapsedMs, Math.Max(0, gcAfter - gcBefore));
                WarnIfSlow(toolName, elapsedMs);
                return MakeError(-32000, ex.Message);
            }
        }

        /// <summary>
        /// Clears the tool cache. The next call to ListTools() or Invoke() will trigger a full re-scan.
        /// </summary>
        public static void InvalidateCache()
        {
            lock (_lock)
            {
                if (_modules != null)
                {
                    foreach (var module in _modules)
                    {
                        try { module.Shutdown(); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[McpToolRegistry] Module '{module.ModuleName}' shutdown error: {ex.Message}");
                        }
                    }
                }
                _tools = null;
                _autoRouteGroups = null;
                _scannedAssemblies = null;
                _modules = null;
                _moduleAvailability.Clear();
                _failedModuleAssemblies.Clear();
                NotifyCatalogChanged();
            }
        }

        /// <summary>
        /// Returns all discovered modules. Triggers lazy discovery on first call.
        /// </summary>
        public static IReadOnlyList<IMcpModule> Modules
        {
            get
            {
                EnsureInitialized();
                lock (_lock) { return _modules?.AsReadOnly() ?? (IReadOnlyList<IMcpModule>)Array.Empty<IMcpModule>(); }
            }
        }

        /// <summary>
        /// Resolves a tool name to a ToolEntry, applying auto-route logic when applicable.
        /// Must be called while holding _lock.
        /// </summary>
        private static ToolEntry ResolveToolEntry(string toolName)
        {
            if (!_tools.TryGetValue(toolName, out var entry))
                return null;

            // If this tool is auto-routed, resolve to the right implementation
            if (entry.AutoRoute && !string.IsNullOrEmpty(entry.AutoRouteCanonicalName)
                && _autoRouteGroups != null
                && _autoRouteGroups.TryGetValue(entry.AutoRouteCanonicalName, out var group))
            {
                // If this is the canonical name, route based on play mode
                if (entry.Name == group.CanonicalName)
                {
                    bool inPlayMode = IsInPlayMode?.Invoke() ?? false;
                    if (inPlayMode && group.RuntimeTool != null && IsEntryEnabled(group.RuntimeTool))
                        return group.RuntimeTool;
                    if (group.EditorTool != null && IsEntryEnabled(group.EditorTool))
                        return group.EditorTool;
                    return null;
                }
                // If invoked by the runtime alias directly, use the runtime tool
                // (backward compat — no routing)
            }

            return IsEntryEnabled(entry) ? entry : null;
        }

        private static bool IsEntryEnabled(ToolEntry entry)
        {
            if (string.IsNullOrEmpty(entry.ModuleName)) return string.IsNullOrEmpty(entry.ModuleAssembly);
            if (!_moduleAvailability.TryGetValue(entry.ModuleAssembly, out bool available) || !available) return false;
            try { return ModuleEnabled?.Invoke(entry.ModuleName) ?? true; }
            catch { return false; }
        }

        private static bool IsExposed(ToolEntry entry) => IsEntryEnabled(entry) ||
            (entry.AutoRoute && entry.Name == entry.AutoRouteCanonicalName && _autoRouteGroups != null &&
             _autoRouteGroups.TryGetValue(entry.Name, out var group) && group.RuntimeTool != null && IsEntryEnabled(group.RuntimeTool));

        public static bool IsModuleAvailable(IMcpModule module)
        {
            EnsureInitialized();
            lock (_lock) return _moduleAvailability.TryGetValue(module.GetType().Assembly.GetName().Name, out bool available) && available;
        }

        public static int ModuleToolCount(string name, bool activeOnly = false)
        {
            EnsureInitialized();
            lock (_lock) return _tools.Values.Count(t => string.Equals(t.ModuleName, name, StringComparison.OrdinalIgnoreCase) && (!activeOnly || IsEntryEnabled(t)));
        }

        public static void RecheckModuleAvailability()
        {
            EnsureInitialized();
            lock (_lock) UpdateModuleAvailability();
        }

        private static void UpdateModuleAvailability()
        {
            foreach (var module in _modules)
            {
                Interlocked.Increment(ref _availabilityChecks);
                bool available;
                try { available = module.IsAvailable; }
                catch (Exception ex) { available = false; Debug.LogWarning($"[MCP] Module {module.ModuleName}: {ex.Message}"); }
                string assembly = module.GetType().Assembly.GetName().Name;
                if (!_moduleAvailability.TryGetValue(assembly, out bool previous) || previous != available)
                { _moduleAvailability[assembly] = available; NotifyCatalogChanged(); }
            }
        }

        private static void EnsureInitialized()
        {
            RefreshModulePolicy?.Invoke();
            if (_tools != null) return;

            lock (_lock)
            {
                if (_tools != null) return;
                var sw = Stopwatch.StartNew();
                _modules = DiscoverModules();
                UpdateModuleAvailability();
                _tools = ScanAssemblies(out _scannedAssemblies);
                _autoRouteGroups = BuildAutoRouteGroups(_tools);
                sw.Stop();
                _initTimeSeconds = (float)sw.Elapsed.TotalSeconds;
                NotifyCatalogChanged();
            }
        }

        private static Dictionary<string, ToolEntry> ScanAssemblies(out List<string> scannedAssemblyNames)
        {
            var result = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);
            var sharedAssemblyName = typeof(McpToolRegistry).Assembly.GetName().Name;
            scannedAssemblyNames = new List<string>();

            var assemblies = I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies();
            foreach (var assembly in assemblies)
            {
                if (!ReferencesSharedAssembly(assembly, sharedAssemblyName))
                    continue;

                scannedAssemblyNames.Add(assembly.GetName().Name);

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in types)
                {
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    foreach (var method in methods)
                    {
                        var attr = method.GetCustomAttribute<McpToolAttribute>();
                        if (attr == null) continue;

                        if (!ValidateToolMethod(method, attr.Name, attr.DeferredResponse))
                            continue;

                        var schema = ParseInputSchema(attr.InputSchemaJson);

                        var candidate = CreateToolEntry(attr, method, schema, type);
                        var owner = _modules.FirstOrDefault(m => m.GetType().Assembly == method.DeclaringType?.Assembly);
                        candidate.ModuleName = owner?.ModuleName;
                        string assemblyName = assembly.GetName().Name;
                        candidate.ModuleAssembly = owner != null || _failedModuleAssemblies.Contains(assemblyName) ? assemblyName : null;

                        if (result.TryGetValue(attr.Name, out var existing))
                        {
                            if (IsEditorRuntimeShadowDuplicate(existing.Method, method))
                            {
                                if (ShouldPreferNewShadowTool(existing.Method, method))
                                {
                                    result[attr.Name] = candidate;
                                }
                                continue;
                            }

                            Debug.LogWarning($"[McpToolRegistry] Duplicate tool name '{attr.Name}' in {type.FullName}.{method.Name} — skipped.");
                            continue;
                        }

                        result[attr.Name] = candidate;
                    }
                }
            }

            return result;
        }

        private static ToolEntry CreateToolEntry(McpToolAttribute attr, MethodInfo method, JObject schema, Type declaringType)
        {
            // Derive canonical name for auto-routed tools
            string autoRouteCanonical = null;
            if (attr.AutoRoute)
            {
                if (!string.IsNullOrEmpty(attr.AutoRouteCanonicalName))
                {
                    autoRouteCanonical = attr.AutoRouteCanonicalName;
                }
                else if (attr.Name.StartsWith("runtime_", StringComparison.Ordinal))
                {
                    autoRouteCanonical = attr.Name.Substring("runtime_".Length);
                }
                else
                {
                    autoRouteCanonical = attr.Name;
                }
            }

            return new ToolEntry
            {
                Name = attr.Name,
                Description = attr.Description,
                RequiresPlayMode = attr.RequiresPlayMode,
                DeferredResponse = attr.DeferredResponse,
                InputSchema = schema,
                Method = method,
                AutoRoute = attr.AutoRoute,
                AutoRouteCanonicalName = autoRouteCanonical,
                Category = attr.Category
                    ?? declaringType.GetCustomAttribute<McpToolCategoryAttribute>()?.Category,
                Version = attr.Version ?? "1.0",
                DeprecatedBy = attr.DeprecatedBy
            };
        }

        private static bool IsEditorRuntimeShadowDuplicate(MethodInfo existingMethod, MethodInfo newMethod)
        {
            bool existingIsEditor = IsEditorToolMethod(existingMethod);
            bool existingIsRuntime = IsRuntimeToolMethod(existingMethod);
            bool newIsEditor = IsEditorToolMethod(newMethod);
            bool newIsRuntime = IsRuntimeToolMethod(newMethod);

            return (existingIsEditor && newIsRuntime) || (existingIsRuntime && newIsEditor);
        }

        private static bool ShouldPreferNewShadowTool(MethodInfo existingMethod, MethodInfo newMethod)
        {
            bool preferEditor = Application.isEditor;
            bool newIsEditor = IsEditorToolMethod(newMethod);
            return preferEditor == newIsEditor;
        }

        private static bool IsEditorToolMethod(MethodInfo method)
        {
            var fullName = method.DeclaringType?.FullName ?? string.Empty;
            return fullName.Contains(".Editor.", StringComparison.Ordinal);
        }

        private static bool IsRuntimeToolMethod(MethodInfo method)
        {
            var fullName = method.DeclaringType?.FullName ?? string.Empty;
            return fullName.Contains(".Runtime.", StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds auto-route groups from the scanned tool entries. Groups tools that share
        /// the same canonical name into editor/runtime pairs.
        /// </summary>
        private static Dictionary<string, AutoRouteGroup> BuildAutoRouteGroups(Dictionary<string, ToolEntry> tools)
        {
            var groups = new Dictionary<string, AutoRouteGroup>(StringComparer.Ordinal);

            foreach (var entry in tools.Values)
            {
                if (!entry.AutoRoute || string.IsNullOrEmpty(entry.AutoRouteCanonicalName))
                    continue;

                var canonical = entry.AutoRouteCanonicalName;

                if (!groups.TryGetValue(canonical, out var group))
                {
                    group = new AutoRouteGroup { CanonicalName = canonical };
                    groups[canonical] = group;
                }

                bool isRuntime = entry.Name.StartsWith("runtime_", StringComparison.Ordinal);
                if (isRuntime)
                    group.RuntimeTool = entry;
                else
                    group.EditorTool = entry;
            }

            return groups;
        }

        /// <summary>
        /// Discovers all types implementing IMcpModule in assemblies that reference the shared assembly.
        /// Instantiates each module and calls Initialize().
        /// </summary>
        private static List<IMcpModule> DiscoverModules()
        {
            var modules = new List<IMcpModule>();
            var sharedAssemblyName = typeof(McpToolRegistry).Assembly.GetName().Name;

            foreach (var assembly in I1rr.UnityMcp.Shared.UnityCompatibility.GetLoadedAssemblies())
            {
                if (!ReferencesSharedAssembly(assembly, sharedAssemblyName))
                    continue;

                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) continue;
                    if (!typeof(IMcpModule).IsAssignableFrom(type)) continue;

                    try
                    {
                        var module = (IMcpModule)Activator.CreateInstance(type);
                        module.Initialize();
                        modules.Add(module);
                    }
                    catch (Exception ex)
                    {
                        _failedModuleAssemblies.Add(assembly.GetName().Name);
                        Debug.LogWarning($"[McpToolRegistry] Failed to initialize module from {type.FullName}: {ex.Message}");
                    }
                }
            }

            return modules;
        }

        private static bool ReferencesSharedAssembly(Assembly assembly, string sharedAssemblyName)
        {
            // The shared assembly itself contains tools too
            if (assembly.GetName().Name == sharedAssemblyName)
                return true;

            var refs = assembly.GetReferencedAssemblies();
            for (int i = 0; i < refs.Length; i++)
            {
                if (string.Equals(refs[i].Name, sharedAssemblyName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool ValidateToolMethod(MethodInfo method, string toolName, bool isDeferredResponse)
        {
            if (!method.IsStatic)
            {
                Debug.LogWarning($"[McpToolRegistry] Tool '{toolName}' on {method.DeclaringType?.FullName}.{method.Name} must be static — skipped.");
                return false;
            }

            // Deferred tools may return JObject or object (which could be DeferredResult)
            if (isDeferredResponse)
            {
                if (method.ReturnType != typeof(JObject) && method.ReturnType != typeof(object))
                {
                    Debug.LogWarning($"[McpToolRegistry] Deferred tool '{toolName}' on {method.DeclaringType?.FullName}.{method.Name} must return JObject or object — skipped.");
                    return false;
                }
            }
            else
            {
                if (method.ReturnType != typeof(JObject))
                {
                    Debug.LogWarning($"[McpToolRegistry] Tool '{toolName}' on {method.DeclaringType?.FullName}.{method.Name} must return JObject — skipped.");
                    return false;
                }
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(JObject))
            {
                Debug.LogWarning($"[McpToolRegistry] Tool '{toolName}' on {method.DeclaringType?.FullName}.{method.Name} must accept a single JObject parameter — skipped.");
                return false;
            }

            return true;
        }

        private static JObject ParseInputSchema(string json)
        {
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    return JObject.Parse(json);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[McpToolRegistry] Failed to parse InputSchemaJson: {ex.Message}. Using default schema.");
                }
            }
            return new JObject { ["type"] = "object" };
        }

        private static JObject MakeError(int code, string message)
        {
            return new JObject
            {
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
        }

        /// <summary>
        /// Appends a deprecation warning to a tool's result if the tool is deprecated.
        /// </summary>
        private static void AppendDeprecationWarning(JObject result, ToolEntry entry)
        {
            if (string.IsNullOrEmpty(entry.DeprecatedBy))
                return;

            result["_warning"] = $"Tool '{entry.Name}' is deprecated. Use '{entry.DeprecatedBy}' instead.";
        }

        /// <summary>
        /// Returns the number of registered tools. Triggers lazy discovery on first call.
        /// </summary>
        public static int ToolCount
        {
            get
            {
                EnsureInitialized();
                lock (_lock) { return _tools?.Values.Count(IsExposed) ?? 0; }
            }
        }

        /// <summary>
        /// Returns the list of assembly names that were scanned during tool discovery.
        /// Only includes assemblies that reference I1rr.UnityMcp.Shared.
        /// </summary>
        public static IReadOnlyList<string> ScannedAssemblies
        {
            get
            {
                EnsureInitialized();
                lock (_lock) { return _scannedAssemblies?.AsReadOnly() ?? (IReadOnlyList<string>)Array.Empty<string>(); }
            }
        }

        /// <summary>
        /// Returns the time in seconds the initial tool scan took.
        /// </summary>
        public static float InitTimeSeconds
        {
            get
            {
                EnsureInitialized();
                return _initTimeSeconds;
            }
        }

        private static void LogReturnedResult(string toolName, double elapsedMs, JObject result)
        {
            bool success = !McpProtocol.IsToolFailure(result);
            LogToolResult(toolName, elapsedMs, success, System.Text.Encoding.UTF8.GetByteCount(result.ToString(Newtonsoft.Json.Formatting.None)),
                success ? null : (string)result["error_message"]);
        }

        private static void LogToolResult(string toolName, double elapsedMs, bool success, int responseBytes, string errorDetail)
        {
            // Always write to structured log (respects its own level filter)
            StructuredLogger.LogToolInvocation(null, toolName, elapsedMs, success,
                success ? null : "tool_error", errorDetail, resultBytes: responseBytes);

            if (!VerboseLogging) return;

            if (success)
            {
                var sizeStr = responseBytes >= 1024
                    ? $"{responseBytes / 1024.0:F1}KB"
                    : $"{responseBytes}B";
                Debug.Log($"[MCP] Tool '{toolName}' completed in {elapsedMs:F1}ms (success, {sizeStr})");
            }
            else
            {
                Debug.Log($"[MCP] Tool '{toolName}' failed in {elapsedMs:F1}ms: {errorDetail}");
            }
        }

        private static void WarnIfSlow(string toolName, double elapsedMs)
        {
            if (ToolExecutionProfiler.Enabled && elapsedMs >= ToolExecutionProfiler.SlowToolThresholdMs)
            {
                Debug.LogWarning($"[MCP] Slow tool: '{toolName}' took {elapsedMs:F1}ms (threshold: {ToolExecutionProfiler.SlowToolThresholdMs}ms)");
            }
        }

    }
}
