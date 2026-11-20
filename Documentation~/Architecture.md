# Architecture

Internal architecture documentation for i1rr-unity-mcp contributors.

---

## High-Level Overview

i1rr-unity-mcp runs two server variants:

- **Editor Server** — HTTP-based, runs inside the Unity Editor process. Uses a 3-thread model with optional native socket keeper for domain reload resilience.
- **Runtime Server** — TCP-based, runs as a MonoBehaviour in development player builds. Uses a 2-thread model compatible with IL2CPP (no HttpListener).

Both share the `I1rr.UnityMcp.Shared` assembly for protocol handling, tool registry, security, utilities, and common infrastructure.

```mermaid
graph TB
    subgraph "Unity Editor Process"
        ES[McpEditorServer]
        SLM[ServerLifecycleManager]
        ERP[EditorRequestPipeline]
        ERW[EditorResponseWriter]
        DRM[DeferredResponseManager]
        NSK[NativeSocketKeeper]
        SM[SessionManager]
    end
    subgraph "Shared Assembly"
        REG[McpToolRegistry]
        PROTO[McpProtocol]
        TH[ToolHelper / McpError]
        BP[BufferPool]
        SL[StructuredLogger]
        METRICS[ServerMetrics]
        TEP[ToolExecutionProfiler]
        RSL[ResponseSizeLimiter]
        MOD[IMcpModule]
    end
    subgraph "Runtime Player"
        RS[McpRuntimeServer]
        RD[RuntimeMainThreadDispatcher]
    end
    ES --> SLM
    ES --> ERP
    ES --> ERW
    ES --> DRM
    ES --> NSK
    ES --> SM
    ES --> REG
    RS --> REG
    REG --> PROTO
    ERP --> BP
    ERW --> BP
    ERW --> RSL
    ES --> METRICS
    ES --> TEP
    ES --> SL
    REG --> MOD
```

---

## Editor Server: Component Decomposition

The `McpEditorServer` class was decomposed into focused single-responsibility components:

| Component | File | Responsibility |
|-----------|------|---------------|
| `McpEditorServer` | `McpEditorServer.cs` | Orchestrator — wires components together, DrainInboundQueue |
| `ServerLifecycleManager` | `ServerLifecycleManager.cs` | Start/stop, domain reload handling and transport cleanup |
| `EditorRequestPipeline` | `EditorRequestPipeline.cs` | Accept thread, HTTP parsing, header validation, inbound queue |
| `EditorResponseWriter` | `EditorResponseWriter.cs` | Response serialization, outbound queue, HTTP writing |
| `DeferredResponseManager` | `DeferredResponseManager.cs` | Deferred result polling, timeout, cleanup |
| `SessionManager` | `Sessions/SessionManager.cs` | Profiler recording lifecycle and concurrency limits |
| `NativeSocketKeeper` | `NativeSocketKeeper.cs` | P/Invoke wrapper for `mcpkeeper.dll` |

---

## Infrastructure Components

| Component | File | Purpose |
|-----------|------|---------|
| `BufferPool` | `Shared/Utilities/BufferPool.cs` | Pooled byte arrays for response serialization — reduces GC pressure |
| `StructuredLogger` | `Shared/Utilities/StructuredLogger.cs` | JSON-structured logging with severity levels and context fields |
| `ServerMetrics` | `Shared/Utilities/ServerMetrics.cs` | Request counters, error rates, connection tracking |
| `ToolExecutionProfiler` | `Shared/Utilities/ToolExecutionProfiler.cs` | Per-tool execution timing (avg/p95/max) |
| `ResponseSizeLimiter` | `Shared/Utilities/ResponseSizeLimiter.cs` | Automatic response truncation with pagination hints |
| `ToolHelper` | `Shared/Utilities/ToolHelper.cs` | Parameter extraction, pagination, common tool patterns |
| `McpError` / `McpErrorCodes` | `Shared/McpError.cs`, `Shared/McpErrorCodes.cs` | Structured error creation with standard error codes |
| `IMcpModule` | `Shared/IMcpModule.cs` | Interface for optional module registration and lifecycle |

---

## Editor Server: Thread Model

The Editor server uses three threads plus the Unity main thread:

```mermaid
sequenceDiagram
    participant Client as AI Client
    participant Accept as Accept Thread
    participant Main as Main Thread (Update)
    participant Writer as Response Writer Thread
    participant Native as Native Socket Keeper

    Client->>Native: TCP connect
    Native->>Accept: Dequeue client
    Accept->>Accept: Parse HTTP request
    Accept->>Main: Enqueue McpRequest
    Main->>Main: Protocol dispatch + Tool execution
    Main->>Writer: Enqueue CompletedRequest
    Writer->>Client: Write HTTP response
```

### Thread Responsibilities

| Thread | Class | Role |
|--------|-------|------|
| **Accept Thread** | `EditorRequestPipeline` | Dequeues accepted connections from native keeper (or accepts from HttpListener), parses HTTP, validates headers, enqueues to inbound queue |
| **Main Thread** | `McpEditorServer.DrainInboundQueue` | Drains up to 20 requests/tick, runs protocol dispatch, executes tools, handles deferred results |
| **Response Writer** | `EditorResponseWriter` | Drains outbound queue, serializes HTTP responses, writes to native socket or HttpListener |

### Queue Architecture

```mermaid
graph LR
    AT[Accept Thread] -->|ConcurrentQueue| IQ[Inbound Queue<br/>cap: 200]
    IQ -->|DrainInboundQueue| MT[Main Thread]
    MT -->|BlockingCollection| OQ[Outbound Queue]
    OQ -->|Take| RW[Response Writer]
```

- **Inbound Queue**: `ConcurrentQueue<McpRequest>`, capped at 200 entries. Overflow returns HTTP 503.
- **Outbound Queue**: `BlockingCollection<CompletedRequest>`, blocks the writer thread until work arrives.

---

## Runtime Server: Thread Model

The Runtime server uses two threads (compatible with IL2CPP stripping):

```mermaid
sequenceDiagram
    participant Client as AI Client
    participant Accept as Accept Thread
    participant Update as MonoBehaviour.Update

    Client->>Accept: TCP connect
    Accept->>Accept: Parse HTTP (TcpHttpHandler)
    Accept->>Update: Enqueue request
    Update->>Update: Protocol dispatch + Tool execution
    Update->>Client: Write response (inline)
```

| Thread | Role |
|--------|------|
| **Accept Thread** | `TcpListener.AcceptTcpClient()`, parse HTTP, enqueue |
| **Main Thread (Update)** | Drain queue (max 3/frame), execute tools, write response directly on the client's `NetworkStream` |

The Runtime server writes responses synchronously on the main thread (no separate writer thread) because player builds cannot afford the overhead of a third thread and the response volume is lower.

---

## Request Lifecycle

```mermaid
flowchart TD
    A[HTTP Request Arrives] --> B{Native or Fallback?}
    B -->|Native| C[NativeSocketKeeper.Dequeue]
    B -->|Fallback| D[HttpListener.GetContext]
    C --> E[Parse HTTP / Validate]
    D --> E
    E --> F{Valid?}
    F -->|No| G[Return 400/401/503]
    F -->|Yes| H[Enqueue to Main Thread]
    H --> I[Validate stateless envelope and request context]
    I --> J{Method?}
    J -->|server/discover| K[Advertise supported protocol and capabilities]
    J -->|tools/list| L[McpToolRegistry.ListTools]
    J -->|tools/call| M[Find & Execute Tool]
    M --> N{Deferred?}
    N -->|No| O[Enqueue Response]
    N -->|Yes| P[Register DeferredResult]
    P --> Q[Poll IsComplete each frame]
    Q -->|Complete| O
    Q -->|Timeout| R[Enqueue Timeout Error]
    O --> S[Response Writer sends HTTP]
```

### Authentication

1. Token is generated using `System.Security.Cryptography.RandomNumberGenerator` (256-bit, Base64url encoded) on first run. Stored at `Library/MCPServer.token` with filesystem permissions restricted to the current user.
2. Requests must include `Authorization: Bearer <token>` header.
3. Token comparison uses constant-time HMAC-based equality (prevents timing side-channel attacks).
4. Exception: `/health` endpoint returns `{"status":"ok"}` without auth (full details require auth).
5. Per-IP rate limiting (120 requests/minute) protects against brute-force attempts.

---

## Domain Reload Sequence

Unity reloads the C# domain on script changes, asset imports, and entering/exiting Play Mode. Without mitigation, this kills the HTTP server and drops all client connections.

```mermaid
sequenceDiagram
    participant Unity as Unity Editor
    participant SLM as ServerLifecycleManager
    participant NSK as NativeSocketKeeper
    participant Client as AI Client

    Unity->>SLM: beforeAssemblyReload
    SLM->>SLM: Interrupt pending work and stage outcomes
    SLM->>NSK: Detach() — stop dequeueing
    SLM->>SLM: Stop Accept + Writer threads
    Note over NSK: Native keeps listening<br/>Returns 503 + Retry-After

    Client->>NSK: Request during reload
    NSK->>Client: HTTP 503, Retry-After: 2

    Unity->>Unity: Reload assemblies...
    Unity->>SLM: InitializeOnLoad (static ctor)
    SLM->>SLM: DelayCall → Start()
    SLM->>NSK: Attach() — resume dequeueing
    Note over SLM: Server fully operational again
```

### What Gets Preserved

| Data | Mechanism |
|------|-----------|
| TCP listening socket | Native DLL (`mcpkeeper.dll`) holds socket fd |
| Pending deferred results | Interrupted; inspect explicit operation handles before retrying |

### Fallback Mode (No Native DLL)

When `mcpkeeper.dll` is unavailable, the server uses `HttpListener`. During domain reload:
- The listener is disposed; clients get connection refused.
- Port file is preserved; clients can reconnect after reload.
- Clients send fresh independent requests with per-request metadata.

---

## Native Socket Keeper

`mcpkeeper.dll` is a minimal native C library that:

1. **Owns the listening socket** — survives C# domain destruction.
2. **Accepts connections** — queues client fds for managed code.
3. **Returns 503 during reload** — when C# is detached, sends `HTTP/1.1 503 Service Unavailable` with `Retry-After: 2` to any connecting client.
4. **Provides raw I/O** — `mcpkeeper_recv`/`mcpkeeper_send` for direct socket read/write.

### API Surface

```
mcpkeeper_start(port)      → Start listening on port
mcpkeeper_attach()         → C# ready to handle requests
mcpkeeper_detach()         → C# going away (domain reload)
mcpkeeper_dequeue(ms)      → Get next accepted client fd (blocking with timeout)
mcpkeeper_recv/send(...)   → Raw socket I/O
mcpkeeper_close_client(id) → Close a client connection
mcpkeeper_stage_response() → Stage response bytes for flush
mcpkeeper_flush_staged()   → Flush all staged responses
mcpkeeper_stop()           → Stop and release the socket
```

The managed wrapper is `NativeSocketKeeper.cs` which checks DLL availability once and exposes a safe API.

---

## Stateless request lifecycle

Only MCP 2026-07-28 per-request HTTP is accepted. Each POST is authenticated,
validated by `McpModernHttp`, and dispatched with immutable `McpRequestContext`.
Matching version/method/name headers and metadata are required; old versions
and `MCP-Session-Id` are rejected before effects. `server/discover` advertises
one version. There is no initialize handshake, protocol session table, session
capacity, session persistence or DELETE lifecycle. Wire batches are rejected.

Callbacks receive request context. Deferred tools retain that context and its
cancellation token; SSE owns each connection lifetime. Disconnect, timeout and
shutdown cancel supported work with main-thread cleanup once. Reload interrupts
unfinished operations; recover through explicit handles before replaying effects.

`McpRequestActivity` keeps at most 128 observations in the current managed
domain. Self-reported labels are informational; eviction never gates admission.
The Dashboard renders cached request snapshots without discovery in OnGUI.
Editor/Runtime `SessionManager` types manage profiler recordings; these explicit
application handles are independent of the removed MCP protocol sessions.

---

## Optional Module System

Optional integrations are compiled only when their dependencies are present, using Unity's `versionDefines` in `package.json`:

```json
{
  "versionDefines": [
    {
      "name": "com.unity.addressables",
      "expression": "",
      "define": "MCP_ADDRESSABLES"
    }
  ]
}
```

### Module Architecture

```mermaid
graph TB
    subgraph "Always Present"
        SHARED[I1rr.UnityMcp.Shared]
        EDITOR[I1rr.UnityMcp.Editor]
        RUNTIME[I1rr.UnityMcp.Runtime]
    end
    subgraph "Conditional (define-gated)"
        ADDR[Editor.Addressables<br/>MCP_ADDRESSABLES]
    end
    EDITOR --> SHARED
    RUNTIME --> SHARED
    ADDR --> EDITOR
```

### Adding a New Optional Module

1. Create a new folder: `Editor.MyModule/` or `Runtime.MyModule/`.
2. Create an asmdef referencing `I1rr.UnityMcp.Editor` (or Runtime) and your dependency.
3. Add `defineConstraints: ["MCP_MYMODULE"]` to the asmdef.
4. Add a `versionDefines` entry in `package.json` mapping the dependency package to `MCP_MYMODULE`.
5. Implement `IMcpModule` for lifecycle management:
   ```csharp
   public class MyModule : IMcpModule
   {
       public string Name => "MyModule";
       public void Initialize() { /* setup */ }
       public void Shutdown() { /* cleanup */ }
       public JObject GetHealthStatus() => new JObject { ["status"] = "ok" };
   }
   ```
6. Implement tools with `[McpTool]` — they are auto-discovered by the registry.

---

## Port Selection

The Editor server uses a deterministic port derived from the project path:

```
port = PortRangeStart + (FNV1a(projectPath) % PortRangeSize)
```

- Range: 8100–8999 (Editor), 9100–9999 (Runtime)
- If the computed port is in use, probes up to 10 adjacent ports.
- Port is written to `Library/MCPServer.port` for client discovery.
- Also written to `.vscode/mcp.json` for VS Code MCP client integration.

---

## Key Design Constraints

1. **Main thread execution**: All Unity API calls must happen on the main thread. Tools execute in `EditorApplication.update` (Editor) or `MonoBehaviour.Update` (Runtime).
2. **No async/await in tools**: Tool methods are synchronous. Use `DeferredResult` for waiting.
3. **IL2CPP compatibility**: Runtime server avoids `HttpListener` (stripped by IL2CPP). Uses raw `TcpListener` + manual HTTP parsing.
4. **Zero release footprint**: Runtime assembly uses `defineConstraints: ["DEVELOPMENT_BUILD"]` — excluded from shipping builds.
5. **Domain reload safety**: Static state is lost on reload. Use `SessionState` for persistence, native DLL for socket continuity.
6. **Security by default**: Localhost-only binding, CSPRNG tokens, constant-time comparison, restricted file permissions. LAN access is opt-in.

---

## Tool File Organization

Tools are organized into focused files by domain, following single-responsibility:

| Category | Files |
|----------|-------|
| Scene & Hierarchy | `SceneTools.cs` |
| Components | `ComponentReadTools.cs`, `ComponentWriteTools.cs` |
| Assets | `AssetSearchTools.cs`, `AssetInfoTools.cs`, `AssetOperationTools.cs` |
| Profiling | `FrameTimingTools.cs`, `MemoryProfilingTools.cs`, `ProfilerCounterTools.cs` |
| Perforce | `P4StatusTools.cs`, `P4EditTools.cs`, `P4DiffTools.cs` |
| Animation | `AnimationTools.cs` |
| Editor | `EditorControlTools.cs`, `CompilationTools.cs`, `ConsoleTools.cs` |
| Code & Types | `CodeSearchTools.cs`, `TypeReflectionTools.cs` |
| Utility | `UtilityTools.cs`, `ScreenshotTools.cs`, `CoroutineTools.cs`, `SessionTools.cs` |
| Files | `FileTools.cs` |
| Prefabs | `PrefabTools.cs` |
| Build | `BuildTools.cs` |

---

## Observability

### Structured Logging

`StructuredLogger` outputs JSON-formatted log entries with:
- Timestamp, severity level, message
- Context fields (tool name, request ID)
- Configurable minimum severity

### Metrics

`ServerMetrics` tracks:
- Total requests processed
- Error counts by error code
- Active connections
- Buffer pool usage (rented/returned)

### Tool Execution Profiling

`ToolExecutionProfiler` records per-tool:
- Invocation count
- Average execution time
- P95 and max execution time
- Exposed via the `/health` endpoint (authenticated) and `diagnostics` tool

---

## Testing

### Test Structure

- `Tests/Editor/` — Editor tool unit tests (NUnit, runs in Unity Test Runner)
- `Tests/Runtime/` — Runtime tool tests
- `Tests/run-tests.ps1` — CLI test execution script
- Repository maintainers pack the tested revision with `Tools~/pack_release.py`.

### Coverage Targets

- Infrastructure code (server, pipeline, sessions): 60%
- Tool implementations: 40%

### Static Analysis

Roslyn analyzers enforce:
- Nullable reference type annotations (`CS8600`–`CS8605` as errors)
- `IDisposable` pattern compliance (`CA2000`, `CA2213`)
- Consistent error handling patterns
