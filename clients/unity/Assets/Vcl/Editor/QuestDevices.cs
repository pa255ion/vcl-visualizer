// OpenXR plugin 1.10 declares com.oculus.supportedDevices = "quest|quest2|cambria",
// which predates the Quest 3 family. Add quest3 and quest3s to the generated
// manifests (after the plugin has written them).
using System.IO;
using UnityEditor.Android;

namespace Vcl.EditorTools
{
    public class QuestDevices : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 10000;

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            var root = Directory.GetParent(unityLibraryPath).FullName;
            foreach (var f in Directory.GetFiles(root, "AndroidManifest.xml", SearchOption.AllDirectories))
            {
                var s = File.ReadAllText(f);
                if (!s.Contains("com.oculus.supportedDevices") || s.Contains("quest3")) continue;
                s = s.Replace("quest|quest2|", "quest|quest2|quest3|quest3s|");
                File.WriteAllText(f, s);
            }
        }
    }
}
