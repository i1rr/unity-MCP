using I1rr.UnityMcp.Shared;
using UnityEditor.AddressableAssets;

namespace I1rr.UnityMcp.Editor.Addressables
{
    /// <summary>
    /// Module registration for the Addressables integration.
    /// Reports availability based on whether AddressableAssetSettings exist.
    /// </summary>
    public sealed class AddressablesModule : IMcpModule
    {
        public string ModuleName => "Addressables";

        public bool IsAvailable => AddressableAssetSettingsDefaultObject.Settings != null;

        public void Initialize() { }

        public void Shutdown() { }
    }
}
