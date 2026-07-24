using BepInEx;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GTModTemplate
{
    [BepInPlugin(Constants.Name, Constants.Guid, Constants.Version)]
    public class PluginBepInEx : BaseUnityPlugin
    {
        private void Awake()
        {
            Debug.Log("[" + Constants.Name + "] Plugin Awake called.");

            TrySetCompatMode();

            var host = GameObject.Find("GTMirrorMod_Host");
            if (!host)
            {
                host = new GameObject("GTMirrorMod_Host");
                DontDestroyOnLoad(host);
                Debug.Log("[" + Constants.Name + "] Created host object.");
            }

            if (!host.GetComponent<Main>())
            {
                host.AddComponent<Main>();
            }

            if (!host.GetComponent<MirrorPlaneBootstrap>())
            {
                host.AddComponent<MirrorPlaneBootstrap>();
            }

            Debug.Log("[" + Constants.Name + "] Init done.");
        }

        private void TrySetCompatMode()
        {
            try
            {
                var s = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
                if (s != null && !s.enableRenderCompatibilityMode)
                {
                    s.enableRenderCompatibilityMode = true;
                    Debug.Log("[" + Constants.Name + "] Forced compat mode.");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[" + Constants.Name + "] Compat mode failed: " + ex.Message);
            }
        }
    }
}