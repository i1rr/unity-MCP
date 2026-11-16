#if UNITY_6000_6_OR_NEWER
using ObjectId = System.UInt64;
#else
using ObjectId = System.Int32;
#endif
using UnityEngine;

namespace I1rr.UnityMcp.Shared
{
    /// <summary>Version-aware, lossless session-local Unity object identities.</summary>
    public static class ObjectIdentity
    {
        public static ObjectId GetMcpId(this Object obj)
        {
            if (obj == null) return 0;
#if UNITY_6000_6_OR_NEWER
            return EntityId.ToULong(obj.GetEntityId());
#else
            return obj.GetInstanceID();
#endif
        }

#if UNITY_EDITOR
        public static Object Resolve(ObjectId id)
        {
            if (id == 0) return null;
#if UNITY_6000_6_OR_NEWER
            return UnityEditor.EditorUtility.EntityIdToObject(EntityId.FromULong(id));
#else
            return UnityEditor.EditorUtility.InstanceIDToObject(id);
#endif
        }
#endif
    }
}
