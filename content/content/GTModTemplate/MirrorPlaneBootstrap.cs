using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Fragilem17.MirrorsAndPortals;
using UnityEngine;

namespace GTModTemplate
{
    public class MirrorPlaneBootstrap : MonoBehaviour
    {
        public Vector3 PlaneSpawnPosition = new Vector3(-51.4114f, 16.6876f, -121.3982f);
        public Vector3 PlaneSpawnRotation = new Vector3(0.2f, 156.7336f, 0f);
        public Vector3 PlaneSpawnScale = new Vector3(2.8817f, 2.0501f, 1f);

        public float PollInterval = 0.25f;
        public float MaxWaitSeconds = 30f;
        public float LayerRecheckInterval = 2f;

        private static readonly (string name, string purpose)[] RequiredShaderProperties = new (string, string)[]
        {
            ("_TexLeft", "left-eye reflection texture"),
            ("_TexRight", "right-eye reflection texture (stereo/XR only)"),
            ("_DistanceBlend", "fade/blend amount based on distance & recursion depth"),
            ("_FadeColor", "color the mirror fades to when out of range"),
            ("_ForceEye", "forces which eye's texture to sample (multipass XR)"),
            ("_WorldPos", "camera world position (multipass XR positional correction)"),
            ("_WorldDir", "camera world right-vector (multipass XR positional correction)"),
        };

        private const string BundleFileName = "gtmirrormod";
        private const string MaterialName = "GT_MirrorMaterial";
        private const string TargetLayerName = "GTMirrorExclusiveLayer";

        private AssetBundle loadedBundle;
        private GameObject mirrorObject;
        private MirrorSurface mirrorSurface;
        private MirrorRenderer mirrorRenderer;
        private bool initialized;
        private int mirrorLayer = -1;

        private void Awake() { }

        private void Start()
        {
            mirrorLayer = GetOrCreateCustomLayer();

            try
            {
                GorillaTagger.OnPlayerSpawned(HandlePlayerSpawned);
            }
            catch (System.Exception) { }

            StartCoroutine(PollForMainCamera());
            StartCoroutine(KeepLayerExclusive(mirrorLayer));
        }

        private void HandlePlayerSpawned() => TryInitializeMirror();

        private IEnumerator PollForMainCamera()
        {
            float waited = 0f;
            while (!initialized && waited < MaxWaitSeconds)
            {
                if (Camera.main != null)
                {
                    TryInitializeMirror();
                    yield break;
                }

                yield return new WaitForSecondsRealtime(PollInterval);
                waited += PollInterval;
            }
        }

        private void TryInitializeMirror()
        {
            if (initialized || Camera.main == null) return;
            initialized = true;
            InitializeMirrorNow();
        }

        private int GetOrCreateCustomLayer()
        {
            for (int i = 8; i < 32; i++)
            {
                string layerName = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(layerName))
                {
                    return i;
                }
                if (layerName == TargetLayerName)
                {
                    return i;
                }
            }
            return -1;
        }

        private bool IsVRMainCamera(Camera cam)
        {
            if (cam == null) return false;
            if (Camera.main != null && cam == Camera.main) return true;
            return cam.CompareTag("MainCamera");
        }

        private void ApplyLayerExclusivity(int layer)
        {
            if (layer == -1) return;

            int hideLayerMask = 1 << layer;

            Camera[] allCameras = Camera.allCameras;
            for (int i = 0; i < allCameras.Length; i++)
            {
                Camera cam = allCameras[i];
                if (cam == null) continue;

                if (IsVRMainCamera(cam))
                {
                    cam.cullingMask |= hideLayerMask;
                }
                else
                {
                    cam.cullingMask &= ~hideLayerMask;
                }
            }
        }

        private IEnumerator KeepLayerExclusive(int layer)
        {
            while (true)
            {
                ApplyLayerExclusivity(layer);
                yield return new WaitForSeconds(LayerRecheckInterval);
            }
        }

        private void InitializeMirrorNow()
        {
            try
            {
                int customLayer = mirrorLayer != -1 ? mirrorLayer : GetOrCreateCustomLayer();
                mirrorLayer = customLayer;

                if (customLayer != -1 && Camera.main != null)
                {
                    Camera.main.cullingMask |= (1 << customLayer);
                }

                Material mat = LoadMirrorMaterial();
                if (mat != null)
                {
                    DumpShaderProperties(mat);

                    if (mirrorObject == null)
                    {
                        mirrorObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                        mirrorObject.SetActive(false);
                        mirrorObject.name = "GTMirrorMod_Mirror";
                        mirrorObject.transform.position = PlaneSpawnPosition;
                        mirrorObject.transform.eulerAngles = PlaneSpawnRotation;
                        mirrorObject.transform.localScale = PlaneSpawnScale;

                        if (customLayer != -1)
                        {
                            mirrorObject.layer = customLayer;
                        }

                        Collider col = mirrorObject.GetComponent<Collider>();
                        if (col != null) Destroy(col);

                        DontDestroyOnLoad(mirrorObject);

                        MeshRenderer mr = mirrorObject.GetComponent<MeshRenderer>();
                        if (mr != null)
                        {
                            Material fallback = new Material(Shader.Find("Unlit/Color"));
                            fallback.color = Color.magenta;
                            mr.sharedMaterial = fallback;

                            mr.sharedMaterial = mat;

                            mirrorSurface = mirrorObject.GetComponent<MirrorSurface>() ?? mirrorObject.AddComponent<MirrorSurface>();
                            if (mirrorSurface != null)
                            {
                                mirrorSurface.Material = mat;
                                mirrorSurface.MyMeshRenderer = mr;
                                mirrorSurface.MyForwardTransform = mirrorObject.transform;
                                mirrorSurface.maxRenderingDistance = 5f;
                                mirrorSurface.fadeDistance = 0.5f;
                                mirrorSurface.maxBlend = 1f;
                                mirrorSurface.FadeColor = Color.black;
                                mirrorSurface.useRecursiveDarkening = true;
                                mirrorSurface.recursiveDarkeningCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
                                mirrorSurface.clippingPlaneOffset = 0f;
                            }

                            mirrorRenderer = mirrorObject.GetComponent<MirrorRenderer>() ?? mirrorObject.AddComponent<MirrorRenderer>();
                            if (mirrorRenderer != null)
                            {
                                mirrorRenderer.mirrorSurfaces = new List<MirrorSurface> { mirrorSurface };
                                mirrorRenderer.recursions = 1;
                                mirrorRenderer.textureSize = Vector2.one * 128f;
                                mirrorRenderer.screenScaleFactor = 0.5f;
                                mirrorRenderer.antiAliasing = MirrorRenderer.AA.Low;
                                mirrorRenderer.disablePixelLights = true;

                                if (customLayer != -1)
                                {
                                    mirrorRenderer.renderLayers = 1 << customLayer;
                                }
                            }

                            mirrorObject.SetActive(true);
                        }
                    }
                }
            }
            catch (System.Exception) { }
        }

        private void DumpShaderProperties(Material material)
        {
            if (material != null && material.shader != null)
            {
                Shader shader = material.shader;
                int count = shader.GetPropertyCount();
                HashSet<string> foundNames = new HashSet<string>();

                for (int i = 0; i < count; i++)
                {
                    foundNames.Add(shader.GetPropertyName(i));
                }

                foreach (var req in RequiredShaderProperties)
                {
                    if (!foundNames.Contains(req.name))
                    {
                        bool missingFlag = true;
                    }
                }
            }
        }

        private Material LoadMirrorMaterial()
        {
            string bundlePath = Path.Combine(Paths.PluginPath, "GTMirrorMod", BundleFileName);
            if (File.Exists(bundlePath))
            {
                loadedBundle = AssetBundle.LoadFromFile(bundlePath);
                if (loadedBundle != null)
                {
                    Material mat = loadedBundle.LoadAsset<Material>(MaterialName);
                    if (mat != null)
                    {
                        Material runtimeMat = new Material(mat);
                        runtimeMat.name = "GTMirrorMod_RuntimeMaterial";
                        return runtimeMat;
                    }
                }
            }
            return null;
        }

        private void OnDestroy()
        {
            if (loadedBundle != null)
            {
                loadedBundle.Unload(false);
                loadedBundle = null;
            }
        }
    }
}