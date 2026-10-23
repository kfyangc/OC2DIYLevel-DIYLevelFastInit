using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        private static IEnumerator AuditSceneMaterials(Scene scene)
        {
            yield return null;
            yield return null;
            if (!scene.isLoaded) yield break;
            HashSet<int> seen = new HashSet<int>();
            List<string> descriptions = new List<string>();
            int broken = 0;
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || !renderer.gameObject.scene.Equals(scene)
                    || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                        broken++;
                        FastInitPlugin.Log.LogWarning("scene material missing [" + scene.name + "]: " + renderer.name);
                        continue;
                    }
                    if (!seen.Add(material.GetInstanceID())) continue;
                    Shader shader = material.shader;
                    string shaderName = shader == null ? "<missing>" : shader.name;
                    bool supported = shader != null && shader.isSupported;
                    bool invalid = !supported || shaderName.IndexOf("InternalErrorShader", StringComparison.OrdinalIgnoreCase) >= 0;
                    string description = material.name + " -> " + shaderName + " (supported=" + supported + ", object=" + renderer.name + ")";
                    if (invalid)
                    {
                        broken++;
                        FastInitPlugin.Log.LogWarning("scene material invalid [" + scene.name + "]: " + description);
                    }
                    if (descriptions.Count < 64) descriptions.Add(description);
                }
            }
            FastInitPlugin.Log.LogInfo("scene material audit [" + scene.name + "]: " + seen.Count + " materials, "
                + broken + " invalid; " + string.Join("; ", descriptions.ToArray()));
        }
    }
}
