// Copied into a disposable signing project by sign_release.py. Not a package tool.
using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

public static class SignMcpRelease
{
    private static PackRequest _request;
    private static DateTime _deadline;
    private static string _source, _output, _organization;
    private static Task _organizations;

    public static void Run()
    {
        try
        {
            string Argument(string name)
            {
                var args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, name);
                if (index < 0 || index + 1 >= args.Length)
                    throw new ArgumentException("Missing " + name);
                return args[index + 1];
            }
            _source = Argument("-mcpSignSource");
            _output = Argument("-mcpSignOutput");
            _organization = Argument("-mcpSignOrganization");
            _deadline = DateTime.UtcNow.AddMinutes(8);
            EditorApplication.update += Check;
            EditorWindow.GetWindow<SceneView>().Show();
            UnityEditor.PackageManager.UI.Window.Open("i1rr-unity-mcp");
        }
        catch (Exception error) { Fail(error.Message); }
    }

    private static void Check()
    {
        if (DateTime.UtcNow > _deadline) { Fail("Unity signing timed out. Check the Unity Hub login and organization membership."); return; }
        if (_request == null)
        {
            // Graphical Editors receive the Hub session asynchronously. Batch
            // mode does not receive this personal-account signing credential.
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var type = typeof(EditorApplication).Assembly.GetType("UnityEditor.Connect.UnityConnect", true);
            var instance = type.GetProperty("instance", flags).GetValue(null);
            if (!(bool)type.GetProperty("loggedIn", flags).GetValue(instance)) return;
            if (_organizations == null)
            {
                // Resolve membership through the same authenticated account API
                // that Package Manager uses before offering a signing identity.
                var requests = typeof(EditorApplication).Assembly.GetType("UnityEditor.Connect.UnityConnectRequests", true);
                var method = requests.GetMethod("GetOrganizationsAsync", flags);
                var parameters = method.GetParameters();
                var values = new object[parameters.Length];
                for (int i = 0; i < values.Length; i++) values[i] = parameters[i].DefaultValue;
                _organizations = (Task)method.Invoke(null, values);
                return;
            }
            if (!_organizations.IsCompleted) return;
            if (_organizations.IsFaulted) { Fail("Unity organization lookup failed: " + _organizations.Exception.GetBaseException().Message); return; }
            bool member = false;
            foreach (var org in (IEnumerable)_organizations.GetType().GetProperty("Result", flags).GetValue(_organizations))
                member |= (string)org.GetType().GetProperty("GenesisId", flags).GetValue(org) == _organization;
            if (!member) { Fail("The signed-in Unity account is not a member of the selected organization."); return; }
            _request = Client.Pack(_source, _output, _organization);
            return;
        }
        if (!_request.IsCompleted)
        {
            return;
        }
        EditorApplication.update -= Check;
        if (_request.Status != StatusCode.Success)
        {
            Fail(_request.Error?.message ?? "Unity signing failed.");
            return;
        }
        File.WriteAllText("Library/mcp-signed-archive.txt", _request.Result.tarballPath);
        EditorApplication.Exit(0);
    }

    private static void Fail(string message)
    {
        EditorApplication.update -= Check;
        File.WriteAllText("Library/mcp-sign-error.txt", message);
        EditorApplication.Exit(1);
    }
}
