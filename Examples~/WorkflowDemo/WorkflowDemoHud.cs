using UnityEngine;

namespace WorkflowDemo {
    public sealed class WorkflowDemoHud : MonoBehaviour {
        public Transform crate;
        private GUIStyle _title;
        private GUIStyle _body;
        private GUIStyle _small;
        private GUIStyle _status;

        private void OnGUI() {
            if (crate == null) return;
            if (_title == null) {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
                _body = new GUIStyle(GUI.skin.label) { fontSize = 22 };
                _small = new GUIStyle(GUI.skin.label) { fontSize = 18 };
                _status = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            }
            var originalMatrix = GUI.matrix;
            var originalColor = GUI.color;
            GUI.matrix = Matrix4x4.Scale(new Vector3(Screen.width / 1280f, Screen.height / 720f, 1));
            bool aligned = Mathf.Abs(crate.position.x) < 0.001f && Mathf.Abs(crate.position.z) < 0.001f;
            Panel(new Rect(28, 28, 1224, 156), new Color(0.035f, 0.065f, 0.12f, 0.96f));
            Text(new Rect(52, 44, 1150, 56), "Inspect. Change. Verify.", _title, Color.white);
            Text(new Rect(54, 108, 1100, 42), "i1rr-unity-mcp  /  a reproducible Unity workflow", _body, new Color(0.62f, 0.76f, 0.9f));
            Panel(new Rect(28, 556, 1224, 136), new Color(0.035f, 0.065f, 0.12f, 0.96f));
            var accent = aligned ? new Color(0.28f, 0.94f, 0.67f) : new Color(1f, 0.56f, 0.34f);
            Panel(new Rect(28, 556, 6, 136), accent);
            Text(new Rect(52, 570, 1140, 40), aligned ? "ALIGNED  /  ready for the saved-scene test" : "MISALIGNED  /  inspect the delivery crate", _status, accent);
            Text(new Rect(52, 616, 1150, 32), $"Delivery Crate x = {crate.position.x:0.00}    |    target x = 0.00, z = 0.00", _body, Color.white);
            Text(new Rect(52, 657, 1150, 28), "Real Game View + HUD capture. The alignment indicator is not a test-run result.", _small, new Color(0.62f, 0.76f, 0.9f));
            GUI.matrix = originalMatrix;
            GUI.color = originalColor;
        }

        private static void Panel(Rect rect, Color color) {
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        private static void Text(Rect rect, string text, GUIStyle style, Color color) {
            GUI.color = color;
            GUI.Label(rect, text, style);
            GUI.color = Color.white;
        }
    }
}
