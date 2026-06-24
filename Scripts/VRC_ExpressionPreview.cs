using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

public class VRC_ExpressionPreview : EditorWindow
{
    public static VRC_ExpressionPreview Instance { get; private set; }

    private PreviewRenderUtility previewUtility;
    private GameObject previewDummy;
    private GameObject lastRootObject;
    private Vector3 cameraPivot = new Vector3(0, 1.5f, 0);
    private Vector3 cameraEuler = new Vector3(0, 180, 0);
    private float cameraDistance = 0.5f;

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

    private Light cachedSceneLight;
    private double lastLightCheckTime;
    private Color cachedAmbientColor = Color.gray;
    private Quaternion lastLightRot;
    private Color lastLightColor;
    private float lastLightIntensity;

    private bool isAvatarChanged = true;
    private bool isDirty = true;

    private enum CameraTool { Pan, Orbit, Zoom }
    private CameraTool currentCameraTool = CameraTool.Orbit;

    private Dictionary<EditorCurveBinding, AnimationCurve> snapshotCurves = new Dictionary<EditorCurveBinding, AnimationCurve>();
    private Dictionary<SkinnedMeshRenderer, float[]> snapshotSMRWeights = new Dictionary<SkinnedMeshRenderer, float[]>();
    private Dictionary<GameObject, bool> snapshotObjectActives = new Dictionary<GameObject, bool>();

    private Dictionary<SkinnedMeshRenderer, float[]> normalSMRWeights = new Dictionary<SkinnedMeshRenderer, float[]>();
    private Dictionary<GameObject, bool> normalObjectActives = new Dictionary<GameObject, bool>();

    private bool isComparing = false;
    private bool isMuted = false;

    private Vector2 uiScrollPos;

    private static readonly string[] CAMERA_TOOL_NAMES = new string[] { "平行", "回転", "ズーム" };

    private GUIContent resizeIconContent;
    private GUILayoutOption optW28, optW45, optW65;
    private GUILayoutOption optH20, optH26, optExpandTrue;

    private GUIStyle centerLockStyle;

    private void OnEnable()
    {
        Instance = this;
        isDirty = true;
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        CleanupPreview();
        if (previewUtility != null) { previewUtility.Cleanup(); previewUtility = null; }
    }

    private void CleanupPreview()
    {
        if (previewDummy != null) { DestroyImmediate(previewDummy); previewDummy = null; }
        cachedMeshPairs.Clear();
    }

    private void OnDestroy() { CleanupPreview(); }

    private void OnEditorUpdate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        if (VRC_ExpressionEditor.Instance == null) { this.Close(); return; }

        double timeSinceStartup = EditorApplication.timeSinceStartup;
        if (timeSinceStartup - lastLightCheckTime < 0.1) return;
        lastLightCheckTime = timeSinceStartup;

        if (cachedSceneLight == null) return;

        bool lightChanged = false;
        if (cachedSceneLight.transform.rotation != lastLightRot) { lightChanged = true; lastLightRot = cachedSceneLight.transform.rotation; }
        if (cachedSceneLight.color != lastLightColor) { lightChanged = true; lastLightColor = cachedSceneLight.color; }
        if (cachedSceneLight.intensity != lastLightIntensity) { lightChanged = true; lastLightIntensity = cachedSceneLight.intensity; }

        if (lightChanged) Repaint();
    }

    public void UpdateSingleBlendShapeImmediate(string relativePath, string shapeName, float value)
    {
        if (previewDummy == null) return;
        foreach (var pair in cachedMeshPairs)
        {
            if (pair.relativePath == relativePath)
            {
                if (pair.shapeIndexMap.TryGetValue(shapeName, out int index))
                {
                    pair.dummySmr.SetBlendShapeWeight(index, value);
                }
                break;
            }
        }
    }

    public void UpdateSingleObjectActiveImmediate(string path, bool isActive)
    {
        if (previewDummy == null) return;
        Transform target = string.IsNullOrEmpty(path) ? previewDummy.transform : previewDummy.transform.Find(path);
        if (target != null) target.gameObject.SetActive(isActive);
    }

    public void MarkPreviewDirty() { isDirty = true; }
    public void ForceRebuildDummy() { CleanupPreview(); MarkPreviewDirty(); Repaint(); }
    public GameObject GetPreviewDummy() { return previewDummy; }

    public void FindAndCacheSceneLight()
    {
        cachedSceneLight = null;
        var lights = FindObjectsOfType<Light>();
        if (lights != null) cachedSceneLight = lights.FirstOrDefault(x => x.type == LightType.Directional);
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

        foreach (var pathKvp in previousClipValues)
        {
            foreach (var pair in cachedMeshPairs)
            {
                if (pair.relativePath == pathKvp.Key)
                {
                    editor.baseShapeKeyBackup.TryGetValue(pathKvp.Key, out var baseDict);
                    foreach (var shapeKvp in pathKvp.Value)
                    {
                        if (pair.shapeIndexMap.TryGetValue(shapeKvp.Key, out int index))
                        {
                            float defaultVal = 0f;
                            if (baseDict != null && baseDict.TryGetValue(shapeKvp.Key, out float cachedVal)) defaultVal = cachedVal;
                            pair.dummySmr.SetBlendShapeWeight(index, defaultVal);
                        }
                    }
                    break;
                }
            }
        }
    }

    private void InitializeGUIStylesIfNeeded()
    {
        // 修正：確実なnullチェック（optExpandTrue）を入れてGUIエラーを防ぐ
        if (resizeIconContent == null || optExpandTrue == null)
        {
            resizeIconContent = EditorGUIUtility.IconContent("d_ViewToolZoom");
            resizeIconContent.tooltip = "ウィンドウサイズを自動調整します";

            optW28 = GUILayout.Width(28);
            optW45 = GUILayout.Width(45);
            optW65 = GUILayout.Width(65);
            optH20 = GUILayout.Height(20);
            optH26 = GUILayout.Height(26);
            optExpandTrue = GUILayout.ExpandWidth(true);

            centerLockStyle = new GUIStyle(EditorStyles.boldLabel);
            centerLockStyle.alignment = TextAnchor.MiddleCenter;
            centerLockStyle.fontSize = 14;
            centerLockStyle.wordWrap = true;
        }
    }

    private void OnGUI()
    {
        InitializeGUIStylesIfNeeded();

        // 修正：再生モード中は重い処理を止め、中央にメッセージのみを表示する
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
        SyncSceneLightingToPreview();

        if (Event.current.type == EventType.Repaint)
        {
            bool isDraggingNow = GUIUtility.hotControl != 0;
            if (!isComparing && !isMuted && ((isDirty && !isDraggingNow) || previewDummy == null))
            {
                SetupAndPoseDummy(editor);
                UpdatePreviewCamera();
                isDirty = false;
            }
            else
            {
                UpdatePreviewCamera();
            }

            previewUtility.BeginPreview(rect, GUIStyle.none);
            if (previewDummy != null) { previewUtility.AddSingleGO(previewDummy); previewUtility.camera.Render(); }
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

    private void SetupAndPoseDummy(VRC_ExpressionEditor editor)
    {
        if (editor.rootObject == null) return;

        AnimationClip editingClip = null;
        if (editor.availableClips != null && editor.availableClips.Count > 0 && editor.selectedClipIndex < editor.availableClips.Count)
            editingClip = editor.availableClips[editor.selectedClipIndex];

        if (editor.testBaseClip != cachedTestBaseClip)
        {
            ResetPreviousAnimBlendShapes(testBaseClipCache);
            cachedTestBaseClip = editor.testBaseClip;
            UpdateTestBaseClipCache(editor.testBaseClip, editor);
        }

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
            BuildMeshPairsCache(editor);

            if (editor.testBaseClip != null) editor.testBaseClip.SampleAnimation(previewDummy, 0f);
            if (editingClip != null) editingClip.SampleAnimation(previewDummy, 0f);
        }

        foreach (var pair in cachedMeshPairs)
        {
            var dummySmr = pair.dummySmr;
            string path = pair.relativePath;
            if (dummySmr == null || dummySmr.sharedMesh == null) continue;

            if (editor.baseShapeKeyBackup.TryGetValue(path, out var baseDict))
                foreach (var kvp in baseDict) if (pair.shapeIndexMap.TryGetValue(kvp.Key, out int index)) dummySmr.SetBlendShapeWeight(index, kvp.Value);

            if (editor.testBaseClip != null && testBaseClipCache.TryGetValue(path, out var baseAnimDict))
                foreach (var kvp in baseAnimDict) if (pair.shapeIndexMap.TryGetValue(kvp.Key, out int index)) dummySmr.SetBlendShapeWeight(index, kvp.Value);

            if (editor.clipExpressionValues.TryGetValue(path, out var clipDict))
                foreach (var kvp in clipDict) if (pair.shapeIndexMap.TryGetValue(kvp.Key, out int index)) dummySmr.SetBlendShapeWeight(index, kvp.Value);
        }

        if (editor.availableSmrs != null && editor.availableSmrs.Count > editor.selectedSmrIndex)
        {
            SkinnedMeshRenderer targetSmr = editor.availableSmrs[editor.selectedSmrIndex];
            string targetPath = editor.GetRelativePath(targetSmr.gameObject);

            MeshPair activePair = default; bool found = false;
            foreach (var pair in cachedMeshPairs) if (pair.relativePath == targetPath) { activePair = pair; found = true; break; }

            if (found && activePair.dummySmr != null)
            {
                foreach (var kvp in editor.currentExpressionValues)
                    if (activePair.shapeIndexMap.TryGetValue(kvp.Key, out int index)) activePair.dummySmr.SetBlendShapeWeight(index, kvp.Value);

                if (editor.testShapeKeyIndex >= 0 && editor.testShapeKeyIndex < activePair.originalSmr.sharedMesh.blendShapeCount)
                {
                    string testShapeName = activePair.originalSmr.sharedMesh.GetBlendShapeName(editor.testShapeKeyIndex);
                    if (activePair.shapeIndexMap.TryGetValue(testShapeName, out int index))
                        activePair.dummySmr.SetBlendShapeWeight(index, editor.testShapeKeyValue);
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

    private void ResetCameraPivot() { cameraPivot = savedHeadLocalPosition + Vector3.up * 0.05f; cameraEuler = new Vector3(0, 180, 0); cameraDistance = 0.5f; MarkPreviewDirty(); }

    private SkinnedMeshRenderer FindCorrespondingSMR(VRC_ExpressionEditor editor, GameObject parent, SkinnedMeshRenderer original)
    {
        if (original == null) return null; string path = editor.GetRelativePath(original.gameObject);
        if (string.IsNullOrEmpty(path)) return parent.GetComponent<SkinnedMeshRenderer>();
        Transform t = parent.transform.Find(path); return t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
    }

    private void SyncSceneLightingToPreview()
    {
        if (previewUtility == null) return;
        var sceneLight = cachedSceneLight;
        if (sceneLight != null && sceneLight.gameObject != null && sceneLight.enabled && sceneLight.gameObject.activeInHierarchy)
        {
            previewUtility.lights[0].enabled = true;
            previewUtility.lights[0].color = sceneLight.color;
            previewUtility.lights[0].intensity = sceneLight.intensity;
            previewUtility.lights[0].transform.rotation = sceneLight.transform.rotation;
        }
        else
        {
            previewUtility.lights[0].enabled = true;
            previewUtility.lights[0].color = Color.white;
            previewUtility.lights[0].intensity = 1.0f;
            previewUtility.lights[0].transform.rotation = Quaternion.Euler(30f, 135f, 0f);
        }
        previewUtility.lights[1].color = cachedAmbientColor;
        previewUtility.lights[1].intensity = 1.0f;
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
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("表情スナップショット (一時比較)", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("📸 一時保存", optH20)) TakeSnapshot(editor);

        Color prevBg = GUI.backgroundColor;
        if (isComparing) GUI.backgroundColor = new Color(0.9f, 0.9f, 1f);
        EditorGUI.BeginChangeCheck();
        isComparing = GUILayout.Toggle(isComparing, "👁️ 比較 (ON/OFF)", GUI.skin.button, optH20);
        if (EditorGUI.EndChangeCheck())
        {
            if (isComparing) { isMuted = false; CaptureNormalExpression(); ApplySnapshotDirectly(); } else RestoreNormalExpressionDirectly();
            Repaint();
        }
        GUI.backgroundColor = prevBg;

        Color prevBgMute = GUI.backgroundColor;
        if (isMuted) GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
        EditorGUI.BeginChangeCheck();
        isMuted = GUILayout.Toggle(isMuted, "🔇 ミュート", GUI.skin.button, optH20);
        if (EditorGUI.EndChangeCheck())
        {
            if (isMuted) { isComparing = false; CaptureNormalExpression(); ApplyMuteExpressionDirectly(editor); } else RestoreNormalExpressionDirectly();
            Repaint();
        }
        GUI.backgroundColor = prevBgMute;

        if (GUILayout.Button("⏪ 復元する", optH20))
            if (EditorUtility.DisplayDialog("スナップショット復元", "保存時点に書き戻しますか？", "復元", "キャンセル")) RestoreSnapshot(editor);

        EditorGUILayout.EndHorizontal();
        GUILayout.Label(snapshotSMRWeights.Count > 0 ? "※一時保存中..." : "※一時保存データはありません", EditorStyles.miniLabel);
        GUILayout.EndVertical();
    }

    private void ApplyMuteExpressionDirectly(VRC_ExpressionEditor editor)
    {
        if (previewDummy == null || editor == null) return;
        foreach (var pair in cachedMeshPairs)
        {
            var dummySmr = pair.dummySmr; string path = pair.relativePath;
            if (dummySmr == null || dummySmr.sharedMesh == null) continue;
            if (editor.baseShapeKeyBackup.TryGetValue(path, out var baseDict))
                for (int i = 0; i < dummySmr.sharedMesh.blendShapeCount; i++) { baseDict.TryGetValue(dummySmr.sharedMesh.GetBlendShapeName(i), out float val); dummySmr.SetBlendShapeWeight(i, val); }
            if (editor.clipExpressionValues.TryGetValue(path, out var clipDict))
                foreach (var kvp in clipDict) if (pair.shapeIndexMap.TryGetValue(kvp.Key, out int idx)) dummySmr.SetBlendShapeWeight(idx, 0f);
        }
        foreach (var path in editor.activeObjectValues.Keys) { Transform t = string.IsNullOrEmpty(path) ? previewDummy.transform : previewDummy.transform.Find(path); if (t != null) t.gameObject.SetActive(false); }
    }

    private void CaptureNormalExpression()
    {
        normalSMRWeights.Clear(); normalObjectActives.Clear(); if (previewDummy == null) return;
        foreach (var pair in cachedMeshPairs)
        {
            if (pair.dummySmr == null || pair.dummySmr.sharedMesh == null) continue;
            float[] weights = new float[pair.dummySmr.sharedMesh.blendShapeCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = pair.dummySmr.GetBlendShapeWeight(i);
            normalSMRWeights[pair.dummySmr] = weights;
        }
        var editor = VRC_ExpressionEditor.Instance;
        if (editor != null) foreach (var path in editor.activeObjectValues.Keys) { Transform t = string.IsNullOrEmpty(path) ? previewDummy.transform : previewDummy.transform.Find(path); if (t != null) normalObjectActives[t.gameObject] = t.gameObject.activeSelf; }
    }

    private void RestoreNormalExpressionDirectly()
    {
        if (previewDummy == null || normalSMRWeights.Count == 0) return;
        foreach (var kvp in normalSMRWeights) if (kvp.Key != null && kvp.Key.sharedMesh != null) for (int i = 0; i < kvp.Value.Length; i++) kvp.Key.SetBlendShapeWeight(i, kvp.Value[i]);
        foreach (var kvp in normalObjectActives) if (kvp.Key != null) kvp.Key.SetActive(kvp.Value);
    }

    private void ApplySnapshotDirectly()
    {
        if (previewDummy == null || snapshotSMRWeights.Count == 0) return;
        foreach (var kvp in snapshotSMRWeights) if (kvp.Key != null && kvp.Key.sharedMesh != null) for (int i = 0; i < kvp.Value.Length; i++) kvp.Key.SetBlendShapeWeight(i, kvp.Value[i]);
        foreach (var kvp in snapshotObjectActives) if (kvp.Key != null) kvp.Key.SetActive(kvp.Value);
    }

    private void TakeSnapshot(VRC_ExpressionEditor editor)
    {
        snapshotCurves.Clear();
        if (editor != null && editor.availableClips.Count > editor.selectedClipIndex)
        {
            AnimationClip clip = editor.availableClips[editor.selectedClipIndex];
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.propertyName.StartsWith("blendShape.") || (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive"))
                {
                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve != null) snapshotCurves[binding] = new AnimationCurve(curve.keys);
                }
            }
        }
        snapshotSMRWeights.Clear(); snapshotObjectActives.Clear();
        if (previewDummy == null) return;

        foreach (var pair in cachedMeshPairs)
        {
            if (pair.dummySmr == null || pair.dummySmr.sharedMesh == null) continue;
            float[] weights = new float[pair.dummySmr.sharedMesh.blendShapeCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = pair.dummySmr.GetBlendShapeWeight(i);
            snapshotSMRWeights[pair.dummySmr] = weights;
        }

        if (editor != null) foreach (var path in editor.activeObjectValues.Keys) { Transform t = string.IsNullOrEmpty(path) ? previewDummy.transform : previewDummy.transform.Find(path); if (t != null) snapshotObjectActives[t.gameObject] = t.gameObject.activeSelf; }
    }

    private void RestoreSnapshot(VRC_ExpressionEditor editor)
    {
        if (snapshotCurves.Count == 0 || editor == null || editor.availableClips.Count <= editor.selectedClipIndex) return;
        AnimationClip clip = editor.availableClips[editor.selectedClipIndex];
        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("スナップショットから復元"); Undo.RecordObject(clip, "スナップショットから復元");
        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            if (binding.propertyName.StartsWith("blendShape.") || (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive"))
                AnimationUtility.SetEditorCurve(clip, binding, null);
        foreach (var kvp in snapshotCurves) AnimationUtility.SetEditorCurve(clip, kvp.Key, kvp.Value);
        Undo.CollapseUndoOperations(group);
        editor.RefreshExpressionCache(); editor.ApplySorting(); editor.ForceRepaintPreview();
    }

    private void DrawIconCapturePanel(VRC_ExpressionEditor editor)
    {
        GUILayout.BeginVertical(GUI.skin.box); GUILayout.Label("アイコン撮影 (透過PNG)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal(); GUILayout.Label("保存先:", optW45);
        string currentPath = (editor != null && editor.GetSettings() != null) ? editor.GetSettings().lastIconSavePath : "Assets";
        GUILayout.Label(currentPath, EditorStyles.helpBox, optExpandTrue);
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
        if (GUILayout.Button("📷 透過PNGで撮影", optH26)) CaptureIconAndSave(editor, currentPath);
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

    private void CaptureIconAndSave(VRC_ExpressionEditor editor, string folderPath)
    {
        if (previewUtility == null || previewDummy == null) return;
        string clipName = (editor != null && editor.availableClips != null && editor.availableClips.Count > editor.selectedClipIndex) ? editor.availableClips[editor.selectedClipIndex].name : "ExpressionIcon";
        string savePath = "";
        for (int i = 0; i < 1000; i++) { savePath = $"{folderPath}/{clipName}{(i == 0 ? "" : $"_{i}")}.png"; if (!System.IO.File.Exists(savePath)) break; }

        var cam = previewUtility.camera;
        var oldClearFlags = cam.clearFlags; var oldBgColor = cam.backgroundColor;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0, 0, 0, 0);

        int size = 256; RenderTexture rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
        var oldTarget = cam.targetTexture; cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt; Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0); tex.Apply();

        RenderTexture.active = null; cam.targetTexture = oldTarget; cam.clearFlags = oldClearFlags; cam.backgroundColor = oldBgColor;
        DestroyImmediate(rt);
        System.IO.File.WriteAllBytes(savePath, tex.EncodeToPNG()); DestroyImmediate(tex);

        AssetDatabase.ImportAsset(savePath);
        if (AssetImporter.GetAtPath(savePath) as TextureImporter is TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false; importer.alphaIsTransparency = true; importer.SaveAndReimport();
        }
        AssetDatabase.Refresh();
    }

    private void InitializePreviewUtilityIfNeeded()
    {
        if (previewUtility == null)
        {
            previewUtility = new PreviewRenderUtility();
            previewUtility.camera.cullingMask = -1; previewUtility.camera.fieldOfView = 30f;
            previewUtility.camera.nearClipPlane = 0.01f; previewUtility.camera.farClipPlane = 10f;
            previewUtility.camera.backgroundColor = new Color(0.19f, 0.19f, 0.19f, 1f); previewUtility.camera.clearFlags = CameraClearFlags.SolidColor;
        }
    }
}