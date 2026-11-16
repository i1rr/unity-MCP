using System.Collections;
using UnityEngine;

namespace I1rr.UnityMcp.Shared
{
    // Own the frame until the deferred request consumes or cancels it. Never
    // queue Unity's asynchronous disk writer: it can outlive request cleanup.
    internal sealed class GameViewFrameCapture : MonoBehaviour
    {
        internal Texture2D Frame;
        internal string Error;
        internal bool Complete;

        internal IEnumerator Capture(int supersize)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                Frame = ScreenCapture.CaptureScreenshotAsTexture(supersize);
                // The framebuffer contains display-encoded bytes, but Unity's
                // returned texture can be tagged linear. GPU resizing would then
                // apply another gamma conversion. Retag a byte-identical copy.
                if (Frame != null && QualitySettings.activeColorSpace == ColorSpace.Linear && !Frame.isDataSRGB)
                {
                    var display = new Texture2D(Frame.width, Frame.height, Frame.format, false, false);
                    try
                    {
                        display.LoadRawTextureData(Frame.GetRawTextureData<byte>());
                        display.Apply();
                    }
                    catch { Destroy(display); throw; }
                    Destroy(Frame);
                    Frame = display;
                }
            }
            catch (System.Exception error) { Error = error.Message; }
            Complete = true;
        }

        internal void ReleaseFrame()
        {
            if (Frame == null) return;
            if (Application.isPlaying) Destroy(Frame);
            else DestroyImmediate(Frame);
            Frame = null;
        }

        private void OnDestroy() => ReleaseFrame();
    }
}
