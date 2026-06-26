using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class VRC_ExpressionPreview : EditorWindow
{
    public static VRC_ExpressionPreview Instance { get; private set; }

    private PreviewRenderUtility previewUtility;
    private GameObject previewDummy;
    private GameObject lastRootObject;

    private Vector3 cameraPivot = new Vector3(0, 1.5f, 0);
    private Vector3 cameraEuler = new Vector3(0, 180, 0);
    private float cameraDistance = 0.40f;

    private Vector3 savedHeadLocalPosition = new Vector3(0, 1.4f, 0);
    private AnimationClip cachedTestBaseClip;
    private Dictionary<string, Dictionary<string, float>> testBaseClipCache = new Dictionary<string, Dictionary<string, float>>();

    private struct MeshPair
    {
        public SkinnedMeshRenderer originalSmr;
        public SkinnedMeshRenderer dummySmr;
        public string relativePath;
        public Dictionary<string, int> shapeIndexMap;
    }
    private List<MeshPair> cachedMeshPairs = new List<MeshPair>();

    private struct ReplicaLightPair
    {
        public UnityEngine.Light original;
        public UnityEngine.Light replica;
        public Vector3 lastPosition;
        public Quaternion lastRotation;
        public LightType lastType;
        public Color lastColor;
        public float lastIntensity;
        public float lastRange;
        public float lastSpotAngle;
    }

    private struct BuiltinLightSync
    {
        public UnityEngine.Light original;
        public int builtinIndex;
    }

    private List<ReplicaLightPair> activeReplicaLights = new List<ReplicaLightPair>();
    private List<BuiltinLightSync> activeBuiltinSyncs = new List<BuiltinLightSync>();
    private Dictionary<int, bool> lastLightSnapshots = new Dictionary<int, bool>();
    private GameObject previewLightsRoot;

    private double lastLightCheckTime;
    private Color cachedAmbientColor = Color.gray;

    private bool replicaLightsNeedRebuild = false;
    private bool isAvatarChanged = true;
    private bool isDirty = true;

    private enum CameraTool { Pan, Orbit, Zoom }
    private CameraTool currentCameraTool = CameraTool.Orbit;

    // データ復元用（アニメ書き戻し用）
    private Dictionary<string, Dictionary<string, float>> snapshotClipValues = new Dictionary<string, Dictionary<string, float>>();
    private Dictionary<string, bool> snapshotClipActiveValues = new Dictionary<string, bool>();

    // プレビュー比較用（見た目用）
    private Dictionary<string, Dictionary<int, float>> snapshotSMRWeights = new Dictionary<string, Dictionary<int, float>>();
    private Dictionary<string, bool> snapshotObjectActives = new Dictionary<string, bool>();

    // 変更されたシェイプキーだけを追跡し、無駄なリセット処理を回避する
    private Dictionary<SkinnedMeshRenderer, HashSet<int>> modifiedBlendShapes = new Dictionary<SkinnedMeshRenderer, HashSet<int>>();

    private bool isComparing = false;
    private bool isMuted = false;

    private Vector2 uiScrollPos;
    private static readonly string[] CAMERA_TOOL_NAMES = new string[] { "平行", "回転", "ズーム" };

    private GUIContent resizeIconContent;
    private GUILayoutOption optW28, optW45, optW65;
    private GUILayoutOption optH20, optH26, optExpandTrue;
    private GUIStyle centerLockStyle;

    private string pendingCapturePath = null;

    private void OnEnable()
    {
        Instance = this;
        isDirty = true;
        EditorApplication.update += OnEditorUpdate;
        EditorApplication.hierarchyChanged += OnHierarchyChanged;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.hierarchyChanged -= OnHierarchyChanged;

        CleanupPreview();
        if (previewUtility != null) { previewUtility.Cleanup(); previewUtility = null; }
    }

    private void CleanupPreview()
    {
        if (previewDummy != null) { DestroyImmediate(previewDummy); previewDummy = null; }
        CleanupReplicaLights();
        cachedMeshPairs.Clear();
        modifiedBlendShapes.Clear();
    }

    private void CleanupReplicaLights()
    {
        if (previewLightsRoot != null) { DestroyImmediate(previewLightsRoot); previewLightsRoot = null; }
        activeReplicaLights.Clear();
        activeBuiltinSyncs.Clear();
        lastLightSnapshots.Clear();
        replicaLightsNeedRebuild = false;
    }

    private void OnDestroy() { CleanupPreview(); }

    private void OnHierarchyChanged()
    {
        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null || editor.IsDraggingSlider()) return;
        replicaLightsNeedRebuild = true;
    }

    private void OnEditorUpdate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) { this.Close(); return; }

        double timeSinceStartup = EditorApplication.timeSinceStartup;
        if (timeSinceStartup - lastLightCheckTime < 0.1) return;
        lastLightCheckTime = timeSinceStartup;

        if (editor.IsDraggingSlider()) return;

        if (replicaLightsNeedRebuild)
        {
            RebuildReplicaLights(editor);
            Repaint();
        }
        else
        {
            if (SyncReplicaLightParameters()) Repaint();
        }
    }

    private void SetDummyWeight(SkinnedMeshRenderer smr, int index, float value)
    {
        if (smr == null) return;
        smr.SetBlendShapeWeight(index, value);

        if (!modifiedBlendShapes.ContainsKey(smr)) modifiedBlendShapes[smr] = new HashSet<int>();
        modifiedBlendShapes[smr].Add(index);
    }

    private void ResetModifiedBlendShapes()
    {
        foreach (var kvp in modifiedBlendShapes)
        {
            if (kvp.Key != null)
            {
                foreach (int index in kvp.Value) kvp.Key.SetBlendShapeWeight(index, 0f);
            }
        }
        modifiedBlendShapes.Clear();
    }

    private List<UnityEngine.Light> GetOnlySceneLights()
    {
        var allLights = UnityEngine.Object.FindObjectsOfType<UnityEngine.Light>();
        var sceneLights = new List<UnityEngine.Light>();

        int count = allLights.Length;
        for (int i = 0; i < count; i++)
        {
            UnityEngine.Light l = allLights[i];
            if (l == null) continue;
            if (l.name.StartsWith("Replica_")) continue;
            sceneLights.Add(l);
        }
        return sceneLights;
    }

    private void RebuildReplicaLights(VRC_ExpressionEditor editor)
    {
        CleanupReplicaLights();

        if (editor == null || editor.rootObject == null || previewUtility == null) return;

        previewLightsRoot = new GameObject("_PreviewLightsRoot");
        previewLightsRoot.hideFlags = HideFlags.HideAndDontSave;

        var currentLights = GetOnlySceneLights();
        Vector3 avatarPos = editor.rootObject.transform.position;

        var dirLights = new List<UnityEngine.Light>();
        var otherLights = new List<UnityEngine.Light>();

        int lightsCount = currentLights.Count;
        for (int i = 0; i < lightsCount; i++)
        {
            UnityEngine.Light l = currentLights[i];
            if (l == null || !l.gameObject.activeInHierarchy || l.lightmapBakeType == LightmapBakeType.Baked) continue;

            if (l.type == LightType.Directional) dirLights.Add(l);
            else
            {
                float distance = Vector3.Distance(l.transform.position, avatarPos);
                if (distance <= l.range * 1.2f) otherLights.Add(l);
            }
        }

        dirLights.Sort((a, b) => b.intensity.CompareTo(a.intensity));

        for (int i = 0; i < 2; i++)
        {
            if (i < dirLights.Count) activeBuiltinSyncs.Add(new BuiltinLightSync { original = dirLights[i], builtinIndex = i });
        }

        int otherCount = otherLights.Count;
        for (int i = 0; i < otherCount; i++)
        {
            UnityEngine.Light srcLight = otherLights[i];
            GameObject lightGo = new GameObject("Replica_" + srcLight.name);
            lightGo.transform.SetParent(previewLightsRoot.transform);
            lightGo.transform.position = srcLight.transform.position - avatarPos;
            lightGo.transform.rotation = srcLight.transform.rotation;

            UnityEngine.Light destLight = lightGo.AddComponent<UnityEngine.Light>();
            CopyLightParameters(srcLight, destLight);
            destLight.shadows = LightShadows.None;

            activeReplicaLights.Add(new ReplicaLightPair
            {
                original = srcLight,
                replica = destLight,
                lastPosition = srcLight.transform.position,
                lastRotation = srcLight.transform.rotation,
                lastType = srcLight.type,
                lastColor = srcLight.color,
                lastIntensity = srcLight.intensity,
                lastRange = srcLight.range,
                lastSpotAngle = srcLight.spotAngle
            });
        }

        lastLightSnapshots.Clear();
        for (int i = 0; i < lightsCount; i++)
        {
            UnityEngine.Light l = currentLights[i];
            if (l != null) lastLightSnapshots[l.GetInstanceID()] = l.gameObject.activeInHierarchy;
        }

        replicaLightsNeedRebuild = false;
    }

    private bool SyncReplicaLightParameters()
    {
        bool anyChanged = false;
        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null || editor.rootObject == null) return false;

        Vector3 avatarPos = editor.rootObject.transform.position;
        int count = activeReplicaLights.Count;
        for (int i = 0; i < count; i++)
        {
            var pair = activeReplicaLights[i];
            if (pair.original == null || pair.replica == null) continue;

            UnityEngine.Light src = pair.original;
            UnityEngine.Light dest = pair.replica;

            if (src.transform.position != pair.lastPosition || src.transform.rotation != pair.lastRotation)
            {
                dest.transform.position = src.transform.position - avatarPos;
                dest.transform.rotation = src.transform.rotation;
                pair.lastPosition = src.transform.position;
                pair.lastRotation = src.transform.rotation;
                anyChanged = true;
            }

            bool paramChanged = false;
            if (src.type != pair.lastType) { dest.type = src.type; pair.lastType = src.type; paramChanged = true; }
            if (src.color != pair.lastColor) { dest.color = src.color; pair.lastColor = src.color; paramChanged = true; }
            if (!Mathf.Approximately(src.intensity, pair.lastIntensity)) { dest.intensity = src.intensity; pair.lastIntensity = src.intensity; paramChanged = true; }
            if (!Mathf.Approximately(src.range, pair.lastRange)) { dest.range = src.range; pair.lastRange = src.range; paramChanged = true; }
            if (!Mathf.Approximately(src.spotAngle, pair.lastSpotAngle)) { dest.spotAngle = src.spotAngle; pair.lastSpotAngle = src.spotAngle; paramChanged = true; }

            if (paramChanged) { activeReplicaLights[i] = pair; anyChanged = true; }
        }
        return anyChanged;
    }

    private void SyncBuiltinLightsImmediate()
    {
        if (previewUtility == null) return;
        cachedAmbientColor = RenderSettings.ambientLight;
        int builtinCount = activeBuiltinSyncs.Count;

        for (int i = 0; i < 2; i++)
        {
            var dest = previewUtility.lights[i];
            if (i < builtinCount)
            {
                var sync = activeBuiltinSyncs[i];
                if (sync.original != null)
                {
                    dest.enabled = true;
                    dest.transform.rotation = sync.original.transform.rotation;
                    dest.color = sync.original.color;
                    dest.intensity = sync.original.intensity;
                    dest.shadows = sync.original.shadows;
                    dest.shadowStrength = sync.original.shadowStrength;
                    dest.shadowBias = sync.original.shadowBias;
                    dest.shadowNormalBias = sync.original.shadowNormalBias;
                }
            }
            else
            {
                if (i == 0) { dest.enabled = false; dest.intensity = 0f; }
                else
                {
                    dest.enabled = true; dest.type = LightType.Directional;
                    dest.color = cachedAmbientColor; dest.intensity = 1.0f; dest.shadows = LightShadows.None;
                }
            }
        }
    }

    private void CopyLightParameters(UnityEngine.Light src, UnityEngine.Light dest)
    {
        dest.type = src.type; dest.color = src.color; dest.intensity = src.intensity;
        dest.range = src.range; dest.spotAngle = src.spotAngle; dest.shadows = src.shadows;
        dest.shadowStrength = src.shadowStrength; dest.shadowBias = src.shadowBias;
        dest.shadowNormalBias = src.shadowNormalBias; dest.shadowNearPlane = src.shadowNearPlane;
    }

    public void UpdateSingleBlendShapeImmediate(string relativePath, string shapeName, float value)
    {
        if (previewDummy == null || isComparing || isMuted) return;
        int count = cachedMeshPairs.Count;
        for (int i = 0; i < count; i++)
        {
            var pair = cachedMeshPairs[i];
            if (pair.relativePath == relativePath)
            {
                if (pair.shapeIndexMap.TryGetValue(shapeName, out int index))
                {
                    SetDummyWeight(pair.dummySmr, index, value);
                }
                break;
            }
        }
    }

    public void UpdateSingleObjectActiveImmediate(string path, bool isActive)
    {
        if (previewDummy == null || isComparing || isMuted) return;
        Transform target = string.IsNullOrEmpty(path) ? previewDummy.transform : previewDummy.transform.Find(path);
        if (target != null) target.gameObject.SetActive(isActive);
    }

    public void MarkPreviewDirty() { isDirty = true; }
    public void ForceRebuildDummy() { CleanupPreview(); MarkPreviewDirty(); Repaint(); }
    public GameObject GetPreviewDummy() { return previewDummy; }

    public void FindAndCacheSceneLight()
    {
        cachedAmbientColor = RenderSettings.ambientLight;
    }

    private void BuildMeshPairsCache(VRC_ExpressionEditor editor)
    {
        cachedMeshPairs.Clear();
        if (editor.rootObject == null || previewDummy == null) return;

        var origSmrs = editor.rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var origSmr in origSmrs)
        {
            var dummySmr = FindCorrespondingSMR(editor, previewDummy, origSmr);
            if (dummySmr != null && dummySmr.sharedMesh != null)
            {
                var shapeMap = new Dictionary<string, int>();
                for (int i = 0; i < dummySmr.sharedMesh.blendShapeCount; i++) shapeMap[dummySmr.sharedMesh.GetBlendShapeName(i)] = i;
                cachedMeshPairs.Add(new MeshPair { originalSmr = origSmr, dummySmr = dummySmr, relativePath = editor.GetRelativePath(origSmr.gameObject), shapeIndexMap = shapeMap });
            }
        }
    }

    public void ResetPreviousAnimBlendShapes(Dictionary<string, Dictionary<string, float>> previousClipValues)
    {
        if (previewDummy == null || previousClipValues == null) return;
        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) return;

        int count = cachedMeshPairs.Count;
        foreach (var pathKvp in previousClipValues)
        {
            for (int i = 0; i < count; i++)
            {
                var pair = cachedMeshPairs[i];
                if (pair.relativePath == pathKvp.Key)
                {
                    editor.baseShapeKeyBackup.TryGetValue(pathKvp.Key, out var baseDict);
                    foreach (var shapeKvp in pathKvp.Value)
                    {
                        if (pair.shapeIndexMap.TryGetValue(shapeKvp.Key, out int index))
                        {
                            float defaultVal = 0f;
                            if (baseDict != null && baseDict.TryGetValue(shapeKvp.Key, out float cachedVal)) defaultVal = cachedVal;
                            SetDummyWeight(pair.dummySmr, index, defaultVal);
                        }
                    }
                    break;
                }
            }
        }
    }

    private void InitializeGUIStylesIfNeeded()
    {
        if (resizeIconContent == null || optExpandTrue == null)
        {
            resizeIconContent = EditorGUIUtility.IconContent("d_ViewToolZoom");
            resizeIconContent.tooltip = "ウィンドウサイズを自動調整します";

            optW28 = GUILayout.Width(28); optW45 = GUILayout.Width(45); optW65 = GUILayout.Width(65);
            optH20 = GUILayout.Height(20); optH26 = GUILayout.Height(26); optExpandTrue = GUILayout.ExpandWidth(true);

            centerLockStyle = new GUIStyle(EditorStyles.boldLabel);
            centerLockStyle.alignment = TextAnchor.MiddleCenter;
            centerLockStyle.fontSize = 14;
            centerLockStyle.wordWrap = true;
        }
    }

    private void OnGUI()
    {
        InitializeGUIStylesIfNeeded();

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("▶ 再生モード中です\n安全のためプレビューをロックしています", centerLockStyle);
            GUILayout.FlexibleSpace();
            return;
        }

        InitializePreviewUtilityIfNeeded();

        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null || editor.rootObject == null) { EditorGUILayout.HelpBox("アバターを設定してください。", MessageType.Info); CleanupPreview(); return; }
        if (editor.rootObject != lastRootObject) { CleanupPreview(); lastRootObject = editor.rootObject; isAvatarChanged = true; FindAndCacheSceneLight(); MarkPreviewDirty(); }

        float cameraHeight = Mathf.Max(150f, position.height - 330f);
        Rect rect = new Rect(10, 10, position.width - 20, cameraHeight);
        EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f, 1f));

        HandleCameraControls(rect);

        if (Event.current.type == EventType.Repaint)
        {
            bool isDraggingNow = editor.IsDraggingSlider();

            if (previewDummy == null || isDirty)
            {
                if (isComparing)
                {
                    EnsureDummyExists(editor);
                    ApplySnapshotDirectly();
                }
                else
                {
                    SetupAndPoseDummy(editor);
                    if (isMuted) ApplyMuteExpressionDirectly(editor);
                }

                UpdatePreviewCamera();
                isDirty = false;
            }
            else
            {
                UpdatePreviewCamera();
            }

            previewUtility.BeginPreview(rect, GUIStyle.none);
            SyncBuiltinLightsImmediate();

            if (previewDummy != null)
            {
                previewUtility.AddSingleGO(previewDummy);
                if (previewLightsRoot != null) previewUtility.AddSingleGO(previewLightsRoot);

                previewUtility.camera.Render();

                if (!string.IsNullOrEmpty(pendingCapturePath))
                {
                    CaptureCurrentPreviewInsideRenderLoop(pendingCapturePath);
                    pendingCapturePath = null;
                }
            }
            Texture rTex = previewUtility.EndPreview();
            if (rTex != null) GUI.DrawTexture(rect, rTex, ScaleMode.StretchToFill, false);
        }

        float uiTop = cameraHeight + 20f;
        float uiHeight = position.height - uiTop - 10f;

        if (uiHeight > 30f)
        {
            GUILayout.BeginArea(new Rect(10, uiTop, position.width - 20, uiHeight));
            uiScrollPos = EditorGUILayout.BeginScrollView(uiScrollPos);

            DrawPreviewToolbar(editor);
            DrawBlendingTestPanel(editor);
            DrawSnapshotPanel(editor);
            DrawIconCapturePanel(editor);

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }

    private void EnsureDummyExists(VRC_ExpressionEditor editor)
    {
        if (previewDummy == null)
        {
            Animator origAnim = editor.rootObject.GetComponent<Animator>();
            if (origAnim != null)
            {
                Transform origHead = origAnim.GetBoneTransform(HumanBodyBones.Head);
                if (origHead != null) savedHeadLocalPosition = editor.rootObject.transform.InverseTransformPoint(origHead.position);
            }

            previewDummy = Instantiate(editor.rootObject);
            previewDummy.name = editor.rootObject.name + "_PreviewDummy";
            previewDummy.hideFlags = HideFlags.HideAndDontSave;

            SyncTransformsAndActive(editor.rootObject.transform, previewDummy.transform, true);
            StripAllUnwantedComponents(previewDummy);

            previewDummy.transform.position = Vector3.zero;
            previewDummy.transform.rotation = Quaternion.identity;

            if (isAvatarChanged) { ResetCameraPivot(); isAvatarChanged = false; }
            FindAndCacheSceneLight();
            RebuildReplicaLights(editor);
            BuildMeshPairsCache(editor);
        }
    }

    private void SetupAndPoseDummy(VRC_ExpressionEditor editor)
    {
        EnsureDummyExists(editor);

        AnimationClip editingClip = null;
        if (editor.availableClips != null && editor.availableClips.Count > 0 && editor.selectedClipIndex < editor.availableClips.Count)
            editingClip = editor.availableClips[editor.selectedClipIndex];

        if (editor.testBaseClip != cachedTestBaseClip)
        {
            ResetPreviousAnimBlendShapes(testBaseClipCache);
            cachedTestBaseClip = editor.testBaseClip;
            UpdateTestBaseClipCache(editor.testBaseClip, editor);
        }

        ResetModifiedBlendShapes();

        int pairCount = cachedMeshPairs.Count;
        for (int i = 0; i < pairCount; i++)
        {
            var pair = cachedMeshPairs[i];
            var dummySmr = pair.dummySmr;
            string path = pair.relativePath;
            if (dummySmr == null || dummySmr.sharedMesh == null) continue;

            // 1. アバター本来の顔（ベース形状）を適用
            if (editor.baseShapeKeyBackup.TryGetValue(path, out var baseDict))
                foreach (var kvp in baseDict) if (pair.shapeIndexMap.TryGetValue(kvp.Key, out int index)) SetDummyWeight(dummySmr, index, kvp.Value);

            // 2. 干渉テスト用のアニメを適用
            if (editor.testBaseClip != null && testBaseClipCache.TryGetValue(path, out var baseAnimDict))
                foreach (var kvp in baseAnimDict) if (pair.shapeIndexMap.TryGetValue(kvp.Key, out int index)) SetDummyWeight(dummySmr, index, kvp.Value);

            // 3. 【3層構造：掲示板から直送】
            // 棚（warehouse）のパッケージ内にある 掲示板（currentValue）を直接人形に代入する
            if (editor.warehouse.TryGetValue(path, out var tracks))
            {
                foreach (var track in tracks)
                {
                    // もし、人形側にそのシェイプキーがあれば、数値を直接代入
                    if (pair.shapeIndexMap.TryGetValue(track.label, out int index))
                    {
                        SetDummyWeight(dummySmr, index, track.currentValue);
                    }
                }
            }
        }

        // 4. 【オリジナルの機能維持】テスト用シェイプキーの処理（ここもそのまま残します）
        if (editor.availableSmrs != null && editor.availableSmrs.Count > editor.selectedSmrIndex)
        {
            SkinnedMeshRenderer targetSmr = editor.availableSmrs[editor.selectedSmrIndex];
            string targetPath = editor.GetRelativePath(targetSmr.gameObject);

            MeshPair activePair = default; bool found = false;
            for (int i = 0; i < pairCount; i++)
            {
                if (cachedMeshPairs[i].relativePath == targetPath) { activePair = cachedMeshPairs[i]; found = true; break; }
            }

            if (found && activePair.dummySmr != null)
            {
                // ※【整理】currentExpressionValues による2重の上書き処理は完全に不要になったため削除しました。

                // テスト用のスライダー値だけを上書き反映
                if (editor.testShapeKeyIndex >= 0 && editor.testShapeKeyIndex < activePair.originalSmr.sharedMesh.blendShapeCount)
                {
                    string testShapeName = activePair.originalSmr.sharedMesh.GetBlendShapeName(editor.testShapeKeyIndex);
                    if (activePair.shapeIndexMap.TryGetValue(testShapeName, out int index))
                        SetDummyWeight(activePair.dummySmr, index, editor.testShapeKeyValue);
                }
            }
        }
    }

    private void UpdateTestBaseClipCache(AnimationClip clip, VRC_ExpressionEditor editor)
    {
        testBaseClipCache.Clear(); if (clip == null) return;
        var bindings = AnimationUtility.GetCurveBindings(clip);
        foreach (var binding in bindings)
        {
            if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape."))
            {
                string name = binding.propertyName.Replace("blendShape.", ""); AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null && curve.keys.Length > 0)
                {
                    if (!testBaseClipCache.ContainsKey(binding.path)) testBaseClipCache[binding.path] = new Dictionary<string, float>();
                    testBaseClipCache[binding.path][name] = curve.keys[0].value;
                }
            }
        }
    }

    private void StripAllUnwantedComponents(GameObject go)
    {
        var allComponents = go.GetComponentsInChildren<Component>(true);
        for (int i = allComponents.Length - 1; i >= 0; i--)
        {
            Component comp = allComponents[i]; if (comp == null) continue;
            if (comp is Transform || comp is SkinnedMeshRenderer || comp is MeshFilter || comp is MeshRenderer) continue;
            DestroyImmediate(comp);
        }
    }

    private void ResetCameraPivot()
    {
        cameraPivot = savedHeadLocalPosition + Vector3.up * 0.05f;
        cameraEuler = new Vector3(0, 180, 0);
        cameraDistance = 0.40f;
        MarkPreviewDirty();
    }

    private SkinnedMeshRenderer FindCorrespondingSMR(VRC_ExpressionEditor editor, GameObject parent, SkinnedMeshRenderer original)
    {
        if (original == null) return null; string path = editor.GetRelativePath(original.gameObject);
        if (string.IsNullOrEmpty(path)) return parent.GetComponent<SkinnedMeshRenderer>();
        Transform t = parent.transform.Find(path); return t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
    }

    private void SyncTransformsAndActive(Transform src, Transform dst, bool isRoot = true)
    {
        if (src == null || dst == null) return;
        dst.gameObject.SetActive(src.gameObject.activeSelf);
        if (isRoot) { dst.localPosition = Vector3.zero; dst.localRotation = Quaternion.identity; dst.localScale = src.localScale; }
        else { dst.localPosition = src.localPosition; dst.localRotation = src.localRotation; dst.localScale = src.localScale; }
        for (int i = 0; i < src.childCount && i < dst.childCount; i++) SyncTransformsAndActive(src.GetChild(i), dst.GetChild(i), false);
    }

    private void UpdatePreviewCamera()
    {
        Quaternion rot = Quaternion.Euler(cameraEuler.x, cameraEuler.y, 0); previewUtility.camera.transform.rotation = rot;
        previewUtility.camera.transform.position = cameraPivot + rot * new Vector3(0, 0, -cameraDistance);
    }

    private void HandleCameraControls(Rect rect)
    {
        Event e = Event.current; if (!rect.Contains(e.mousePosition)) return;
        if (e.type == EventType.MouseDrag)
        {
            MarkPreviewDirty();
            if (e.button == 1) { cameraEuler.y += e.delta.x * 0.5f; cameraEuler.x += e.delta.y * 0.5f; e.Use(); }
            else if (e.button == 0)
            {
                if (e.alt || currentCameraTool == CameraTool.Orbit) { cameraEuler.y += e.delta.x * 0.5f; cameraEuler.x += e.delta.y * 0.5f; }
                else if (currentCameraTool == CameraTool.Pan) { float f = cameraDistance * 0.002f; cameraPivot -= previewUtility.camera.transform.right * e.delta.x * f; cameraPivot += previewUtility.camera.transform.up * e.delta.y * f; }
                else if (currentCameraTool == CameraTool.Zoom) { cameraDistance += e.delta.y * 0.01f; }
            }
            else if (e.button == 2) { float f = cameraDistance * 0.002f; cameraPivot -= previewUtility.camera.transform.right * e.delta.x * f; cameraPivot += previewUtility.camera.transform.up * e.delta.y * f; }
            cameraEuler.x = Mathf.Clamp(cameraEuler.x, -80, 80); cameraDistance = Mathf.Max(0.05f, cameraDistance); e.Use();
        }
        else if (e.type == EventType.ScrollWheel) { MarkPreviewDirty(); cameraDistance += e.delta.y * 0.02f; cameraDistance = Mathf.Max(0.05f, cameraDistance); e.Use(); }
    }

    private void DrawPreviewToolbar(VRC_ExpressionEditor editor)
    {
        EditorGUILayout.BeginHorizontal(); GUILayout.Label("カメラ:", optW45);
        currentCameraTool = (CameraTool)GUILayout.Toolbar((int)currentCameraTool, CAMERA_TOOL_NAMES);
        if (GUILayout.Button("リセット", optW65)) ResetCameraPivot();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBlendingTestPanel(VRC_ExpressionEditor editor)
    {
        GUILayout.BeginVertical(GUI.skin.box); GUILayout.Label("表情干渉・ブレンドテスト", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();

        if (editor.testClipNamesCache != null && editor.testClipNamesCache.Length > 0)
        {
            int currentIdx = 0; if (editor.testBaseClip != null) { int foundIdx = editor.availableClips.IndexOf(editor.testBaseClip); if (foundIdx != -1) currentIdx = foundIdx + 1; }
            int newIdx = EditorGUILayout.Popup("下層テストアニメ", currentIdx, editor.testClipNamesCache);
            if (newIdx != currentIdx) { if (newIdx == 0) editor.testBaseClip = null; else editor.testBaseClip = editor.availableClips[newIdx - 1]; }
        }
        else { editor.testBaseClip = (AnimationClip)EditorGUILayout.ObjectField("下層テストアニメ", editor.testBaseClip, typeof(AnimationClip), false); }

        if (editor.testShapeNamesCache != null && editor.testShapeNamesCache.Length > 0)
        {
            editor.testShapeKeyIndex = EditorGUILayout.Popup("テストシェイプ", editor.testShapeKeyIndex, editor.testShapeNamesCache);
            editor.testShapeKeyValue = EditorGUILayout.Slider("テスト値", editor.testShapeKeyValue, 0f, 100f);
        }

        if (EditorGUI.EndChangeCheck()) { MarkPreviewDirty(); Repaint(); }
        GUILayout.EndVertical();
    }

    private void DrawSnapshotPanel(VRC_ExpressionEditor editor)
    {
        // ★追加1：タイムラインが再生中かどうかを確認する
        bool isPlaying = VRC_ExpressionTimeline.Instance != null && VRC_ExpressionTimeline.Instance.IsPlaying();

        // ★追加2：再生中なら「無効化（グレーアウト）」を開始
        EditorGUI.BeginDisabledGroup(isPlaying);

        // --- ここから既存のUI描画 ---
        // Vertical(GUI.skin.box) の戻り値を panelRect として受け取るように変更します
        Rect panelRect = EditorGUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("表情スナップショット (一時比較)", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("📸 一時保存", optH20)) TakeSnapshot(editor);

        Color prevBg = GUI.backgroundColor;
        if (isComparing) GUI.backgroundColor = new Color(0.9f, 0.9f, 1f);

        EditorGUI.BeginChangeCheck();
        isComparing = GUILayout.Toggle(isComparing, "👁️ 比較 (ON/OFF)", GUI.skin.button, optH20);
        if (EditorGUI.EndChangeCheck())
        {
            isDirty = true;
            if (isComparing) { EnsureDummyExists(editor); ApplySnapshotDirectly(); }
            else { SetupAndPoseDummy(editor); }
            Repaint();
        }
        GUI.backgroundColor = prevBg;

        Color prevBgMute = GUI.backgroundColor;
        if (isMuted) GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
        EditorGUI.BeginChangeCheck();
        isMuted = GUILayout.Toggle(isMuted, "🔇 ミュート", GUI.skin.button, optH20);
        if (EditorGUI.EndChangeCheck())
        {
            isDirty = true;
            if (isMuted) { EnsureDummyExists(editor); ApplyMuteExpressionDirectly(editor); }
            else { SetupAndPoseDummy(editor); }
            Repaint();
        }
        GUI.backgroundColor = prevBgMute;

        if (GUILayout.Button("⏪ 復元する", optH20))
            if (EditorUtility.DisplayDialog("スナップショット復元", "保存時点に書き戻しますか？", "復元", "キャンセル")) RestoreSnapshot(editor);

        EditorGUILayout.EndHorizontal();
        GUILayout.Label(snapshotClipValues.Count > 0 ? "※一時保存中..." : "※一時保存データはありません", EditorStyles.miniLabel);
        EditorGUILayout.EndVertical();

        // ★追加3：無効化を終了
        EditorGUI.EndDisabledGroup();
    }

    private void ApplyMuteExpressionDirectly(VRC_ExpressionEditor editor)
    {
        if (previewDummy == null || editor == null) return;

        int pairCount = cachedMeshPairs.Count;
        for (int i = 0; i < pairCount; i++)
        {
            var pair = cachedMeshPairs[i];
            var dummySmr = pair.dummySmr;
            string path = pair.relativePath;
            if (dummySmr == null || dummySmr.sharedMesh == null) continue;

            // ★【3層構造へ接続】古い辞書の代わりに、棚のパッケージ（warehouse）から動いている項目を特定する
            if (editor.warehouse.TryGetValue(path, out var tracks))
            {
                foreach (var track in tracks)
                {
                    if (pair.shapeIndexMap.TryGetValue(track.label, out int index))
                    {
                        float originalValue = 0f;
                        // アバター本来のデフォルトの数値を調べて、それを代入する（消音する）
                        if (editor.baseShapeKeyBackup.TryGetValue(path, out var baseDict))
                            baseDict.TryGetValue(track.label, out originalValue);

                        SetDummyWeight(dummySmr, index, originalValue);
                    }
                }
            }

            if (editor.availableSmrs != null && editor.availableSmrs.Count > editor.selectedSmrIndex)
            {
                if (path == editor.GetRelativePath(editor.availableSmrs[editor.selectedSmrIndex].gameObject))
                {
                    foreach (var kvp in editor.currentExpressionValues)
                    {
                        if (pair.shapeIndexMap.TryGetValue(kvp.Key, out int index))
                        {
                            float originalValue = 0f;
                            if (editor.baseShapeKeyBackup.TryGetValue(path, out var baseDict)) baseDict.TryGetValue(kvp.Key, out originalValue);
                            SetDummyWeight(dummySmr, index, originalValue);
                        }
                    }
                }
            }
        }

        foreach (var path in editor.activeObjectValues.Keys)
        {
            Transform t = string.IsNullOrEmpty(path) ? previewDummy.transform : previewDummy.transform.Find(path);
            if (t != null) t.gameObject.SetActive(false);
        }
    }

    private void ApplySnapshotDirectly()
    {
        if (previewDummy == null || snapshotSMRWeights.Count == 0) return;
        ResetModifiedBlendShapes();

        int pairCount = cachedMeshPairs.Count;
        for (int i = 0; i < pairCount; i++)
        {
            var pair = cachedMeshPairs[i];
            if (snapshotSMRWeights.TryGetValue(pair.relativePath, out var snapDict))
            {
                if (pair.dummySmr != null)
                {
                    foreach (var kvp in snapDict) SetDummyWeight(pair.dummySmr, kvp.Key, kvp.Value);
                }
            }
        }

        foreach (var kvp in snapshotObjectActives)
        {
            Transform t = string.IsNullOrEmpty(kvp.Key) ? previewDummy.transform : previewDummy.transform.Find(kvp.Key);
            if (t != null) t.gameObject.SetActive(kvp.Value);
        }
    }

    private void TakeSnapshot(VRC_ExpressionEditor editor)
    {
        snapshotClipValues.Clear();
        if (editor != null && editor.clipExpressionValues != null)
        {
            foreach (var kvp in editor.clipExpressionValues)
                snapshotClipValues[kvp.Key] = new Dictionary<string, float>(kvp.Value);
        }

        snapshotClipActiveValues.Clear();
        if (editor != null && editor.activeObjectValues != null)
        {
            foreach (var kvp in editor.activeObjectValues)
                snapshotClipActiveValues[kvp.Key] = kvp.Value;
        }

        snapshotSMRWeights.Clear();
        snapshotObjectActives.Clear();
        if (previewDummy != null)
        {
            int count = cachedMeshPairs.Count;
            for (int i = 0; i < count; i++)
            {
                var pair = cachedMeshPairs[i];
                if (pair.dummySmr == null || pair.dummySmr.sharedMesh == null) continue;

                var dict = new Dictionary<int, float>();
                if (modifiedBlendShapes.TryGetValue(pair.dummySmr, out var indices))
                {
                    foreach (int idx in indices) dict[idx] = pair.dummySmr.GetBlendShapeWeight(idx);
                }
                snapshotSMRWeights[pair.relativePath] = dict;
            }

            if (editor != null && editor.activeObjectValues != null)
            {
                foreach (var path in editor.activeObjectValues.Keys)
                {
                    Transform t = string.IsNullOrEmpty(path) ? previewDummy.transform : previewDummy.transform.Find(path);
                    if (t != null) snapshotObjectActives[path] = t.gameObject.activeSelf;
                }
            }
        }
        Debug.Log("<color=cyan>[表情エディタ]</color> スナップショットを一時保存しました。");
    }

    private void RestoreSnapshot(VRC_ExpressionEditor editor)
    {
        if (snapshotClipValues.Count == 0 || editor == null || editor.availableClips.Count <= editor.selectedClipIndex) return;
        AnimationClip clip = editor.availableClips[editor.selectedClipIndex];

        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("スナップショットから復元");
        Undo.RecordObject(clip, "スナップショットから復元");

        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            if (binding.propertyName.StartsWith("blendShape.") || (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive"))
            {
                AnimationUtility.SetEditorCurve(clip, binding, null);
            }
        }

        foreach (var pathKvp in snapshotClipValues)
        {
            string path = pathKvp.Key;
            foreach (var shapeKvp in pathKvp.Value)
            {
                var binding = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shapeKvp.Key);
                var curve = new AnimationCurve(new Keyframe(0f, shapeKvp.Value));
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
        }

        foreach (var kvp in snapshotClipActiveValues)
        {
            var binding = EditorCurveBinding.FloatCurve(kvp.Key, typeof(GameObject), "m_IsActive");
            var curve = new AnimationCurve(new Keyframe(0f, kvp.Value ? 1f : 0f));
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        Undo.CollapseUndoOperations(group);
        editor.RefreshExpressionCache();
        editor.ApplySorting();
        editor.ForceRepaintPreview();
    }

    private void DrawIconCapturePanel(VRC_ExpressionEditor editor)
    {
        GUILayout.BeginVertical(GUI.skin.box); GUILayout.Label("アイコン撮影 (透過PNG)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal(); GUILayout.Label("保存先:", optW45);
        string currentPath = (editor != null && editor.GetSettings() != null) ? editor.GetSettings().lastIconSavePath : "Assets";
        if (string.IsNullOrEmpty(currentPath)) currentPath = "Assets";

        GUILayout.Label(currentPath, EditorStyles.helpBox, optExpandTrue);

        // Projectウィンドウで保存先フォルダをピン留め(Ping)してフォーカスする検索ボタン
        GUIContent searchIcon = EditorGUIUtility.IconContent("d_Search Icon");
        searchIcon.tooltip = "この保存先フォルダの位置をProjectウィンドウで表示します";
        if (GUILayout.Button(searchIcon, GUILayout.Width(28), GUILayout.Height(18)))
        {
            Object folderObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(currentPath);
            if (folderObject != null)
            {
                EditorGUIUtility.PingObject(folderObject);
                Selection.activeObject = folderObject;
            }
            else
            {
                Debug.LogWarning($"<color=cyan>[表情エディタ]</color> フォルダが見つかりませんでした: {currentPath}");
            }
        }

        if (GUILayout.Button("変更", optW45))
        {
            string absPath = EditorUtility.OpenFolderPanel("保存先フォルダを選択", currentPath, "");
            if (!string.IsNullOrEmpty(absPath) && absPath.Contains(Application.dataPath))
            {
                if (editor != null && editor.GetSettings() != null) { editor.GetSettings().lastIconSavePath = "Assets" + absPath.Substring(Application.dataPath.Length); EditorUtility.SetDirty(editor.GetSettings()); AssetDatabase.SaveAssets(); }
            }
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(0.7f, 1f, 1f);

        if (GUILayout.Button("📷 透過PNGで撮影", optH26))
        {
            string clipName = (editor != null && editor.availableClips != null && editor.selectedClipIndex < editor.availableClips.Count) ? editor.availableClips[editor.selectedClipIndex].name : "ExpressionIcon";
            string savePath = "";
            for (int i = 0; i < 1000; i++)
            {
                savePath = $"{currentPath}/{clipName}{(i == 0 ? "" : $"_{i}")}.png";
                if (!System.IO.File.Exists(savePath)) break;
            }
            RequestCapture(savePath);
        }
        GUI.backgroundColor = Color.white;
        if (GUILayout.Button(resizeIconContent, optW28, optH26)) OptimizeWindowSize();
        EditorGUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    private void OptimizeWindowSize()
    {
        Rect currentRect = this.position;
        float cameraWidth = currentRect.width - 20f; float cameraHeight = currentRect.height - 330f;
        float targetWidth = currentRect.width; float targetHeight = currentRect.height;
        if (cameraWidth > cameraHeight) targetWidth = cameraHeight + 20f; else targetHeight = cameraWidth + 330f;
        this.position = new Rect(currentRect.x, currentRect.y, targetWidth, targetHeight);
    }

    public void RequestCapture(string path)
    {
        pendingCapturePath = path;
        MarkPreviewDirty();
        Repaint();
    }

    private void CaptureCurrentPreviewInsideRenderLoop(string savePath)
    {
        var cam = previewUtility.camera;
        int size = 256;
        RenderTexture rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);

        var oldTarget = cam.targetTexture;
        var oldClearFlags = cam.clearFlags;
        var oldBgColor = cam.backgroundColor;

        cam.targetTexture = rt;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0);

        cam.Render();

        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        tex.Apply();

        RenderTexture.active = null;
        cam.targetTexture = oldTarget;
        cam.clearFlags = oldClearFlags;
        cam.backgroundColor = oldBgColor;
        DestroyImmediate(rt);

        string absolutePath = System.IO.Path.GetFullPath(Application.dataPath + savePath.Substring("Assets".Length));
        string directory = System.IO.Path.GetDirectoryName(absolutePath);
        if (!System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
            string relativeDir = System.IO.Path.GetDirectoryName(savePath).Replace("\\", "/");
            AssetDatabase.ImportAsset(relativeDir);
        }

        System.IO.File.WriteAllBytes(absolutePath, tex.EncodeToPNG());
        DestroyImmediate(tex);

        AssetDatabase.ImportAsset(savePath);
        if (AssetImporter.GetAtPath(savePath) as TextureImporter is TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
        }
        AssetDatabase.Refresh();

        if (VRC_ExpressionThumbnailWindow.Instance != null)
        {
            VRC_ExpressionThumbnailWindow.Instance.OnCaptureComplete();
        }
    }

    private void InitializePreviewUtilityIfNeeded()
    {
        if (previewUtility == null)
        {
            previewUtility = new PreviewRenderUtility();
            previewUtility.camera.cullingMask = -1;
            previewUtility.camera.fieldOfView = 30f;
            previewUtility.camera.nearClipPlane = 0.01f;
            previewUtility.camera.farClipPlane = 50f;
            previewUtility.camera.backgroundColor = new Color(0.19f, 0.19f, 0.19f, 1f);
            previewUtility.camera.clearFlags = CameraClearFlags.SolidColor;
        }
    }
    // --- タイムライン窓からアクセスするための窓口 ---
    public bool GetIsComparing() => isComparing;
    public bool GetIsMuted() => isMuted;

    public void SetIsComparing(bool value)
    {
        isComparing = value;
        MarkPreviewDirty(); // 状態が変わったので再描画を予約
    }

    public void SetIsMuted(bool value)
    {
        isMuted = value;
        MarkPreviewDirty(); // 状態が変わったので再描画を予約
    }
}