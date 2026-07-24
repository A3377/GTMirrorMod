using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using RenderPipeline = UnityEngine.Rendering.RenderPipelineManager;
using UniversalData = UnityEngine.Rendering.Universal.UniversalAdditionalCameraData;
using UniversalPipeline = UnityEngine.Rendering.Universal.UniversalRenderPipeline;

// hello fellow code viewer :salute: ignore the #if UNITY_EDITOR, this was a paid asset ported to a gorilla tag mod, this didnt take long to make but its fairly high quality so i hope you enjoy it!!!!!!!

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fragilem17.MirrorsAndPortals
{
    [ExecuteInEditMode]
    public class MirrorRenderer : MonoBehaviour
    {
        public static List<MirrorRenderer> instances;

        public List<MirrorSurface> mirrorSurfaces;

        [Min(1)]
        public int recursions = 1;

        [Header("Pipeline Settings")]
        public LayerMask renderLayers = -1;
        public CameraOverrideOption opaqueTextureMode = CameraOverrideOption.Off;
        public CameraOverrideOption depthTextureMode = CameraOverrideOption.Off;
        public bool renderShadows = false;
        public bool renderPostProcessing = false;

        [Header("Environment")]
        public Material customSkybox;

        [Header("Clear Flags")]
        public bool overrideClearFlags = false;
        public CameraClearFlags clearFlagsOverride = CameraClearFlags.Color;
        public Color32 clearColorOverride;

        [Header("Quality & Resolution")]
        public Vector2 textureSize = new Vector2(2048f, 2048f);
        public bool useScreenScale = true;
        [Range(0.01f, 1f)]
        public float screenScaleFactor = 1f;
        public RenderTextureFormat textureFormat = RenderTextureFormat.Default;
        public AA antiAliasing = AA.Low;
        public bool disablePixelLights = true;

        [Min(0)]
        public int frameSkipCount = 0;

        public bool useOcclusionCulling = true;
        public float frustumExtension = 0.01f;

        [Header("Events")]
        public UnityEvent onStartRendering;
        public UnityEvent onFinishedRendering;

        [Header("Beta Options")]
        public bool freezeRenderingKeepMaterials = false;

        [Header("Debugging")]
        public bool showDebugVisuals = false;
#if UNITY_EDITOR_OSX
        public bool macOsCrashFixLogs = true;
#endif

        public enum AA
        {
            None = 1,
            Low = 2,
            Medium = 4,
            High = 8
        }

        private readonly List<PooledTexture> pooledTextures = new List<PooledTexture>();
        private readonly List<CameraMatrices> orderedMatrices = new List<CameraMatrices>();

        private static readonly Dictionary<Camera, Camera> reflectionCameras = new Dictionary<Camera, Camera>();
        private static readonly Dictionary<Camera, UniversalData> uacCache = new Dictionary<Camera, UniversalData>();
        private static readonly Dictionary<Camera, Skybox> skyboxCache = new Dictionary<Camera, Skybox>();

        private static MirrorRenderer masterInstance;
        private static Camera activeReflectionCamera;

        private int frameCounter = 0;
        private int cachedTextureResolution = 0;
        private float cachedScreenScale = 0.5f;
        private AA cachedAA = AA.Low;
        private RenderTextureFormat cachedFormat = RenderTextureFormat.Default;
        bool cachedUseScreenScale = true;

        private bool isMultipass = true;
        private bool allowXRRendering = true;

        private UniversalData cachedUacRender;
        private UniversalData cachedUacReflection;
        private Skybox cachedSkyboxRender;
        private Skybox cachedSkyboxReflection;
        private Material targetSkyboxMaterial;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeMirrorBootstrapper()
        {
            int hideLayer = LayerMask.NameToLayer("LCKHide");
            if (hideLayer == -1) return;

            int hideLayerMask = 1 << hideLayer;

            Camera[] allCameras = Camera.allCameras;
            for (int i = 0; i < allCameras.Length; i++)
            {
                Camera cam = allCameras[i];
                if (cam == null) continue;

                if (cam.CompareTag("MainCamera"))
                {
                    cam.cullingMask |= hideLayerMask;
                }
                else
                {
                    cam.cullingMask &= ~hideLayerMask;
                }
            }
        }

        private void OnEnable()
        {
            if (instances == null) instances = new List<MirrorRenderer>();
            instances.Add(this);

            RenderPipeline.beginCameraRendering += OnBeginCameraRendering;

            CacheSettings();

            int customLayer = LayerMask.NameToLayer("LCKHide");
            if (customLayer != -1)
            {
                gameObject.layer = customLayer;
                for (int i = 0; i < transform.childCount; i++)
                {
                    transform.GetChild(i).gameObject.layer = customLayer;
                }
            }
        }

        private void OnDisable()
        {
            if (instances != null) instances.Remove(this);

            RenderPipeline.beginCameraRendering -= OnBeginCameraRendering;

            for (int i = 0; i < pooledTextures.Count; i++)
            {
                ReleasePooledTexture(pooledTextures[i]);
            }
            pooledTextures.Clear();

            if (masterInstance == this) masterInstance = null;
        }

        private void LateUpdate()
        {
            if (!XRSettings.enabled) return;

            isMultipass = XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass;
            if (!isMultipass || reflectionCameras.Count <= 0 || Camera.main == null) return;

            for (int i = 0; i < mirrorSurfaces.Count; i++)
            {
                var surface = mirrorSurfaces[i];
                if (surface != null)
                {
                    surface.UpdatePositionsInMaterial(Camera.main.transform.position, Camera.main.transform.right);
                }
            }
        }

        private void Update()
        {
            int currentResolutionSum = (int)textureSize.x + (int)textureSize.y;
            if (cachedTextureResolution != currentResolutionSum
                || cachedScreenScale != screenScaleFactor
                || cachedAA != antiAliasing
                || cachedFormat != textureFormat
                || cachedUseScreenScale != useScreenScale)
            {
                CacheSettings();

                for (int i = 0; i < pooledTextures.Count; i++)
                {
                    ReleasePooledTexture(pooledTextures[i]);
                }
                pooledTextures.Clear();
            }
        }

        private void CacheSettings()
        {
            cachedUseScreenScale = useScreenScale;
            cachedAA = antiAliasing;
            cachedFormat = textureFormat;
            cachedScreenScale = screenScaleFactor;
            cachedTextureResolution = (int)textureSize.x + (int)textureSize.y;
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!ShouldRenderCamera(cam)) return;

            if (frameCounter > 0)
            {
                frameCounter--;
                return;
            }
            frameCounter = frameSkipCount;

            if (!AnySurfaceVisible(cam))
            {
                if (masterInstance == this) masterInstance = null;
                return;
            }

            if (freezeRenderingKeepMaterials)
            {
                ProcessFrozenMaterials(cam);
                if (masterInstance == this) masterInstance = null;
                return;
            }

            if (masterInstance == null) masterInstance = this;

            GetOrCreateReflectionCamera(cam, out activeReflectionCamera);
            activeReflectionCamera.CopyFrom(cam);
            activeReflectionCamera.cullingMask = renderLayers.value;

            ConfigureCameraPipelineData(cam, activeReflectionCamera);

            if (XRSettings.enabled && masterInstance == this && allowXRRendering)
            {
                HandleXRCameraPass(context, cam, activeReflectionCamera);
            }
            else
            {
                HandleStandardCameraPass(context, cam, activeReflectionCamera);
            }
        }

        private bool ShouldRenderCamera(Camera cam)
        {
            if (!enabled || cam == null) return false;
            if (mirrorSurfaces == null || mirrorSurfaces.Count == 0) return false;

#if UNITY_EDITOR
            return cam.CompareTag("MainCamera") || (cam.cameraType == CameraType.SceneView && cam.name.IndexOf("Preview Camera", System.StringComparison.Ordinal) == -1);
#else
            return cam.CompareTag("MainCamera");
#endif
        }

        private bool AnySurfaceVisible(Camera cam)
        {
            for (int i = 0; i < mirrorSurfaces.Count; i++)
            {
                var surface = mirrorSurfaces[i];
                if (surface != null && surface.VisibleFromCamera(cam, false))
                {
                    return true;
                }
            }
            return false;
        }

        private void ProcessFrozenMaterials(Camera cam)
        {
            for (int i = 0; i < mirrorSurfaces.Count; i++)
            {
                var surface = mirrorSurfaces[i];
                if (surface == null) continue;

                float distance = Vector3.Distance(cam.transform.position, surface.ClosestPoint(cam.transform.position));
                surface.UpdateMaterial(Camera.StereoscopicEye.Left, null, this, 1, distance);
            }
        }

        private void ConfigureCameraPipelineData(Camera sourceCam, Camera targetCam)
        {
#if UNITY_2020_3_OR_NEWER
            GetUacComponent(sourceCam, out cachedUacRender);
            GetUacComponent(targetCam, out cachedUacReflection);

            if (cachedUacRender != null)
            {
                allowXRRendering = cachedUacRender.allowXRRendering;
                cachedUacReflection.requiresColorOption = opaqueTextureMode;
                cachedUacReflection.requiresDepthOption = depthTextureMode;
                cachedUacReflection.renderPostProcessing = renderPostProcessing;
                cachedUacReflection.renderShadows = renderShadows;
            }
            else
            {
                allowXRRendering = true;
            }
#endif

            targetSkyboxMaterial = customSkybox;
            if (targetSkyboxMaterial == null)
            {
                GetSkyboxComponent(sourceCam, out cachedSkyboxRender);
                if (cachedSkyboxRender != null)
                {
                    targetSkyboxMaterial = cachedSkyboxRender.material;
                }
            }

            GetSkyboxComponent(targetCam, out cachedSkyboxReflection);
            if (cachedSkyboxReflection != null)
            {
                cachedSkyboxReflection.material = targetSkyboxMaterial;
            }
        }

        private void HandleStandardCameraPass(ScriptableRenderContext context, Camera sourceCam, Camera reflectionCam)
        {
            reflectionCam.transform.SetPositionAndRotation(sourceCam.transform.position, sourceCam.transform.rotation);
            reflectionCam.worldToCameraMatrix = sourceCam.worldToCameraMatrix;
            reflectionCam.projectionMatrix = sourceCam.projectionMatrix;

            orderedMatrices.Clear();
            onStartRendering.Invoke();

            BuildReflectionOrder(sourceCam, orderedMatrices, 1, Camera.StereoscopicEye.Left);
            ExecuteReflectionRenderPass(context, reflectionCam, orderedMatrices, Camera.StereoscopicEye.Left);

            if (isMultipass)
            {
                for (int i = 0; i < mirrorSurfaces.Count; i++)
                {
                    var surface = mirrorSurfaces[i];
                    if (surface != null) surface.ForceLeftEye();
                }
            }

            onFinishedRendering.Invoke();
        }

        private void HandleXRCameraPass(ScriptableRenderContext context, Camera sourceCam, Camera reflectionCam)
        {
            var centerEyeDevice = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            centerEyeDevice.TryGetFeatureValue(CommonUsages.leftEyePosition, out Vector3 leftPos);
            centerEyeDevice.TryGetFeatureValue(CommonUsages.rightEyePosition, out Vector3 rightPos);

            float ipd = Vector3.Distance(leftPos, rightPos) * sourceCam.transform.lossyScale.x;

            Vector3 originalPos = sourceCam.transform.position;
            sourceCam.transform.position -= sourceCam.transform.right * (ipd * 0.5f);
            reflectionCam.transform.SetPositionAndRotation(sourceCam.transform.position, sourceCam.transform.rotation);
            reflectionCam.worldToCameraMatrix = sourceCam.worldToCameraMatrix;
            sourceCam.transform.position = originalPos;

            reflectionCam.projectionMatrix = sourceCam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);

            orderedMatrices.Clear();
            onStartRendering.Invoke();

            BuildReflectionOrder(sourceCam, orderedMatrices, 1, Camera.StereoscopicEye.Left);
            ExecuteReflectionRenderPass(context, reflectionCam, orderedMatrices, Camera.StereoscopicEye.Left);

            originalPos = sourceCam.transform.position;
            sourceCam.transform.position += sourceCam.transform.right * (ipd * 0.5f);
            reflectionCam.transform.SetPositionAndRotation(sourceCam.transform.position, sourceCam.transform.rotation);
            reflectionCam.worldToCameraMatrix = sourceCam.worldToCameraMatrix;
            sourceCam.transform.position = originalPos;

            reflectionCam.projectionMatrix = sourceCam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);

            orderedMatrices.Clear();
            BuildReflectionOrder(sourceCam, orderedMatrices, 1, Camera.StereoscopicEye.Right);
            ExecuteReflectionRenderPass(context, reflectionCam, orderedMatrices, Camera.StereoscopicEye.Right);

            for (int i = 0; i < mirrorSurfaces.Count; i++)
            {
                var surface = mirrorSurfaces[i];
                if (surface != null) surface.TurnOffForceEye();
            }

            onFinishedRendering.Invoke();
        }

        private void BuildReflectionOrder(Camera sourceCam, List<CameraMatrices> targetList, int depth, Camera.StereoscopicEye eye,
            MirrorSurface parentSurface = null,
            MirrorSurface grandParentSurface = null,
            MirrorSurface greatGrandParentSurface = null,
            MirrorSurface greatGreatGrandParentSurface = null)
        {
            if (depth > recursions + 1) return;

            Vector3 currentPos = activeReflectionCamera.transform.position;
            Quaternion currentRot = activeReflectionCamera.transform.rotation;
            Matrix4x4 currentW2C = activeReflectionCamera.worldToCameraMatrix;
            Matrix4x4 currentProj = activeReflectionCamera.projectionMatrix;

            if (sourceCam.cameraType != CameraType.SceneView && XRSettings.enabled && allowXRRendering)
            {
                currentProj = activeReflectionCamera.GetStereoProjectionMatrix(eye);
            }

            for (int i = 0; i < mirrorSurfaces.Count; i++)
            {
                var surface = mirrorSurfaces[i];
                if (surface == null || surface == parentSurface || !surface.VisibleFromCamera(activeReflectionCamera, true)) continue;

                Vector3 mirrorPos = surface.MyForwardTransform.position;
                Vector3 mirrorNormal = -surface.MyForwardTransform.forward;

                float distance = Vector3.Distance(currentPos, surface.ClosestPoint(currentPos));

                if (distance > surface.maxRenderingDistance && !surface.useRecursiveDarkening) continue;

                float planeConstant = -Vector3.Dot(mirrorNormal, mirrorPos) - surface.clippingPlaneOffset;
                Vector4 reflectionPlane = new Vector4(mirrorNormal.x, mirrorNormal.y, mirrorNormal.z, planeConstant);

                Matrix4x4 reflectionMat = Matrix4x4.identity;
                CalculateReflectionMatrix(ref reflectionMat, reflectionPlane);

                Vector3 reflectedPos = reflectionMat.MultiplyPoint(currentPos);
                activeReflectionCamera.transform.position = reflectedPos;

                Vector3 reflectedForward = Vector3.Reflect(activeReflectionCamera.transform.forward, mirrorNormal);
                activeReflectionCamera.transform.rotation = Quaternion.LookRotation(reflectedForward);

                Matrix4x4 reflectedW2C = activeReflectionCamera.worldToCameraMatrix * reflectionMat;
                activeReflectionCamera.worldToCameraMatrix = reflectedW2C;

                Vector4 clipPlane = ExtractCameraSpacePlane(reflectedW2C, mirrorPos, mirrorNormal, 1.0f, surface.clippingPlaneOffset);

                Matrix4x4 projMat = (sourceCam.cameraType != CameraType.SceneView && XRSettings.enabled && allowXRRendering)
                    ? activeReflectionCamera.GetStereoProjectionMatrix(eye)
                    : activeReflectionCamera.projectionMatrix;

                Matrix4x4 cullingMat = projMat * reflectedW2C;

                if (useOcclusionCulling)
                {
                    Camera.MonoOrStereoscopicEye stereoEye = (XRSettings.enabled && allowXRRendering)
                        ? (Camera.MonoOrStereoscopicEye)eye
                        : Camera.MonoOrStereoscopicEye.Mono;

                    Vector3[] bounds = surface.ShrinkPointsToBounds(activeReflectionCamera, distance, stereoEye);
                    if (bounds != null)
                    {
                        Vector3 backwardOffsetEye = reflectedPos + (activeReflectionCamera.transform.forward * sourceCam.nearClipPlane);
                        cullingMat = BuildOffAxisProjectionMatrix(sourceCam.nearClipPlane, sourceCam.farClipPlane, bounds[0], bounds[1], bounds[2], backwardOffsetEye);
                    }
                }

                MakeProjectionOblique(ref projMat, clipPlane);
                activeReflectionCamera.projectionMatrix = projMat;

                BuildReflectionOrder(sourceCam, targetList, depth + 1, eye, surface, parentSurface, grandParentSurface, greatGrandParentSurface);

                activeReflectionCamera.transform.position = currentPos;
                activeReflectionCamera.transform.rotation = currentRot;
                activeReflectionCamera.worldToCameraMatrix = currentW2C;
                activeReflectionCamera.projectionMatrix = currentProj;

                targetList.Add(new CameraMatrices(sourceCam, projMat, reflectedW2C, cullingMat, surface, depth % 2 != 0, reflectedPos, depth, distance, parentSurface, grandParentSurface, greatGrandParentSurface, greatGreatGrandParentSurface));
            }
        }

        private void ExecuteReflectionRenderPass(ScriptableRenderContext context, Camera reflectionCam, List<CameraMatrices> matricesList, Camera.StereoscopicEye eye)
        {
            int oldPixelLightCount = QualitySettings.pixelLightCount;
            if (disablePixelLights) QualitySettings.pixelLightCount = 0;

            if (overrideClearFlags)
            {
                reflectionCam.clearFlags = clearFlagsOverride;
                reflectionCam.backgroundColor = clearColorOverride;
            }

            for (int i = 0; i < matricesList.Count; i++)
            {
                var mat = matricesList[i];

                if (mat.depth >= recursions + 1)
                {
                    mat.mirrorSurface.UpdateMaterial(eye, null, this, mat.depth, Mathf.Infinity);
                    continue;
                }

                GetFreePooledTexture(out PooledTexture ptex, eye);
                ptex.matrices = mat;
                ptex.liteLock = true;

                if (mat.parentMirrorSurface == null)
                {
                    for (int j = 0; j < pooledTextures.Count; j++)
                    {
                        pooledTextures[j].liteLock = false;
                    }
                    ptex.fullLock = true;
                }

                mat.mirrorSurface.UpdateMaterial(eye, ptex.texture, this, mat.depth, mat.distance);
                reflectionCam.targetTexture = ptex.texture;
                reflectionCam.transform.position = mat.camPos;
                reflectionCam.worldToCameraMatrix = mat.worldToCameraMatrix;
                reflectionCam.projectionMatrix = mat.projectionMatrix;

                if (mat.even) GL.invertCulling = true;

                reflectionCam.useOcclusionCulling = useOcclusionCuringSafely();
                reflectionCam.cullingMatrix = mat.cullingMatrix;

#if UNITY_EDITOR_OSX
                if (macOsCrashFixLogs)
                {
                }
#endif

                UniversalPipeline.RenderSingleCamera(context, reflectionCam);

                if (mat.even) GL.invertCulling = false;

                PropagateSharedTextures(matricesList, mat, eye);
                reflectionCam.useOcclusionCulling = true;
            }

            reflectionCam.targetTexture = null;

            for (int i = 0; i < pooledTextures.Count; i++)
            {
                pooledTextures[i].liteLock = false;
                pooledTextures[i].fullLock = false;
            }

            if (disablePixelLights) QualitySettings.pixelLightCount = oldPixelLightCount;
        }

        private bool useOcclusionCuringSafely() => useOcclusionCulling;

        private void PropagateSharedTextures(List<CameraMatrices> list, CameraMatrices current, Camera.StereoscopicEye eye)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var target = list[i];
                if (target == current || target.depth != current.depth
                    || target.parentMirrorSurface != current.parentMirrorSurface
                    || target.grandParentMirrorSurface != current.grandParentMirrorSurface
                    || target.greatGrandParentMirrorSurface != current.greatGrandParentMirrorSurface)
                {
                    continue;
                }

                var match = pooledTextures.Find(p => p.matrices.mirrorSurface == target.mirrorSurface
                    && p.matrices.parentMirrorSurface == target.parentMirrorSurface
                    && p.matrices.grandParentMirrorSurface == target.grandParentMirrorSurface
                    && p.matrices.greatGrandParentMirrorSurface == target.greatGrandParentMirrorSurface
                    && p.matrices.depth == target.depth && p.eye == eye);

                if (match != null)
                {
                    target.mirrorSurface.UpdateMaterial(eye, match.texture, this, target.depth, target.distance);
                }
            }
        }

        private void GetFreePooledTexture(out PooledTexture textureOut, Camera.StereoscopicEye eye)
        {
            var found = pooledTextures.Find(t => !t.fullLock && !t.liteLock && t.eye == eye);
            if (found == null)
            {
                found = new PooledTexture { eye = eye };
                pooledTextures.Add(found);

                if (useScreenScale && screenScaleFactor > 0f)
                {
                    textureSize = new Vector2(Screen.width * screenScaleFactor, Screen.height * screenScaleFactor);
                }

                var desc = new RenderTextureDescriptor((int)textureSize.x, (int)textureSize.y, textureFormat, 1)
                {
                    useMipMap = false,
                    msaaSamples = (int)antiAliasing
                };

                found.texture = RenderTexture.GetTemporary(desc);
                found.texture.wrapMode = TextureWrapMode.Mirror;
                found.texture.name = $"MirrorTex_{gameObject.name}_{pooledTextures.Count}";
                found.texture.hideFlags = HideFlags.DontSave;

                CacheSettings();
            }
            textureOut = found;
        }

        private void ReleasePooledTexture(PooledTexture ptex)
        {
            if (ptex == null || ptex.texture == null) return;

            foreach (var kvp in reflectionCameras)
            {
                if (kvp.Value != null && kvp.Value.targetTexture == ptex.texture)
                {
                    kvp.Value.targetTexture = null;
                }
            }

            if (activeReflectionCamera != null && activeReflectionCamera.targetTexture == ptex.texture)
            {
                activeReflectionCamera.targetTexture = null;
            }

            RenderTexture.ReleaseTemporary(ptex.texture);
            ptex.texture = null;
        }

        private void GetOrCreateReflectionCamera(Camera sourceCam, out Camera refCam)
        {
            if (!reflectionCameras.TryGetValue(sourceCam, out refCam) || refCam == null)
            {
                var go = new GameObject($"MirrorCamera_{sourceCam.name}", typeof(Camera));
                go.hideFlags = HideFlags.HideAndDontSave;
                refCam = go.GetComponent<Camera>();
                refCam.enabled = false;
                reflectionCameras[sourceCam] = refCam;
            }
        }

        private void GetUacComponent(Camera cam, out UniversalData uac)
        {
            if (!uacCache.TryGetValue(cam, out uac) || uac == null)
            {
                uac = cam.GetComponent<UniversalData>();
                if (uac != null) uacCache[cam] = uac;
            }
        }

        private void GetSkyboxComponent(Camera cam, out Skybox skybox)
        {
            if (!skyboxCache.TryGetValue(cam, out skybox) || skybox == null)
            {
                skybox = cam.GetComponent<Skybox>();
                if (skybox == null) skybox = cam.gameObject.AddComponent<Skybox>();
                skyboxCache[cam] = skybox;
            }
        }

        private static void CalculateReflectionMatrix(ref Matrix4x4 mat, Vector4 plane)
        {
            mat.m00 = 1F - 2F * plane[0] * plane[0];
            mat.m01 = -2F * plane[0] * plane[1];
            mat.m02 = -2F * plane[0] * plane[2];
            mat.m03 = -2F * plane[3] * plane[0];

            mat.m10 = -2F * plane[1] * plane[0];
            mat.m11 = 1F - 2F * plane[1] * plane[1];
            mat.m12 = -2F * plane[1] * plane[2];
            mat.m13 = -2F * plane[3] * plane[1];

            mat.m20 = -2F * plane[2] * plane[0];
            mat.m21 = -2F * plane[2] * plane[1];
            mat.m22 = 1F - 2F * plane[2] * plane[2];
            mat.m23 = -2F * plane[3] * plane[2];

            mat.m30 = 0F;
            mat.m31 = 0F;
            mat.m32 = 0F;
            mat.m33 = 1F;
        }

        private static Vector4 ExtractCameraSpacePlane(Matrix4x4 w2c, Vector3 pos, Vector3 normal, float sign, float offset)
        {
            Vector3 offsetPos = pos + normal * offset;
            Vector3 cpos = w2c.MultiplyPoint(offsetPos);
            Vector3 cnormal = w2c.MultiplyVector(normal).normalized * sign;
            return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
        }

        private static void MakeProjectionOblique(ref Matrix4x4 proj, Vector4 clipPlane)
        {
            Vector4 q = proj.inverse * new Vector4(SignOrZero(clipPlane.x), SignOrZero(clipPlane.y), 1.0f, 1.0f);
            Vector4 c = clipPlane * (2.0f / Vector4.Dot(clipPlane, q));
            proj[2] = c.x;
            proj[6] = c.y;
            proj[10] = c.z + 1.0f;
            proj[14] = c.w;
        }

        private static float SignOrZero(float value)
        {
            if (value > 0f) return 1f;
            if (value < 0f) return -1f;
            return 0f;
        }

        private static Matrix4x4 BuildOffAxisProjectionMatrix(float near, float far, Vector3 bottomLeft, Vector3 bottomRight, Vector3 topLeft, Vector3 eyePos)
        {
            Vector3 va = bottomRight - bottomLeft;
            Vector3 vb = topLeft - bottomLeft;
            Vector3 vr = va.normalized;
            Vector3 vu = vb.normalized;
            Vector3 vn = Vector3.Cross(vr, vu).normalized;

            Vector3 v0 = bottomLeft - eyePos;
            Vector3 v1 = bottomRight - eyePos;
            Vector3 v2 = topLeft - eyePos;

            float d = -Vector3.Dot(v0, vn);
            float l = Vector3.Dot(vr, v0) * near / d;
            float r = Vector3.Dot(vr, v1) * near / d;
            float b = Vector3.Dot(vu, v0) * near / d;
            float t = Vector3.Dot(vu, v2) * near / d;

            Matrix4x4 frustum = Matrix4x4.Frustum(l, r, b, t, near, far);
            Matrix4x4 translation = Matrix4x4.Translate(-eyePos);
            Matrix4x4 rotation = new Matrix4x4();

            rotation.SetRow(0, new Vector4(vr.x, vr.y, vr.z, 0f));
            rotation.SetRow(1, new Vector4(vu.x, vu.y, vu.z, 0f));
            rotation.SetRow(2, new Vector4(vn.x, vn.y, vn.z, 0f));
            rotation.SetRow(3, new Vector4(0f, 0f, 0f, 1f));

            return frustum * rotation * translation;
        }
    }

    public class CameraMatrices
    {
        public Camera renderCamera;
        public Matrix4x4 projectionMatrix;
        public Matrix4x4 worldToCameraMatrix;
        public Matrix4x4 cullingMatrix;
        public MirrorSurface mirrorSurface;
        public bool even;
        public Vector3 camPos;
        int depthVal;
        public int depth
        {
            get => depthVal;
            set => depthVal = value;
        }
        public float distance;
        public MirrorSurface parentMirrorSurface;
        public MirrorSurface grandParentMirrorSurface;
        public MirrorSurface greatGrandParentMirrorSurface;
        public MirrorSurface greatGreatGrandParentMirrorSurface;

        public CameraMatrices(Camera cam, Matrix4x4 proj, Matrix4x4 w2c, Matrix4x4 culling, MirrorSurface surface, bool isEven, Vector3 pos, int depthVal, float dist, MirrorSurface p1, MirrorSurface p2, MirrorSurface p3, MirrorSurface p4)
        {
            renderCamera = cam;
            projectionMatrix = proj;
            worldToCameraMatrix = w2c;
            cullingMatrix = culling;
            mirrorSurface = surface;
            even = isEven;
            camPos = pos;
            this.depthVal = depthVal;
            distance = dist;
            parentMirrorSurface = p1;
            grandParentMirrorSurface = p2;
            greatGrandParentMirrorSurface = p3;
            greatGreatGrandParentMirrorSurface = p4;
        }
    }

    public class PooledTexture
    {
        public RenderTexture texture;
        public Camera.StereoscopicEye eye;
        public CameraMatrices matrices;
        public bool fullLock;
        public bool liteLock;
    }
}