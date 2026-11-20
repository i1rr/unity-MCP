using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using I1rr.UnityMcp.Shared;
using UnityEditor;
using UnityEngine;

namespace I1rr.UnityMcp.Editor
{
    /// <summary>
    /// Dockable MCP Dashboard window — provides live server status, request activity,
    /// settings, and a scrollable error/warning log. Replaces the Project Settings panel.
    ///
    /// Everything displayed is sampled into a snapshot on a fixed timer (<see cref="RefreshIntervalSec"/>);
    /// <see cref="OnGUI"/> only renders that snapshot. IMGUI invokes OnGUI many times per second
    /// (Layout + Repaint, plus one pass per input event while the cursor is over the window), so
    /// querying the tool registry, module availability or request history from OnGUI directly
    /// costs those queries several times a frame.
    /// </summary>
    public sealed class McpDashboardWindow : EditorWindow
    {
        private const string TokenMask = "••••••••-••••-••••-••••-••••••••••••";
        private const int MaxLogEntries = 200;
        private const double RefreshIntervalSec = 1.0;

        // --- Settings ---
        /// <summary>
        /// Whether the execute_code tool is enabled. Backed by
        /// <see cref="McpPreferencesProvider.EnableCodeExecution"/> so both entry points
        /// share a single source of truth.
        /// </summary>
        public static bool EnableCodeExecution
        {
            get => McpPreferencesProvider.EnableCodeExecution;
            set => McpPreferencesProvider.EnableCodeExecution = value;
        }

        // --- Log buffer ---
        /// <summary>
        /// A captured console line. The display text is rendered once at capture time so the
        /// GUI never formats strings while drawing.
        /// </summary>
        private readonly struct LogEntry
        {
            public readonly string Text;
            public readonly LogType Type;

            public LogEntry(string text, LogType type)
            {
                Text = text;
                Type = type;
            }
        }

        private static readonly object _logGate = new object();
        private static readonly Queue<LogEntry> _logEntries = new();
        private static bool _logDirty;
        private static int _logSubscriberCount;

        // --- Snapshot (refreshed on the timer, read by OnGUI) ---


        private bool _serverRunning;
        private int _serverPort;
        private long _totalRequests;
        private double _avgResponseMs;
        private string _uptimeText = "";
        private int _toolCount;
        private bool _codeExecutionEnabled;
        private bool _requireAuthentication;
        private bool _editorBusy;
        private string _transport, _endpoint;
        [SerializeField] private int _selectedTab;
        private static readonly string[] Tabs = { "Overview", "Tools", "Settings", "Activity" };
        private JArray _modernActivity = new JArray();
        private int _modernStreamCount;
        private sealed class ToolView
        {
            internal string Name, Description, Calls, Category;
            internal long Count;
        }
        private readonly SortedDictionary<string, List<ToolView>> _toolGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _expandedToolGroups = new(StringComparer.Ordinal);
        private string _toolSearch = "";
        private string _usageSince, _usageError;
        private readonly List<ToolView> _byUsage = new();
        private enum ToolOrder { MostUsed, LeastUsed, Category }
        private enum UsageFilter { All, Used, Unused }
        [SerializeField] private ToolOrder _toolOrder;
        [SerializeField] private UsageFilter _usageFilter;
        private bool _focusedCatalog;
        private readonly List<LogEntry> _logRenderBuffer = new();

        // Scratch buffers reused by the refresh so sampling allocates nothing steady-state.
        private readonly Dictionary<string, int> _displayNameCounts =
            new(StringComparer.OrdinalIgnoreCase);

        // --- UI state ---
        private string _cachedToken;
        private bool _showToken;
        private Vector2 _logScroll;
        private Vector2 _mainScroll;
        private Vector2 _issueScroll;
        [SerializeField] private bool _activityExpanded = true;
        [SerializeField] private bool _issuesExpanded = true;
        private McpBacklogReader _issueReader;
        private McpBacklogReader.Snapshot _issueSnapshot = new();
        private readonly McpBacklogSetup.StateCache _backlogStateCache = new();
        private long _lastUsageRevision = -1;
        private int _lastCatalogCount = -1, _toolCatalogBuilds, _snapshotSamples;
        private int _lastCatalogRevision = -1;
        private bool _lastFocused;
        private ToolOrder _lastToolOrder;
        private double _lastModuleSample = double.NegativeInfinity;
        private int _lastModuleRevision = -1;
        private JObject _moduleSnapshot = new() { ["modules"] = new JArray() };
        private string[] _projectModuleIds = Array.Empty<string>();
        [SerializeField] private int _selectedModuleIndex;
        private string _pendingModuleToggle, _pendingModuleAction;
        private bool _pendingModuleEnabled;
        private double _lastRefreshTime;
        private bool _autoScrollLog = true;
        private bool _scrollLogToEnd;

        // Actions raised by controls are applied after the GUI pass finishes: mutating the
        // snapshot mid-pass would change the control count between Layout and Repaint.
        private bool _refreshRequested;
        private bool _clearLogRequested;
        private string _pendingCancelOperationId;
        private enum BacklogAction { None, Enable, Disable, Repair }
        private BacklogAction _pendingBacklogAction;
        private McpBacklogSetup.State _backlogState;
        private string _backlogProjectRoot, _backlogError;
        private bool _backlogLogExists;
        private int _pendingBacklogLine;
        private string _pendingIssueLink;
        private bool _pendingBacklogHelp;
        private static readonly GUIContent BacklogHelp = new("?",
            "Create a local backlog that your AI assistant updates.\n\n" +
            "View it in Activity or i1rr-MCP-backlog.md. Ask your assistant to use it to improve " +
            "your workflow, report an issue, or build a fix on your own branch.");

        // --- Styles (lazy; EditorStyles is only valid inside OnGUI) ---
        private GUIStyle _errorStyle;
        private GUIStyle _warningStyle;
        private GUIStyle _infoStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _tintedLabel;
        private GUIStyle _tintedMiniLabel;

        [MenuItem("Tools/i1rr-unity-mcp Dashboard")]
        public static void ShowWindow()
        {
            var window = GetWindow<McpDashboardWindow>();
            window.titleContent = new GUIContent("MCP Dashboard", EditorGUIUtility.IconContent("d_Profiler.NetworkMessages").image);
            window.minSize = new Vector2(420, 400);
            window.Show();
        }

        private void OnEnable()
        {
            var expanded = SessionState.GetString("MCP.Dashboard.ExpandedTools", "");
            foreach (var category in expanded.Split('\n'))
                if (!string.IsNullOrEmpty(category)) _expandedToolGroups.Add(category);


            SubscribeLog();
            EditorApplication.update += OnEditorUpdate;
            RefreshSnapshot();
        }

        private void OnDisable()
        {
            SessionState.SetString("MCP.Dashboard.ExpandedTools", string.Join("\n", _expandedToolGroups));
            EditorApplication.update -= OnEditorUpdate;
            UnsubscribeLog();
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastRefreshTime < RefreshIntervalSec) return;

            _lastRefreshTime = EditorApplication.timeSinceStartup;
            RefreshSnapshot();
            Repaint();
        }

        // ===================== SNAPSHOT =====================
        /// <summary>
        /// Samples every value the window displays. This is the only place that touches the
        /// server, the tool registry, module availability or request history.
        /// </summary>
        internal void RefreshSnapshot()
        {
            _snapshotSamples++;
            _serverRunning = McpEditorServer.IsRunning;
            _serverPort = McpEditorServer.Port;
            _editorBusy = EditorApplication.isCompiling || EditorApplication.isUpdating;
            _transport = McpEditorServer._useNativeTransport ? "Native socket keeper" : "HTTP listener fallback";
            _endpoint = _serverRunning ? $"http://127.0.0.1:{_serverPort}/mcp" : "Server stopped";

            if (_serverRunning)
            {
                var metrics = ServerMetrics.GetMetrics();
                _totalRequests = (long)(metrics["total_requests"] ?? 0);
                _avgResponseMs = (double)(metrics["avg_response_time_ms"] ?? 0);
                _uptimeText = FormatUptime((double)(metrics["uptime_seconds"] ?? 0));
            }

            _toolCount = McpToolRegistry.ToolCount;
            _codeExecutionEnabled = EnableCodeExecution;
            _requireAuthentication = McpPreferencesProvider.RequireAuthentication;
            _cachedToken = McpEditorServer._authToken;
            _backlogProjectRoot = Path.GetDirectoryName(Application.dataPath);
            _backlogState = _backlogStateCache.Read(_backlogProjectRoot);
            _backlogLogExists = File.Exists(Path.Combine(_backlogProjectRoot, McpBacklogSetup.LogName));
            _issueReader ??= new McpBacklogReader();
            _issueSnapshot = _issueReader.Read(_backlogProjectRoot);

            _modernActivity = McpRequestActivity.Snapshot();
            _modernStreamCount = EditorStreamingResponses.Pool.Count;
            if (_selectedTab == 0 && (EditorApplication.timeSinceStartup - _lastModuleSample >= 5 || _lastModuleRevision != McpToolRegistry.CatalogRevision))
            {
                _lastModuleSample = EditorApplication.timeSinceStartup;
                RefreshModules();
            }
            if (_selectedTab == 1) RefreshTools();
            RefreshLog();
        }

        private void RefreshTools()
        {
            _usageError = EditorToolUsage.Store.PersistenceError;
            long revision = EditorToolUsage.Store.Revision;
            bool focused = ToolCatalog.Focused;
            if (_lastUsageRevision == revision && _lastCatalogCount == _toolCount && _lastFocused == focused && _lastCatalogRevision == McpToolRegistry.CatalogRevision)
            {
                if (_lastToolOrder != _toolOrder) SortUsage();
                return;
            }
            _lastUsageRevision = revision; _lastCatalogCount = _toolCount; _lastFocused = focused;
            _lastCatalogRevision = McpToolRegistry.CatalogRevision;
            _toolCatalogBuilds++;
            var usage = EditorToolUsage.Store.Snapshot();
            _usageSince = EditorToolUsage.Store.StartedAtUtc;
            _usageError = EditorToolUsage.Store.PersistenceError;
            _toolGroups.Clear();
            _byUsage.Clear();
            _focusedCatalog = ToolCatalog.Focused;
            foreach (JObject tool in McpToolRegistry.ListTools())
            {
                var category = (string)tool["category"] ?? "Other";
                if (!_toolGroups.TryGetValue(category, out var group))
                    _toolGroups[category] = group = new List<ToolView>();
                var name = (string)tool["name"];
                usage.TryGetValue(name, out var count);
                var view = new ToolView { Name = name, Description = (string)tool["description"], Category = category, Count = count, Calls = count.ToString("N0") };
                group.Add(view);
                _byUsage.Add(view);
                usage.Remove(name);
            }
            if (usage.Count > 0)
            {
                const string retired = "Previously available";
                var group = new List<ToolView>();
                _toolGroups[retired] = group;
                foreach (var item in usage)
                {
                    var view = new ToolView { Name = item.Key, Category = retired, Count = item.Value, Calls = item.Value.ToString("N0") };
                    group.Add(view);
                    _byUsage.Add(view);
                }
            }
            SortUsage();
            foreach (var group in _toolGroups.Values)
                group.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        }

        internal void SelectTab(string tab)
        {
            int index = Array.FindIndex(Tabs, t => string.Equals(t, tab, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ArgumentException("Unknown Dashboard tab.");
            _selectedTab = index;
            _mainScroll = Vector2.zero;
            RefreshSnapshot();
            Repaint();
        }

        internal JObject InspectSnapshot() => new JObject
        {
            ["open"] = true, ["tab"] = Tabs[_selectedTab], ["backlog_enabled"] = _backlogState.Enabled,
            ["backlog_needs_repair"] = _backlogState.NeedsRepair, ["issue_count"] = _issueSnapshot.Total,
            ["activity_expanded"] = _activityExpanded, ["issues_expanded"] = _issuesExpanded,
            ["snapshot_samples"] = _snapshotSamples, ["tool_catalog_builds"] = _toolCatalogBuilds,
            ["backlog_state_reads"] = _backlogStateCache.Inspections, ["tool_count"] = _toolCount,
            ["modules"] = _moduleSnapshot["modules"]?.DeepClone(),
            ["module_availability_checks"] = McpToolRegistry.ModuleAvailabilityChecks,
            ["modern_activity"] = _modernActivity.DeepClone(),
            ["modern_activity_capacity"] = McpRequestActivity.Capacity,
            ["active_request_streams"] = _modernStreamCount
        };


        private void RefreshModules()
        {
            _moduleSnapshot = McpModuleManager.Snapshot();
            _lastModuleRevision = McpToolRegistry.CatalogRevision;
            var rows = ((JArray)_moduleSnapshot["modules"]).Children<JObject>().ToArray();
            _projectModuleIds = rows.Where(r => (bool)r["project_module"]).Select(r => (string)r["name"]).ToArray();
            _selectedModuleIndex = _projectModuleIds.Length == 0 ? 0 : Mathf.Clamp(_selectedModuleIndex, 0, _projectModuleIds.Length - 1);
        }

        private void RefreshLog()
        {
            lock (_logGate)
            {
                _logRenderBuffer.Clear();
                _logRenderBuffer.AddRange(_logEntries);

                if (_logDirty)
                {
                    _logDirty = false;
                    if (_autoScrollLog) _scrollLogToEnd = true;
                }
            }
        }

        // ===================== LOG CAPTURE =====================
        private static void SubscribeLog()
        {
            // Reference-counted so a duplicate window in a saved layout cannot double-subscribe
            // the static handler (which would capture every message twice).
            if (_logSubscriberCount++ == 0)
                Application.logMessageReceived += OnLogMessage;
        }

        private static void UnsubscribeLog()
        {
            if (--_logSubscriberCount > 0) return;

            _logSubscriberCount = 0;
            Application.logMessageReceived -= OnLogMessage;
        }

        private static void OnLogMessage(string condition, string stacktrace, LogType type)
        {
            // Only capture MCP-related log messages
            if (condition == null || condition.IndexOf("[MCP]", StringComparison.Ordinal) < 0) return;

            var text = $"[{DateTime.Now:HH:mm:ss}] {condition}";

            lock (_logGate)
            {
                _logEntries.Enqueue(new LogEntry(text, type));
                while (_logEntries.Count > MaxLogEntries)
                    _logEntries.Dequeue();

                _logDirty = true;
            }
        }

        // ===================== STYLES =====================
        private void InitStyles()
        {
            if (_errorStyle != null) return;

            _errorStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(1f, 0.35f, 0.35f) },
                wordWrap = true,
                richText = false
            };
            _warningStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(1f, 0.75f, 0.2f) },
                wordWrap = true,
                richText = false
            };
            _infoStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = EditorStyles.label.normal.textColor },
                wordWrap = true,
                richText = false
            };
            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12
            };
            _tintedLabel = new GUIStyle(EditorStyles.label);
            _tintedMiniLabel = new GUIStyle(EditorStyles.miniLabel);
        }

        /// <summary>
        /// Returns a shared style tinted for this draw call. IMGUI renders immediately, so
        /// re-tinting one instance is safe and avoids allocating a GUIStyle per label per frame.
        /// </summary>
        private GUIStyle Tinted(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            return style;
        }

        // ===================== GUI =====================
        private void OnGUI()
        {
            InitStyles();
            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Min(220, position.width * 0.52f);
            var nextTab = GUILayout.Toolbar(_selectedTab, Tabs, GUILayout.Height(26));
            if (nextTab != _selectedTab)
            {
                _selectedTab = nextTab;
                _mainScroll = Vector2.zero;
                _refreshRequested = true;
                GUI.FocusControl(null);
            }
            if (_selectedTab == 3) DrawActivity();
            else
            {
                _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);
                switch (_selectedTab)
                {
                    case 0:
                        DrawServerStatus();
                        EditorGUILayout.Space(6);
                        DrawModules();
                        EditorGUILayout.Space(6);
                        EditorGUILayout.Space(6);
                        DrawModernActivity();
                        break;
                    case 1: DrawTools(); break;
                    case 2:
                        DrawBacklog();
                        EditorGUILayout.Space(6);
                        DrawAuthentication();
                        EditorGUILayout.Space(6);
                        DrawSettings();
                        break;
                }

                EditorGUILayout.EndScrollView();
            }
            EditorGUIUtility.labelWidth = previousLabelWidth;

            ApplyPendingActions();
        }

        /// <summary>
        /// Applies control-raised actions once the GUI pass has finished drawing, then resamples.
        /// Unity issues a fresh Layout event before the next Repaint, so the changed row count is
        /// picked up cleanly.
        /// </summary>
        private void ApplyPendingActions()
        {
            if (!string.IsNullOrEmpty(_pendingModuleToggle) || !string.IsNullOrEmpty(_pendingModuleAction))
            {
                string id = _pendingModuleToggle, action = _pendingModuleAction;
                bool enabled = _pendingModuleEnabled;
                string selected = _projectModuleIds.Length == 0 ? null : _projectModuleIds[_selectedModuleIndex];
                _pendingModuleToggle = _pendingModuleAction = null;
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(id)) McpModuleManager.SetEnabled(id, enabled);
                        if (action == "create") McpNewModuleWindow.Open();
                        if (action == "import") McpModuleManager.ImportDialog();
                        if (action == "export" && selected != null) McpModuleManager.ExportDialog(selected);
                    }
                    catch (Exception ex) { Debug.LogWarning("[MCP] Module action: " + ex.Message); }
                    if (this) { _lastModuleSample = double.NegativeInfinity; RefreshSnapshot(); Repaint(); }
                };
            }
            // External editors and native dialogs can exit/reenter IMGUI. Open them after drawing.
            if (_pendingBacklogLine > 0)
            {
                int line = _pendingBacklogLine;
                _pendingBacklogLine = 0;
                EditorApplication.delayCall += () => { if (this) OpenBacklog(line); };
            }
            if (!string.IsNullOrEmpty(_pendingIssueLink))
            {
                string target = _pendingIssueLink;
                _pendingIssueLink = null;
                EditorApplication.delayCall += () => { if (this) OpenIssueLink(target); };
            }
            if (_pendingBacklogHelp)
            {
                _pendingBacklogHelp = false;
                EditorApplication.delayCall += () => EditorUtility.DisplayDialog("About MCP issue logging", BacklogHelp.tooltip, "OK");
            }
            if (_pendingBacklogAction != BacklogAction.None)
            {
                var action = _pendingBacklogAction;
                _pendingBacklogAction = BacklogAction.None;
                try
                {
                    if (action == BacklogAction.Disable) McpBacklogSetup.Disable(_backlogProjectRoot);
                    else McpBacklogSetup.Enable(_backlogProjectRoot, action == BacklogAction.Repair);
                    _backlogError = null;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.DecoderFallbackException)
                { _backlogError = ex.Message; }
                _refreshRequested = true;
            }


            if (_pendingCancelOperationId != null)
            {
                McpRequestActivity.Cancel(_pendingCancelOperationId);
                _pendingCancelOperationId = null;
                _refreshRequested = true;
            }

            if (_clearLogRequested)
            {
                _clearLogRequested = false;
                lock (_logGate)
                {
                    _logEntries.Clear();
                    _logDirty = false;
                }
                _scrollLogToEnd = false;
                _refreshRequested = true;
            }

            if (!_refreshRequested) return;

            _refreshRequested = false;
            RefreshSnapshot();
            Repaint();
        }

        // ===================== SERVER STATUS =====================
        private void DrawServerStatus()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Server Status", _headerStyle);
                EditorGUILayout.Space(2);

                // A cached snapshot alone cannot establish main-thread readiness. This
                // monotonic heartbeat read does not query Unity, request history or the registry.
                bool responsive = _serverRunning && EditorDispatchReadiness.IsResponsive;
                var statusText = !_serverRunning ? "Stopped" : !responsive ? "Editor update stalled" : _editorBusy ? "Unity compiling / importing" : "Editor responding";
                var statusColor = responsive && !_editorBusy ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.8f, 0.5f, 0.2f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("Status");
                    GUILayout.Label(statusText, Tinted(_tintedLabel, statusColor));
                }

                using (new EditorGUI.DisabledGroupScope(true))
                {
                    EditorGUILayout.IntField("Port", _serverPort);
                }

                EditorGUILayout.LabelField("Transport", _transport);
                EditorGUILayout.HelpBox("Native menus and dialogs can pause Unity. Requests waiting over 2s expire without running.", MessageType.None);
                EditorGUILayout.SelectableLabel(_endpoint, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                using (new EditorGUI.DisabledGroupScope(!_serverRunning))
                    if (GUILayout.Button("Copy endpoint")) EditorGUIUtility.systemCopyBuffer = _endpoint;
                EditorGUILayout.LabelField("Authentication", _requireAuthentication ? "Token required" : "Optional · local only");

                if (_serverRunning)
                {
                    using (new EditorGUI.DisabledGroupScope(true))
                    {
                        EditorGUILayout.LongField(new GUIContent("HTTP requests", "Since this managed server start, including protocol requests; separate from lifetime tool counts."), _totalRequests);
                        EditorGUILayout.DoubleField(new GUIContent("Dispatch time (ms)", "Average main-thread request dispatch time; excludes deferred completion and network delivery."), _avgResponseMs);
                        EditorGUILayout.TextField("Since server start", _uptimeText);
                    }
                }

                EditorGUILayout.Space(4);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_serverRunning)
                    {
                        if (GUILayout.Button("Stop Server", GUILayout.Width(120)))
                        {
                            McpEditorServer.Shutdown();
                            _refreshRequested = true;
                        }
                    }
                    else
                    {
                        if (GUILayout.Button("Start Server", GUILayout.Width(120)))
                        {
                            McpEditorServer.Start();
                            _refreshRequested = true;
                        }
                    }
                }
            }
        }

        // ===================== CONNECTED SESSIONS =====================


        private void DrawModernActivity()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Stateless requests", _headerStyle);
                EditorGUILayout.LabelField($"Recent: {_modernActivity.Count}/{McpRequestActivity.Capacity} · Open streams: {_modernStreamCount}", EditorStyles.miniLabel);
                EditorGUILayout.HelpBox("Client labels are self-reported. Cancel stops request work where supported; it does not undo effects or revoke access.", MessageType.Info);
                foreach (var item in _modernActivity.Take(12))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string client = (string)item["client_label"];
                        if (string.IsNullOrEmpty(client)) client = "Unreported client";
                        string operation = (string)item["tool"];
                        if (string.IsNullOrEmpty(operation)) operation = (string)item["method"];
                        GUILayout.Label(new GUIContent(client + " · " + operation, "Operation " + (string)item["operation_id"]), EditorStyles.miniLabel);
                        GUILayout.Label((string)item["status"], EditorStyles.miniLabel, GUILayout.Width(145));
                        using (new EditorGUI.DisabledScope((bool?)item["cancellable"] != true))
                            if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(48)))
                            { _pendingCancelOperationId = (string)item["operation_id"]; _refreshRequested = true; }
                    }
                }
                if (_modernActivity.Count > 12)
                    EditorGUILayout.LabelField("Showing the 12 most recent requests; get_dashboard_state includes retained history.", EditorStyles.miniLabel);
            }
        }

        // ===================== AUTHENTICATION =====================
        private void DrawTools()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Tools · lifetime calls", _headerStyle);
                _toolSearch = EditorGUILayout.TextField(new GUIContent("Search tools", "Search names, descriptions or categories."), _toolSearch);
                bool focused = EditorGUILayout.ToggleLeft("Advertise focused catalog to clients", _focusedCatalog);
                if (focused != _focusedCatalog) { ToolCatalog.Focused = focused; _focusedCatalog = focused; }
                _usageFilter = (UsageFilter)EditorGUILayout.EnumPopup("Show", _usageFilter);
                var order = (ToolOrder)EditorGUILayout.EnumPopup("Sort", _toolOrder);
                if (order != _toolOrder) { _toolOrder = order; SortUsage(); }
                if (_toolOrder == ToolOrder.Category)
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Expand all")) foreach (var key in _toolGroups.Keys) _expandedToolGroups.Add(key);
                    if (GUILayout.Button("Collapse all")) _expandedToolGroups.Clear();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Tool", EditorStyles.boldLabel);
                    GUILayout.Label("Calls", EditorStyles.boldLabel, GUILayout.Width(65));
                }
                int visible = 0;
                if (_toolOrder != ToolOrder.Category)
                {
                    foreach (var tool in _byUsage)
                    {
                        if (!MatchesTool(tool.Category, tool)) continue;
                        DrawToolRow(tool);
                        visible++;
                    }
                }
                else foreach (var pair in _toolGroups)
                {
                    int matches = 0;
                    foreach (var tool in pair.Value) if (MatchesTool(pair.Key, tool)) matches++;
                    if (matches == 0) continue;
                    visible += matches;
                    bool expanded = _expandedToolGroups.Contains(pair.Key);
                    bool next = EditorGUILayout.Foldout(expanded, $"{pair.Key} ({matches})", true);
                    if (next) _expandedToolGroups.Add(pair.Key); else _expandedToolGroups.Remove(pair.Key);
                    if (!next) continue;
                    foreach (var tool in pair.Value)
                    {
                        if (!MatchesTool(pair.Key, tool)) continue;
                        DrawToolRow(tool);
                    }
                }
                if (visible == 0) EditorGUILayout.LabelField("No tools match these filters.", EditorStyles.wordWrappedMiniLabel);
                var since = DateTime.TryParse(_usageSince, out var started) ? started.ToLocalTime().ToString("g") : _usageSince;
                EditorGUILayout.LabelField("Counts since " + since + ". Includes failed attempts; zero calls means unused since tracking began.", EditorStyles.wordWrappedMiniLabel);
                if (!string.IsNullOrEmpty(_usageError)) EditorGUILayout.HelpBox(_usageError, MessageType.Warning);
            }
        }

        private void SortUsage()
        {
            _lastToolOrder = _toolOrder;
            _byUsage.Sort((a, b) =>
            {
                int count = _toolOrder == ToolOrder.LeastUsed ? a.Count.CompareTo(b.Count) : b.Count.CompareTo(a.Count);
                return count != 0 ? count : string.CompareOrdinal(a.Name, b.Name);
            });
        }

        private static void DrawToolRow(ToolView tool)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(tool.Name, tool.Category + " · " + tool.Description), EditorStyles.label, GUILayout.MinWidth(80));
                GUILayout.Label(tool.Calls, EditorStyles.miniLabel, GUILayout.Width(65));
            }
        }

        private bool MatchesTool(string category, ToolView tool)
        {
            if ((_usageFilter == UsageFilter.Used && tool.Count == 0) ||
                (_usageFilter == UsageFilter.Unused && tool.Count != 0)) return false;
            return string.IsNullOrEmpty(_toolSearch) ||
                category.IndexOf(_toolSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                tool.Name.IndexOf(_toolSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (tool.Description?.IndexOf(_toolSearch, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        private void DrawAuthentication()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Authentication", _headerStyle);
                EditorGUILayout.Space(2);

                var requireAuth = EditorGUILayout.ToggleLeft("Require authentication", _requireAuthentication);
                if (requireAuth != _requireAuthentication)
                {
                    McpPreferencesProvider.RequireAuthentication = requireAuth;
                    _refreshRequested = true;
                }
                EditorGUILayout.HelpBox(_requireAuthentication
                    ? "Clients must send the token. This setting applies only to this project's Editor."
                    : "Local access without a token. The Editor listens on 127.0.0.1 only; Host and Origin checks remain active.", MessageType.Info);
                if (!_requireAuthentication) return;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("Auth Token");
                    var displayText = _showToken ? (_cachedToken ?? "N/A") : TokenMask;
                    EditorGUILayout.LabelField(displayText, EditorStyles.textField,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button(_showToken ? "Hide" : "Show", GUILayout.Width(60)))
                        _showToken = !_showToken;

                    if (GUILayout.Button("Copy", GUILayout.Width(60)))
                    {
                        var tokenToCopy = !string.IsNullOrEmpty(McpEditorServer._authToken)
                            ? McpEditorServer._authToken
                            : _cachedToken;

                        if (!string.IsNullOrEmpty(tokenToCopy))
                        {
                            EditorGUIUtility.systemCopyBuffer = tokenToCopy;
                            Debug.Log("[MCP] Token copied to clipboard.");
                        }
                        else
                        {
                            Debug.LogWarning("[MCP] No token available to copy. Is the server running?");
                        }
                    }

                if (GUILayout.Button("Regenerate", GUILayout.Width(90)))
                    {
                        if (EditorUtility.DisplayDialog(
                            "Regenerate MCP Token",
                            "This will invalidate all existing connections. Are you sure?",
                            "Regenerate", "Cancel"))
                        {
                            RegenerateToken();
                        }
                    }
                }
            }
        }

        // ===================== PROJECT ISSUE LOGGING =====================
        private void DrawBacklog()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("MCP issue logging", _headerStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(BacklogHelp, EditorStyles.miniButton, GUILayout.Width(20)))
                        _pendingBacklogHelp = true;
                }
                using (new EditorGUI.DisabledGroupScope(!_backlogState.CanRepair ||
                    (_backlogState.NeedsRepair && !_backlogState.Enabled)))
                {
                    bool enabled = EditorGUILayout.Toggle(new GUIContent("Enable MCP issue logging",
                        "Add package-managed guidance to root AGENTS.md and a local Markdown issue log."), _backlogState.Enabled);
                    if (enabled != _backlogState.Enabled)
                        _pendingBacklogAction = enabled ? BacklogAction.Enable : BacklogAction.Disable;
                }
                // Unity can restore a null string as empty during a domain reload.
                bool hasError = !string.IsNullOrEmpty(_backlogError);
                EditorGUILayout.HelpBox(hasError ? _backlogError : _backlogState.Message,
                    hasError ? MessageType.Error : _backlogState.NeedsRepair ? MessageType.Warning : MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_backlogState.NeedsRepair)
                    {
                        using (new EditorGUI.DisabledGroupScope(!_backlogState.CanRepair))
                            if (GUILayout.Button("Repair setup")) _pendingBacklogAction = BacklogAction.Repair;
                    }
                    using (new EditorGUI.DisabledGroupScope(!_backlogLogExists))
                        if (GUILayout.Button("Open issue log"))
                            EditorUtility.OpenWithDefaultApp(Path.Combine(_backlogProjectRoot, McpBacklogSetup.LogName));
                }
            }
        }

        // ===================== SETTINGS =====================
        private void DrawSettings()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Settings", _headerStyle);
                EditorGUILayout.Space(2);

                using (new EditorGUI.DisabledGroupScope(true))
                {
                    EditorGUILayout.IntField("Discovered Tools", _toolCount);
                }

                EditorGUILayout.Space(4);


                EditorGUILayout.Space(4);

                var newVerbose = EditorGUILayout.Toggle("Verbose Tool Logging", McpToolRegistry.VerboseLogging);
                if (newVerbose != McpToolRegistry.VerboseLogging)
                    McpToolRegistry.VerboseLogging = newVerbose;

                var newProfiling = EditorGUILayout.Toggle(
                    new GUIContent("Tool Execution Profiling",
                        "Track per-tool timing histograms and GC allocations."),
                    ToolExecutionProfiler.Enabled);
                if (newProfiling != ToolExecutionProfiler.Enabled)
                    ToolExecutionProfiler.Enabled = newProfiling;

                EditorGUILayout.Space(4);

                var newEnableCode = EditorGUILayout.Toggle(
                    new GUIContent("Enable Code Execution",
                        "Allow the execute_code tool to run arbitrary C# in the Editor. " +
                        "Grants the Editor process's permissions to MCP clients. No sandbox or interruptible timeout."),
                    _codeExecutionEnabled);
                if (newEnableCode != _codeExecutionEnabled)
                {
                    // Persist now, but let the snapshot pick the new value up after the pass —
                    // the warning box below is conditional, so flipping it mid-pass would leave
                    // this pass with a different control count than its Layout pass.
                    EnableCodeExecution = newEnableCode;
                    _refreshRequested = true;
                }

                if (_codeExecutionEnabled)
                {
                    EditorGUILayout.HelpBox(
                        "Code execution is ENABLED. Trust every connected client: code can access files and network with the Editor's permissions. There is no sandbox, and a timeout cannot stop running code.",
                        MessageType.Warning);
                }
            }
        }

        // ===================== MODULES =====================
        private void DrawModules()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Modules", _headerStyle);
                EditorGUILayout.Space(2);

                string error = (string)_moduleSnapshot["policy_error"];
                if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Warning);
                foreach (JObject module in (JArray)_moduleSnapshot["modules"])
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            bool requested = (bool)module["requested_enabled"];
                            bool next = GUILayout.Toggle(requested, new GUIContent("", "Use this module's MCP tools when its dependencies are available. Does not uninstall the Unity package or prevent C# compilation."), GUILayout.Width(18));
                            if (next != requested) { _pendingModuleToggle = (string)module["name"]; _pendingModuleEnabled = next; }
                            GUILayout.Label((string)module["name"], EditorStyles.boldLabel);
                            GUILayout.FlexibleSpace();
                            string status = (string)module["status"];
                            GUILayout.Label(status switch { "enabled" => "Enabled", "disabled" => "Disabled", "not_installed" => "Not installed", "unavailable" => "Dependency missing", "awaiting_reload" => "Awaiting compilation", _ => "Settings error" }, EditorStyles.miniLabel);
                        }
                        GUILayout.Label($"{module["active_tool_count"]} / {module["tool_count"]} tools", EditorStyles.miniLabel);
                        string description = (string)module["description"] ?? "";
                        GUILayout.Label(description.Length > 240 ? description.Substring(0, 240) + "…" : description, EditorStyles.wordWrappedMiniLabel);
                    }
                }
                foreach (var sourceError in (JArray)_moduleSnapshot["source_errors"] ?? new JArray())
                    EditorGUILayout.HelpBox((string)sourceError, MessageType.Warning);
                if (_projectModuleIds.Length > 0)
                    _selectedModuleIndex = EditorGUILayout.Popup("Project module", _selectedModuleIndex, _projectModuleIds);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("New module")) _pendingModuleAction = "create";
                    if (GUILayout.Button("Import…")) _pendingModuleAction = "import";
                    using (new EditorGUI.DisabledGroupScope(_projectModuleIds.Length == 0))
                        if (GUILayout.Button("Export…")) _pendingModuleAction = "export";
                }
            }
        }

        // ===================== LOG =====================
        private void DrawActivity()
        {
            float available = Mathf.Max(80, position.height - 110);
            DrawLog(_issuesExpanded ? available * 0.6f : available);
            EditorGUILayout.Space(6);
            DrawIssues(_activityExpanded ? available * 0.4f : available);
        }

        private void DrawLog(float height)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _activityExpanded = EditorGUILayout.Foldout(_activityExpanded, "Activity log", true);
                    GUILayout.FlexibleSpace();
                    if (_activityExpanded)
                    {
                        _autoScrollLog = GUILayout.Toggle(_autoScrollLog, "Auto-scroll", GUILayout.Width(85));
                        if (GUILayout.Button("Clear", GUILayout.Width(50)))
                            _clearLogRequested = true;
                    }
                }
                if (!_activityExpanded) return;
                if (_scrollLogToEnd)
                {
                    // BeginScrollView clamps this to the content height once it knows it, so the
                    // flag is only cleared after a Repaint pass has actually applied it.
                    _logScroll.y = float.MaxValue;
                    if (Event.current.type == EventType.Repaint) _scrollLogToEnd = false;
                }

                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(height));
                if (_logRenderBuffer.Count == 0)
                    EditorGUILayout.LabelField("No MCP log messages yet.", EditorStyles.miniLabel);

                foreach (var entry in _logRenderBuffer)
                {
                    var style = entry.Type switch
                    {
                        LogType.Error or LogType.Exception => _errorStyle,
                        LogType.Warning => _warningStyle,
                        _ => _infoStyle
                    };

                    GUILayout.Label(entry.Text, style);
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawIssues(float height)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _issuesExpanded = EditorGUILayout.Foldout(_issuesExpanded, $"AI-reported issues ({_issueSnapshot.Total})", true);
                    using (new EditorGUI.DisabledGroupScope(!_issueSnapshot.Exists))
                        if (GUILayout.Button("Open backlog", GUILayout.Width(92))) _pendingBacklogLine = 1;
                }
                if (!_issuesExpanded) return;
                _issueScroll = EditorGUILayout.BeginScrollView(_issueScroll, GUILayout.Height(height));
                if (!string.IsNullOrEmpty(_issueSnapshot.Error))
                    EditorGUILayout.HelpBox(_issueSnapshot.Error, MessageType.Warning);
                else if (!_issueSnapshot.Exists)
                    EditorGUILayout.LabelField("No MCP backlog yet. Enable MCP issue logging in Settings to let agents record their observations.", EditorStyles.wordWrappedLabel);
                else if (_issueSnapshot.Total == 0)
                    EditorGUILayout.LabelField("No AI-reported issues yet.", EditorStyles.wordWrappedLabel);
                if (_issueSnapshot.Total > _issueSnapshot.Issues.Length)
                    EditorGUILayout.LabelField($"Showing the first {_issueSnapshot.Issues.Length} issues. Open the backlog for the full list.", EditorStyles.wordWrappedMiniLabel);
                foreach (var issue in _issueSnapshot.Issues)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(issue.Title, EditorStyles.wordWrappedLabel);
                            if (GUILayout.Button(new GUIContent("Open issue", "Open the backlog at this issue's heading in your configured code editor."), GUILayout.Width(78)))
                                _pendingBacklogLine = issue.Line;
                        }
                        GUILayout.Label(issue.Status, EditorStyles.miniLabel);
                        if (issue.Summary.Length > 0) GUILayout.Label(issue.Summary, EditorStyles.wordWrappedMiniLabel);
                        foreach (var link in issue.Links)
                            if (GUILayout.Button(new GUIContent(link.Label, link.Target), EditorStyles.linkLabel))
                                _pendingIssueLink = link.Target;
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void OpenBacklog(int line)
        {
            try
            {
                if (!File.Exists(_issueSnapshot.Path)) { Debug.LogWarning("[MCP] The backlog file is no longer available."); return; }
                if (!Unity.CodeEditor.CodeEditor.CurrentEditor.OpenProject(_issueSnapshot.Path, line, 1))
                    EditorUtility.OpenWithDefaultApp(_issueSnapshot.Path);
            }
            catch (ExitGUIException) { throw; }
            catch (Exception ex) { Debug.LogWarning("[MCP] Could not open the backlog: " + ex.Message); }
        }

        private void OpenIssueLink(string target)
        {
            // Revalidate at click time. Local evidence is revealed rather than executed.
            if (!McpBacklogReader.TryResolveLink(_backlogProjectRoot, target, out string resolved))
            { Debug.LogWarning("[MCP] The issue link is no longer available."); return; }
            if (Uri.TryCreate(resolved, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                Application.OpenURL(resolved);
            else EditorUtility.RevealInFinder(resolved);
        }

        // ===================== HELPERS =====================
        private void RegenerateToken()
        {
            try
            {
                var libraryPath = Path.Combine(
                    Directory.GetParent(Application.dataPath)?.FullName ?? "",
                    "Library");
                var tokenPath = Path.Combine(libraryPath, "MCPServer.token");

                if (File.Exists(tokenPath))
                    File.Delete(tokenPath);

                _cachedToken = null;
                _showToken = false;

                if (McpEditorServer.IsRunning)
                {
                    McpEditorServer.Shutdown();
                    EditorApplication.delayCall += McpEditorServer.Start;
                }

                Debug.Log("[MCP] Token regenerated. Server will restart with new credentials.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP] Failed to regenerate token: {ex.Message}");
            }
        }

        private static string FormatUptime(double seconds)
        {
            if (seconds < 60) return $"{seconds:F0}s";
            if (seconds < 3600) return $"{seconds / 60:F0}m {seconds % 60:F0}s";
            return $"{seconds / 3600:F0}h {(seconds % 3600) / 60:F0}m";
        }



    }
}
