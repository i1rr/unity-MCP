using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

namespace I1rr.UnityMcp.Runtime
{
    [Preserve]
    [McpToolCategory("Editor")]
    public static class RuntimeScreenshotTools
    {
        [McpTool("runtime_take_screenshot",
            "Capture the final rendered frame including UI Toolkit and overlay canvases. Use capture_source=camera for a camera-only render. include_image returns a native MCP image preview. Files expire after 1 hour; call individually, not in a batch.",
            RequiresPlayMode = true, DeferredResponse = true, Version = "1.1",
            InputSchemaJson = ScreenshotCapture.FileSchema)]
        [Preserve]
        public static object TakeScreenshot(JObject parameters) => ScreenshotCapture.Capture(parameters);

        [McpTool("runtime_take_screenshot_base64",
            "Capture the final rendered frame including screen overlays as legacy JSON base64. Prefer take_screenshot with include_image=true for a visible MCP image. Use capture_source=camera for camera-only capture. Call individually, not in a batch.",
            RequiresPlayMode = true, DeferredResponse = true, Version = "1.1",
            InputSchemaJson = ScreenshotCapture.Base64Schema)]
        [Preserve]
        public static object TakeScreenshotBase64(JObject parameters) => ScreenshotCapture.Capture(parameters, true);
    }
}
