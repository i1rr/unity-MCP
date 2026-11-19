using I1rr.UnityMcp.Shared;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Project.Mcp.M_SceneAudit
{
    [McpToolCategory("SceneAudit")]
    public static class SceneColliderTools
    {
        [McpTool("sceneaudit_count_colliders",
            "Count 3D Collider components in the active scene, including inactive objects and disabled colliders.",
            InputSchemaJson = @"{""type"":""object"",""properties"":{},""additionalProperties"":false}")]
        public static JObject CountColliders(JObject args)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return McpError.Create(McpErrorCodes.InvalidState, "Open a scene before counting colliders.");
            }

            int count = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                count += root.GetComponentsInChildren<Collider>(includeInactive: true).Length;
            }

            return new JObject
            {
                ["scene"] = scene.name,
                ["collider_count"] = count
            };
        }
    }
}
