using UnityEngine;
using UnityEngine.XR;
using RenderPipeline = UnityEngine.Rendering.RenderPipelineManager;
using System;

// hello fellow code viewer :salute: ignore the #if UNITY_EDITOR, this was a paid asset ported to a gorilla tag mod, this didnt take long to make but its fairly high quality so i hope you enjoy it!!!!!!!

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fragilem17.MirrorsAndPortals
{
    [ExecuteInEditMode]
    public class MirrorSurface : MonoBehaviour
    {
        [Tooltip("The source material, disable and re-enable this component if you make changes to the material")]
        public Material Material;

        [Tooltip("When the camera is further from this distance, the surface stops updating it's texture.")]
        [MinAttribute(0)]
        public float maxRenderingDistance = 5f;

        [Tooltip("The % of maxRenderingDistance over which the mirror starts to darkens.")]
        [Range(0, 1)]
        public float fadeDistance = 0.5f;

        [Tooltip("How much reflection is allowed to blend in the color when you're closer than maxRenderingDistance-fadeDistance.")]
        [Range(0, 1)]
        public float maxBlend = 1f;

        public Color FadeColor = Color.black;

        [Space(10)]

        [Tooltip("When enabled each recursion can be used to darken the reflection, disabled the fadeDistance will be used to darken.")]
        public bool useRecursiveDarkening = true;

        public AnimationCurve recursiveDarkeningCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Other")]

        public float clippingPlaneOffset = 0.0f;

        public MeshRenderer MyMeshRenderer;
        public Transform MyForwardTransform;
        private Material _material;
        private MirrorRenderer _myRenderer;
        private MeshFilter _myMeshFilter;
        private Color _oldFadeColor = Color.black;
        private Material _oldMaterial;
        private bool _wasToFar = false;

        private bool _isSelectedInEditor = false;

        private Vector3[] _portalBounds;
        private Vector3[] _frustumCorners = new Vector3[4];
        private Vector3[] _fustrumBoundsOnPlane = new Vector3[4];
        private Vector3[] _shrinkToVectorArray = new Vector3[4];
        private Plane _plane;

        private void OnEnable()
        {
#if UNITY_EDITOR
            var flags = GameObjectUtility.GetStaticEditorFlags(gameObject);
            flags &= ~StaticEditorFlags.BatchingStatic;
            GameObjectUtility.SetStaticEditorFlags(gameObject, flags);
#endif

            if (MyForwardTransform == null)
            {
                MyForwardTransform = GetComponent<Transform>();
            }
            if (MyMeshRenderer == null)
            {
                MyMeshRenderer = GetComponent<MeshRenderer>();
            }
            if (_myMeshFilter == null)
            {
                _myMeshFilter = GetComponent<MeshFilter>();
            }

            _wasToFar = false;

            if (!Material && MyMeshRenderer)
            {
                Material = MyMeshRenderer.sharedMaterial;
            }

            if (MyMeshRenderer && Material)
            {
                _oldMaterial = Material;

                if (_isSelectedInEditor)
                {
                    Material.SetColor("_FadeColor", FadeColor);
                    MyMeshRenderer.sharedMaterial = Material;
                    _material = Material;
                }
                else
                {
                    _material = new Material(Material);
                    _material.name += " (for " + gameObject.name + ")";
                    _material.SetColor("_FadeColor", FadeColor);
                    MyMeshRenderer.material = _material;
                }
            }

#if UNITY_EDITOR
            Selection.selectionChanged -= OnSelectionChange;
            Selection.selectionChanged += OnSelectionChange;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            Selection.selectionChanged -= OnSelectionChange;
#endif
            if (_material != Material)
            {
                DestroyImmediate(_material, true);
            }
            if (MyMeshRenderer)
            {
                MyMeshRenderer.material = Material;
            }
        }

#if UNITY_EDITOR
        private void OnDestroy()
        {
            _isSelectedInEditor = false;
            Selection.selectionChanged -= OnSelectionChange;
        }
#endif

        internal Bounds GetBounds()
        {
            return MyMeshRenderer.bounds;
        }

        public void UpdatePositionsInMaterial(Vector3 position, Vector3 direction)
        {
            if (_material.HasProperty("_WorldPos"))
            {
                _material.SetVector("_WorldPos", position);
                _material.SetVector("_WorldDir", direction);
            }
        }

        public bool VisibleFromCamera(Camera renderCamera, bool ignoreDistance = true)
        {
            if (!enabled || !MyMeshRenderer || !_material || !gameObject.activeInHierarchy)
            {
                return false;
            }

            if (!MyMeshRenderer.isVisible)
            {
                return false;
            }

            Vector3 forward = -1 * MyForwardTransform.forward;
            Vector3 toOther = renderCamera.transform.position - MyForwardTransform.position;

            if (Vector3.Dot(forward, toOther) < 0)
            {
                if (!_wasToFar)
                {
                    _wasToFar = true;

                    if (_material.HasProperty("_DistanceBlend"))
                    {
                        _material.SetFloat("_DistanceBlend", 0);
                    }
                }

                return false;
            }
            else
            {
                if (_wasToFar)
                {
                    _wasToFar = false;
                }
            }

            if (!ignoreDistance)
            {
                bool toFar = Vector3.Distance(ClosestPoint(renderCamera.transform.position), renderCamera.transform.position) > maxRenderingDistance;
                if (toFar && !_wasToFar)
                {
                    _wasToFar = true;

                    if (_material.HasProperty("_DistanceBlend"))
                    {
                        _material.SetFloat("_DistanceBlend", 0);
                    }
                }
                if (!toFar && _wasToFar)
                {
                    _wasToFar = false;
                }

                if (toFar)
                {
                    return false;
                }
            }

            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(renderCamera);
            bool inBounds = GeometryUtility.TestPlanesAABB(planes, MyMeshRenderer.bounds);

            return inBounds;
        }

        public bool ShouldRenderBasedOnDistance(Camera renderCamera)
        {
            if (!enabled)
            {
                return false;
            }

            if (!_material)
            {
                return false;
            }

            bool toFar = Vector3.Distance(ClosestPoint(renderCamera.transform.position), renderCamera.transform.position) > maxRenderingDistance;

            if (toFar && !_wasToFar)
            {
                _wasToFar = true;

                if (_material.HasProperty("_DistanceBlend"))
                {
                    _material.SetFloat("_DistanceBlend", 0);
                }
            }

            if (!toFar && _wasToFar)
            {
                _wasToFar = false;
            }

            return !toFar;
        }

        public Vector3 ClosestPoint(Vector3 toPos)
        {
            Vector3 p = MyMeshRenderer.bounds.ClosestPoint(toPos);
            return p;
        }

        public void UpdateMaterial(Camera.StereoscopicEye eye = Camera.StereoscopicEye.Left, RenderTexture texture = null, MirrorRenderer myRenderer = null, int depth = 1, float distance = 0)
        {
            if (MyMeshRenderer && _material)
            {
                _myRenderer = myRenderer;
                Material m = _material;

                if (depth >= _myRenderer.recursions + 1)
                {
                    if (m.HasProperty("_DistanceBlend"))
                    {
                        m.SetFloat("_DistanceBlend", 0);
                    }
                    return;
                }

                if (m.HasProperty("_ForceEye"))
                {
                    m.SetInt("_ForceEye", eye == Camera.StereoscopicEye.Left ? 0 : 1);
                }

                if (eye == Camera.StereoscopicEye.Left && m.HasProperty("_TexLeft") && texture != null)
                {
                    m.SetTexture("_TexLeft", texture);
                }

                if (eye == Camera.StereoscopicEye.Right && XRSettings.enabled && m.HasProperty("_TexRight") && texture != null)
                {
                    m.SetTexture("_TexRight", texture);
                }

                if (depth != -1)
                {
                    float blend;
                    distance = distance - (maxRenderingDistance - (maxRenderingDistance * fadeDistance));
                    blend = Mathf.Clamp(1 - (distance / (maxRenderingDistance * fadeDistance)), 0, 1) * maxBlend;

                    if (useRecursiveDarkening && depth > 1)
                    {
                        float recusiveDarkening = 1 - (((float)depth - 1) / ((float)myRenderer.recursions));
                        recusiveDarkening = recursiveDarkeningCurve.Evaluate(recusiveDarkening);
                        blend = recusiveDarkening;
                    }

                    if (m.HasProperty("_DistanceBlend"))
                    {
                        m.SetFloat("_DistanceBlend", blend);
                    }
                }
            }
        }

#if UNITY_EDITOR
        void OnSelectionChange()
        {
            if (gameObject == Selection.activeGameObject)
            {
                _isSelectedInEditor = true;

                if (Material != null)
                {
                    Material.SetColor("_FadeColor", FadeColor);
                    MyMeshRenderer.sharedMaterial = Material;
                    _material = Material;
                }
            }
            else if (_isSelectedInEditor)
            {
                _isSelectedInEditor = false;

                OnDisable();
                OnEnable();

                if (_myRenderer != null)
                {
                    _myRenderer.SurfaceGotDeselectedInEditor();
                }
            }
        }

        public void RefreshMaterialInEditor()
        {
            OnDisable();
            OnEnable();
        }

        private void Update()
        {
            _plane = new Plane(-MyForwardTransform.forward, MyForwardTransform.position);

            if (!FadeColor.Equals(_oldFadeColor))
            {
                if (_material)
                { 
                    _material.SetColor("_FadeColor", FadeColor);
                }
                _oldFadeColor = FadeColor;
            }

            if (_oldMaterial != Material)
            {
                _material = Material;
                RefreshMaterialInEditor();
            }
        }
#endif

#if !UNITY_EDITOR
        private void Update()
        {
            _plane = new Plane(-MyForwardTransform.forward, MyForwardTransform.position);
        }
#endif

        public void TurnOffForceEye()
        {
            if (_material && _material.HasProperty("_ForceEye"))
            {
                _material.SetInt("_ForceEye", -1);
            }
        }

        public void ForceLeftEye()
        {
            if (_material && _material.HasProperty("_ForceEye"))
            {
                _material.SetInt("_ForceEye", 0);
            }
        }

        public Vector3[] ShrinkPointsToBounds(Camera reflectionCamera, float distanceToPlane, Camera.MonoOrStereoscopicEye eye)
        {
            if (_myRenderer == null)
            {
                return null;
            }

            _portalBounds = GetPortalBounds();

            bool allPointsFound = GetFustrumBoundsOnPlane(reflectionCamera, eye, distanceToPlane, ref _fustrumBoundsOnPlane);

            for (int x = 0; x < 4; x++)
            {
                _shrinkToVectorArray[x] = _portalBounds[x];

                _fustrumBoundsOnPlane[x] = RotatePointAroundPivot(_fustrumBoundsOnPlane[x], transform.position, Quaternion.Inverse(MyForwardTransform.rotation));
                _portalBounds[x] = RotatePointAroundPivot(_portalBounds[x], transform.position, Quaternion.Inverse(MyForwardTransform.rotation));
            }

            Vector2[] rect1 = new Vector2[4];
            Vector2[] rect2 = new Vector2[4];
            for (int x = 0; x < 4; x++)
            {
                rect1[x] = new Vector2(_fustrumBoundsOnPlane[x].x, _fustrumBoundsOnPlane[x].y);
                rect2[x] = new Vector2(_portalBounds[x].x, _portalBounds[x].y);
            }

            Rect r1 = CreateRectFromPoints(rect1);
            Rect r2 = CreateRectFromPoints(rect2);
            Rect rectOut;

            if (RectIntersects(r1, r2, out rectOut))
            {
                _shrinkToVectorArray[0] = new Vector3(rectOut.center.x - (rectOut.width / 2f), rectOut.center.y - (rectOut.height / 2f), _fustrumBoundsOnPlane[0].z);
                _shrinkToVectorArray[1] = new Vector3(rectOut.center.x + (rectOut.width / 2f), rectOut.center.y - (rectOut.height / 2f), _fustrumBoundsOnPlane[0].z);
                _shrinkToVectorArray[2] = new Vector3(rectOut.center.x - (rectOut.width / 2f), rectOut.center.y + (rectOut.height / 2f), _fustrumBoundsOnPlane[0].z);
                _shrinkToVectorArray[3] = new Vector3(rectOut.center.x + (rectOut.width / 2f), rectOut.center.y + (rectOut.height / 2f), _fustrumBoundsOnPlane[0].z);

                for (int x = 0; x < 4; x++)
                {
                    _shrinkToVectorArray[x] = RotatePointAroundPivot(_shrinkToVectorArray[x], transform.position, MyForwardTransform.rotation);
                    _shrinkToVectorArray[x] = _shrinkToVectorArray[x] + ((_shrinkToVectorArray[x] - reflectionCamera.transform.position) * 0.025f);
                }
            }

            return _shrinkToVectorArray;
        }

        public Vector3[] GetPortalBounds()
        {
            Bounds b = _myMeshFilter.sharedMesh.bounds;

            Vector3 boundsCenterOffset = Vector3.zero - b.center;
            b.center = transform.position - boundsCenterOffset;

            Vector3 bottomLeft = b.min;
            Vector3 topRight = b.max;
            Vector3 topLeft = new Vector3(b.min.x, b.max.y, b.max.z);
            Vector3 bottomRight = new Vector3(b.max.x, b.min.y, b.min.z);

            float scaleLarger = 1.0f;

            bottomLeft = ScaleAroundPivot(bottomLeft, transform.position, transform.lossyScale * scaleLarger);
            topRight = ScaleAroundPivot(topRight, transform.position, transform.lossyScale * scaleLarger);
            topLeft = ScaleAroundPivot(topLeft, transform.position, transform.lossyScale * scaleLarger);
            bottomRight = ScaleAroundPivot(bottomRight, transform.position, transform.lossyScale * scaleLarger);

            bottomLeft = RotatePointAroundPivot(bottomLeft, transform.position, transform.rotation);
            topRight = RotatePointAroundPivot(topRight, transform.position, transform.rotation);
            topLeft = RotatePointAroundPivot(topLeft, transform.position, transform.rotation);
            bottomRight = RotatePointAroundPivot(bottomRight, transform.position, transform.rotation);

            Vector3[] points = new Vector3[4];
            points[0] = bottomLeft;
            points[1] = bottomRight;
            points[2] = topLeft;
            points[3] = topRight;

            return points;
        }

        private bool GetFustrumBoundsOnPlane(Camera reflectionCamera, Camera.MonoOrStereoscopicEye eye, float distanceToPlane, ref Vector3[] positionsOnPlane)
        {
            float extendFrustum = 0;

            reflectionCamera.CalculateFrustumCorners(new Rect(-extendFrustum, -extendFrustum, 1f + (2 * extendFrustum), 1f + (2 * extendFrustum)), 1, eye, _frustumCorners);

            bool allSucceeded = true;
            for (int i = 0; i < 4; i++)
            {
                _frustumCorners[i] = reflectionCamera.transform.TransformPoint(_frustumCorners[i]);

                if (!ProjectPointOnPlane(reflectionCamera.transform.position, _frustumCorners[i], ref positionsOnPlane[i]))
                {
                    positionsOnPlane[i] = reflectionCamera.transform.position + ((_frustumCorners[i] - reflectionCamera.transform.position) * 25f);
                    positionsOnPlane[i] = _plane.ClosestPointOnPlane(positionsOnPlane[i]);
                    allSucceeded = false;
                }
            }
            return allSucceeded;
        }

        private bool ProjectPointOnPlane(Vector3 origin, Vector3 target, ref Vector3 posOnPlane)
        {
            Ray r = new Ray(origin, (target - origin));
            float distance = 0;
            if (_plane.Raycast(r, out distance))
            {
                posOnPlane = r.origin + (r.direction.normalized * (distance));
                return true;
            }
            return false;
        }

        public Vector3 RotatePointAroundPivot(Vector3 point, Vector3 pivotPosition, Quaternion rotation)
        {
            return pivotPosition + (rotation * (point - pivotPosition));
        }

        public Vector3 ScaleAroundPivot(Vector3 target, Vector3 pivot, Vector3 newScale)
        {
            Vector3 dir = (target - pivot);
            dir.Scale(newScale);
            return pivot + dir;
        }

        private static Rect CreateRectFromPoints(Vector2[] points)
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            foreach (Vector2 point in points)
            {
                if (point.x < minX)
                {
                    minX = point.x;
                }
                if (point.y < minY)
                {
                    minY = point.y;
                }
                if (point.x > maxX)
                {
                    maxX = point.x;
                }
                if (point.y > maxY)
                {
                    maxY = point.y;
                }
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private bool RectIntersects(Rect r1, Rect r2, out Rect area)
        {
            area = new Rect();

            if (r2.Overlaps(r1))
            {
                float x1 = Mathf.Min(r1.xMax, r2.xMax);
                float x2 = Mathf.Max(r1.xMin, r2.xMin);
                float y1 = Mathf.Min(r1.yMax, r2.yMax);
                float y2 = Mathf.Max(r1.yMin, r2.yMin);
                area.x = Mathf.Min(x1, x2);
                area.y = Mathf.Min(y1, y2);
                area.width = Mathf.Max(0.0f, x1 - x2);
                area.height = Mathf.Max(0.0f, y1 - y2);

                return true;
            }

            return false;
        }
    }
}