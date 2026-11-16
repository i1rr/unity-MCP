using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;

namespace I1rr.UnityMcp.Editor
{
    [McpToolCategory("Editor")]
    public static class ScreenshotTools
    {
        [McpTool("take_screenshot",
            "Capture the final Game View including UI Toolkit and overlay canvases. Requires unpaused Play Mode. Use capture_source=camera for a camera-only render. include_image returns a native MCP image preview. Files expire after 1 hour; call individually, not in a batch.",
            AutoRoute = true, DeferredResponse = true, Version = "1.1",
            InputSchemaJson = ScreenshotCapture.FileSchema)]
        public static object TakeScreenshot(JObject parameters) => ScreenshotCapture.Capture(parameters);

        [McpTool("take_screenshot_base64",
            "Capture the final Game View including screen overlays as legacy JSON base64. Prefer take_screenshot with include_image=true for a visible MCP image. Use capture_source=camera for camera-only capture. Call individually, not in a batch.",
            AutoRoute = true, DeferredResponse = true, Version = "1.1",
            InputSchemaJson = ScreenshotCapture.Base64Schema)]
        public static object TakeScreenshotBase64(JObject parameters) => ScreenshotCapture.Capture(parameters, true);
    }
}
