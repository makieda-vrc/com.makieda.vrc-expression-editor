using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.Collections.Generic;
using System.Linq;
using System.IO;

public class VRC_ExpressionEditor : EditorWindow
{
    public static VRC_ExpressionEditor Instance { get; private set; }

    private ExpressionEditorSettings settingsAsset;
    private const string SETTINGS_PATH = "Assets/Editor/VRC_ExpressionEditor/ExpressionEditorSettings.asset";
    public ExpressionEditorSettings GetSettings() => settingsAsset;

    private GameObject lastSelectionObject;
    public List<AnimationClip> manuallyCreatedClips = new List<AnimationClip>();

    [System.NonSerialized] public GameObject rootObject;
    [System.NonSerialized] public List<SkinnedMeshRenderer> availableSmrs = new List<SkinnedMeshRenderer>();

    public Dictionary<SkinnedMeshRenderer, string> smrPathCache = new Dictionary<SkinnedMeshRenderer, string>();

    public List<AnimationClip> availableClips = new List<AnimationClip>();
    public int selectedClipIndex = 0;
    public int selectedSmrIndex = 0;
    public AnimationClip referenceClip;

    public List<AnimatorController> cachedControllers = new List<AnimatorController>();

    public Dictionary<string, Dictionary<string, float>> baseShapeKeyBackup = new Dictionary<string, Dictionary<string, float>>();
    public Dictionary<string, Dictionary<string, float>> clipExpressionValues = new Dictionary<string, Dictionary<string, float>>();
    public HashSet<string> registeredShapeKeys = new HashSet<string>();
    public Dictionary<string, float> currentExpressionValues = new Dictionary<string, float>();

    public Dictionary<string, bool> activeObjectValues = new Dictionary<string, bool>();

    public bool isMirroringEnabled = false;
    public Dictionary<string, string> mirrorShapeMap = new Dictionary<string, string>();

    public bool isCopyPasteMode = false;
    public HashSet<string> copyTargetShapes = new HashSet<string>();
    public Dictionary<string, float> clipboardValues = new Dictionary<string, float>();

    public HashSet<string> copyTargetObjects = new HashSet<string>();
    public Dictionary<string, bool> clipboardObjectValues = new Dictionary<string, bool>();

    public HashSet<string> favoriteShapes = new HashSet<string>();

    public List<string> layerNames = new List<string>();
    public bool[] selectedLayers;

    public bool showAdvancedOptions = false;
    public enum SortMode { Default, ActiveFirst, RegisteredFirst }
    public SortMode currentSortMode = SortMode.ActiveFirst;
    public List<string> sortedShapeKeyNames = new List<string>();

    public bool autoLinkShapeKeys = true;
    public string searchFilter = "";

    private HashSet<string> dirtyShapeKeys = new HashSet<string>();
    private Vector2 leftScrollPos;
    private Vector2 rightScrollPos;

    public AnimationClip testBaseClip;
    public int testShapeKeyIndex = 0;
    public float testShapeKeyValue = 0f;

    private double lastPreviewCheckTime;
    private bool isClampBlendShapesEnabledCache = false;

    private static readonly string[] SORT_MODE_NAMES = { "デフォルト", "動いている順", "登録済み優先" };
    private static readonly (string l, string r)[] MIRROR_REPLACE_RULES = { ("_L", "_R"), ("_l", "_r"), (".L", ".R"), ("Left", "Right"), ("left", "right"), ("左", "右") };

    private float cachedMaxShapeNameWidth = 75f;
    private float cachedMaxObjNameWidth = 75f;
    private bool isMultiFrameCache = false;
    private string[] clipNamesCache = new string[0];
    private string[] smrNamesCache = new string[0];
    public string[] testShapeNamesCache = new string[0];
    public string[] testClipNamesCache = new string[0];

    private GUIContent cachedLayerContent;

    private GUIStyle cachedBoldLabelStyle;
    private GUIStyle cachedNormalLabelStyle;
    private GUIStyle cachedDotButtonStyle;
    private GUIStyle cachedPlaceholderStyle;
    private GUIContent cachedFavOnContent;
    private GUIContent cachedFavOffContent;
    private GUIContent warnIconContent;
    private GUIContent trashIconContent;
    private GUIContent createIconContent;
    private GUIContent openIconContent;
    private GUIContent linkIconContent;
    private GUIContent mirrorIconContent;
    private GUIContent copypasteIconContent;
    private GUIContent jumpDownIconContent;
    private GUIContent jumpUpIconContent;
    private GUIContent cachedSortTooltipContent;
    private GUIContent gearIconContent;
    private GUIStyle centerLockStyle;

    [System.NonSerialized] public List<string> detailFilterWords = new List<string>();
    [System.NonSerialized] public List<bool> detailFilterActives = new List<bool>();
    [System.NonSerialized] public bool isFilterWindowOpen = false;

    private float filterPanelWidth = 150f;
    private bool isResizingFilter = false;
    [System.NonSerialized] public bool isClosing = false;

    private GUILayoutOption optW15, optW18, optW20, optW24, optW26, optW28, optW30, optW40, optWMin100, optWMax200, optWMax165;
    private GUILayoutOption optH1, optH18, optH22, optH24, optH30, optExpandTrue;

    private float lastShapeLabelW = -1f, lastSliderW = -1f;
    private GUILayoutOption optShapeLabelW, optSliderW;
    private float lastObjLabelW = -1f, lastToggleW = -1f;
    private GUILayoutOption optObjLabelW, optToggleW;

    private GUIContent[] cachedShapeContents = new GUIContent[0];

    private class ActiveObjectCache { public string path; public GUIContent content; }
    private List<ActiveObjectCache> cachedActiveObjects = new List<ActiveObjectCache>();

    public bool IsDraggingSlider() => GUIUtility.hotControl != 0;

    [MenuItem("Tools/VRChat/1F表情エディタを開く")]
    public static void OpenBothWindows()
    {
        Instance = GetWindow<VRC_ExpressionEditor>("表情エディタ");
        Instance.minSize = new Vector2(350, 400);
        var preview = GetWindow<VRC_ExpressionPreview>("表情プレビュー");
        preview.minSize = new Vector2(300, 450);
        Instance.Focus();
    }

    private void OnEnable()
    {
        Instance = this;
        isClosing = false;

        Selection.selectionChanged += OnSelectionChanged;
        EditorApplication.update += OnEditorUpdate;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += OnSceneOpened;

        LoadSettings();
        this.minSize = new Vector2(350f, 400f);
        if (settingsAsset != null)
        {
            isFilterWindowOpen = settingsAsset.isFilterWindowOpen;
            filterPanelWidth = settingsAsset.filterPanelWidth;
            if (filterPanelWidth < 100f) filterPanelWidth = 150f;
            this.minSize = new Vector2(isFilterWindowOpen ? 350f + filterPanelWidth : 350f, 400f);
        }

        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            AutoSelectAvatarOnLoad();
            Repaint();
        };
    }

    private void OnDisable()
    {
        isClosing = true;
        Selection.selectionChanged -= OnSelectionChanged;
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        UnityEditor.SceneManagement.EditorSceneManager.sceneOpened -= OnSceneOpened;

        SaveCurrentSettings();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            AutoSelectAvatarOnLoad();
            Repaint();
            if (VRC_ExpressionPreview.Instance != null) VRC_ExpressionPreview.Instance.Repaint();
        }
    }

    private void OnSceneOpened(UnityEngine.SceneManagement.Scene scene, UnityEditor.SceneManagement.OpenSceneMode mode)
    {
        AutoSelectAvatarOnLoad();
        Repaint();
    }

    private void AutoSelectAvatarOnLoad()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        if (Selection.activeGameObject != null)
        {
            var animator = Selection.activeGameObject.GetComponentInParent<Animator>();
            if (animator != null) { rootObject = animator.gameObject; RefreshData(true); return; }
        }

        if (settingsAsset != null && !string.IsNullOrEmpty(settingsAsset.lastAvatarName))
        {
            GameObject foundObj = GameObject.Find(settingsAsset.lastAvatarName);
            if (foundObj != null)
            {
                var animator = foundObj.GetComponentInParent<Animator>();
                if (animator != null) { rootObject = animator.gameObject; RefreshData(true); }
            }
        }
    }

    private void OnEditorUpdate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        if (GUIUtility.hotControl == 0 && dirtyShapeKeys.Count > 0) CommitDirtyKeysDeferred();

        double timeSinceStartup = EditorApplication.timeSinceStartup;
        if (timeSinceStartup - lastPreviewCheckTime < 0.1) return;
        lastPreviewCheckTime = timeSinceStartup;
        if (VRC_ExpressionPreview.Instance == null) this.Close();
    }

    private void CommitDirtyKeysDeferred()
    {
        if (dirtyShapeKeys.Count == 0 || availableClips.Count <= selectedClipIndex) return;
        AnimationClip clip = availableClips[selectedClipIndex];
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("表情スライダー調整");
        foreach (string shapeName in dirtyShapeKeys) CommitShapeKeyValue(clip, shapeName, currentExpressionValues[shapeName]);
        dirtyShapeKeys.Clear();
        Undo.CollapseUndoOperations(group);
        RefreshExpressionCache();
    }

    private void LoadSettings()
    {
        if (!Directory.Exists("Assets/Editor/VRC_ExpressionEditor")) Directory.CreateDirectory("Assets/Editor/VRC_ExpressionEditor");
        settingsAsset = AssetDatabase.LoadAssetAtPath<ExpressionEditorSettings>(SETTINGS_PATH);
        if (settingsAsset == null)
        {
            settingsAsset = ScriptableObject.CreateInstance<ExpressionEditorSettings>();
            AssetDatabase.CreateAsset(settingsAsset, SETTINGS_PATH);
            AssetDatabase.SaveAssets();
        }
    }

    private string GetAvatarID()
    {
        if (rootObject == null) return "";
        Component descriptor = rootObject.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "VRCAvatarDescriptor");
        if (descriptor != null)
        {
            SerializedObject so = new SerializedObject(descriptor);
            SerializedProperty baseLayers = so.FindProperty("baseAnimationLayers");
            if (baseLayers != null && baseLayers.arraySize > 4)
            {
                var fx = baseLayers.GetArrayElementAtIndex(4).FindPropertyRelative("animatorController").objectReferenceValue;
                if (fx != null) return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(fx));
            }
        }
        if (cachedControllers.Count > 0 && cachedControllers[0] != null) return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(cachedControllers[0]));
        return "";
    }

    public void SaveCurrentSettings()
    {
        if (settingsAsset == null || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;

        if (rootObject != null) settingsAsset.lastAvatarName = rootObject.name;

        string id = GetAvatarID(); if (string.IsNullOrEmpty(id)) return;
        string clipPath = (availableClips.Count > selectedClipIndex && selectedClipIndex >= 0) ? AssetDatabase.GetAssetPath(availableClips[selectedClipIndex]) : "";
        string smrName = (availableSmrs.Count > selectedSmrIndex && selectedSmrIndex >= 0) ? availableSmrs[selectedSmrIndex].name : "";
        List<string> manualClipPaths = manuallyCreatedClips.Where(c => c != null).Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).ToList();

        string refClipPath = referenceClip != null ? AssetDatabase.GetAssetPath(referenceClip) : "";
        List<string> favShapes = favoriteShapes.ToList();

        settingsAsset.isFilterWindowOpen = isFilterWindowOpen;
        settingsAsset.filterPanelWidth = filterPanelWidth;

        settingsAsset.SaveEntry(id, clipPath, smrName, selectedLayers, manualClipPaths, refClipPath, favShapes, autoLinkShapeKeys, isMirroringEnabled, detailFilterWords, detailFilterActives);
        EditorUtility.SetDirty(settingsAsset);
    }

    private void OnSelectionChanged()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (Selection.activeGameObject != null)
        {
            var animator = Selection.activeGameObject.GetComponentInParent<Animator>();
            if (animator != null && animator.gameObject != rootObject)
            {
                SaveCurrentSettings();
                rootObject = animator.gameObject;
                RefreshData(true);
                Repaint();
            }
        }
    }

    private SerializedProperty FindClampPropertySmart(SerializedObject ps)
    {
        SerializedProperty iterator = ps.GetIterator();
        while (iterator.Next(true)) { string lowerName = iterator.name.ToLower(); if (lowerName.Contains("clamp") && lowerName.Contains("blend")) return iterator; }
        return null;
    }

    private bool IsClampBlendShapesEnabled()
    {
        try { PlayerSettings[] s = Resources.FindObjectsOfTypeAll<PlayerSettings>(); if (s != null && s.Length > 0) { SerializedObject ps = new SerializedObject(s[0]); ps.Update(); var p = FindClampPropertySmart(ps); if (p != null) return p.boolValue; } } catch { }
        return false;
    }

    private void UncheckClampBlendShapes()
    {
        try { PlayerSettings[] s = Resources.FindObjectsOfTypeAll<PlayerSettings>(); if (s != null && s.Length > 0) { SerializedObject ps = new SerializedObject(s[0]); ps.Update(); var p = FindClampPropertySmart(ps); if (p != null) { p.boolValue = false; ps.ApplyModifiedProperties(); EditorUtility.SetDirty(s[0]); AssetDatabase.SaveAssets(); } } } catch { }
    }

    private void InitializeGUIStylesIfNeeded()
    {
        if (cachedBoldLabelStyle == null || optExpandTrue == null)
        {
            cachedBoldLabelStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            cachedNormalLabelStyle = new GUIStyle(EditorStyles.label);
            cachedDotButtonStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            cachedPlaceholderStyle = new GUIStyle(EditorStyles.label);
            cachedPlaceholderStyle.normal.textColor = Color.gray;

            centerLockStyle = new GUIStyle(EditorStyles.boldLabel);
            centerLockStyle.alignment = TextAnchor.MiddleCenter;
            centerLockStyle.fontSize = 14;
            centerLockStyle.wordWrap = true;

            cachedSortTooltipContent = new GUIContent("", "シェイプキーの表示順を変更します");
            cachedFavOnContent = new GUIContent("★", "ピン留め解除");
            cachedFavOffContent = new GUIContent("☆", "ピン留め");
            warnIconContent = EditorGUIUtility.IconContent("console.warnIcon"); warnIconContent.tooltip = "警告: 複数フレームを持っています。\nスライダーを動かすと1F目に上書きされます！";
            trashIconContent = EditorGUIUtility.IconContent("TreeEditor.Trash"); trashIconContent.tooltip = "このアニメを編集リストから除外する";
            createIconContent = EditorGUIUtility.IconContent("Toolbar Plus"); createIconContent.tooltip = "新規表情アニメを作成";
            openIconContent = EditorGUIUtility.IconContent("FolderOpened Icon"); openIconContent.tooltip = "既存のアニメーションを開く";
            linkIconContent = EditorGUIUtility.IconContent("Linked"); linkIconContent.tooltip = "別メッシュ同名シェイプ連動";
            mirrorIconContent = new GUIContent("M", "左右対称（ミラー）モード");
            copypasteIconContent = EditorGUIUtility.IconContent("Clipboard"); copypasteIconContent.tooltip = "コピペモード";
            jumpDownIconContent = new GUIContent("▼", "一番下へジャンプ");
            jumpUpIconContent = new GUIContent("▲", "一番上へジャンプ");
            gearIconContent = EditorGUIUtility.IconContent("d_SettingsIcon"); gearIconContent.tooltip = "詳細フィルター（OR絞り込み）の開閉";

            optW15 = GUILayout.Width(15); optW18 = GUILayout.Width(18); optW20 = GUILayout.Width(20); optW24 = GUILayout.Width(24);
            optW26 = GUILayout.Width(26); optW28 = GUILayout.Width(28); optW30 = GUILayout.MinWidth(30); optW40 = GUILayout.Width(40);
            optWMin100 = GUILayout.MinWidth(100); optWMax200 = GUILayout.MaxWidth(200); optWMax165 = GUILayout.MaxWidth(165);

            optH1 = GUILayout.Height(1); optH18 = GUILayout.Height(18); optH22 = GUILayout.Height(22); optH24 = GUILayout.Height(24); optH30 = GUILayout.Height(30);
            optExpandTrue = GUILayout.ExpandWidth(true);

            if (cachedLayerContent == null) cachedLayerContent = new GUIContent("選択なし (なし)");
        }
    }

    private void OnGUI()
    {
        EnsureCollectionsInitialized();

        InitializeGUIStylesIfNeeded();

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("▶ 再生モード中です\n安全のため表情の編集をロックしています", centerLockStyle, GUILayout.Height(40));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
            return;
        }

        EditorGUIUtility.labelWidth = 95f;

        EditorGUILayout.BeginHorizontal();
        if (isFilterWindowOpen)
        {
            leftScrollPos = EditorGUILayout.BeginScrollView(leftScrollPos, GUILayout.Width(filterPanelWidth), GUILayout.ExpandHeight(true));
            DrawDetailFilterPanel();
            EditorGUILayout.EndScrollView();

            Rect splitterRect = GUILayoutUtility.GetRect(5, position.height, GUILayout.ExpandHeight(true), GUILayout.Width(5));
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);
            if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition)) isResizingFilter = true;
            if (isResizingFilter) { filterPanelWidth = Mathf.Clamp(Event.current.mousePosition.x, 110f, 350f); Repaint(); }
            if (Event.current.type == EventType.MouseUp && isResizingFilter) { isResizingFilter = false; this.minSize = new Vector2(350f + filterPanelWidth, 400f); SaveCurrentSettings(); }
        }

        rightScrollPos = EditorGUILayout.BeginScrollView(rightScrollPos, optExpandTrue, GUILayout.ExpandHeight(true));
        DrawControlPanel();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndHorizontal();
    }

    private void EnsureCollectionsInitialized()
    {
        if (favoriteShapes == null) favoriteShapes = new HashSet<string>();
        if (currentExpressionValues == null) currentExpressionValues = new Dictionary<string, float>();
        if (registeredShapeKeys == null) registeredShapeKeys = new HashSet<string>();
        if (dirtyShapeKeys == null) dirtyShapeKeys = new HashSet<string>();
        if (clipExpressionValues == null) clipExpressionValues = new Dictionary<string, Dictionary<string, float>>();
        if (activeObjectValues == null) activeObjectValues = new Dictionary<string, bool>();
        if (smrPathCache == null) smrPathCache = new Dictionary<SkinnedMeshRenderer, string>();
        if (baseShapeKeyBackup == null) baseShapeKeyBackup = new Dictionary<string, Dictionary<string, float>>();
        if (mirrorShapeMap == null) mirrorShapeMap = new Dictionary<string, string>();
        if (copyTargetShapes == null) copyTargetShapes = new HashSet<string>();
        if (clipboardValues == null) clipboardValues = new Dictionary<string, float>();
        if (copyTargetObjects == null) copyTargetObjects = new HashSet<string>();
        if (clipboardObjectValues == null) clipboardObjectValues = new Dictionary<string, bool>();
        if (manuallyCreatedClips == null) manuallyCreatedClips = new List<AnimationClip>();
        if (availableSmrs == null) availableSmrs = new List<SkinnedMeshRenderer>();
        if (availableClips == null) availableClips = new List<AnimationClip>();
        if (cachedControllers == null) cachedControllers = new List<AnimatorController>();
        if (layerNames == null) layerNames = new List<string>();
        if (selectedLayers == null) selectedLayers = new bool[0];
        if (detailFilterWords == null) detailFilterWords = new List<string>();
        if (detailFilterActives == null) detailFilterActives = new List<bool>();
        if (sortedShapeKeyNames == null) sortedShapeKeyNames = new List<string>();
        if (cachedShapeContents == null) cachedShapeContents = new GUIContent[0];
        if (cachedActiveObjects == null) cachedActiveObjects = new List<ActiveObjectCache>();
        if (optShapeLabelW == null) lastShapeLabelW = -1f;
        if (optSliderW == null) lastSliderW = -1f;
        if (optObjLabelW == null) lastObjLabelW = -1f;
        if (optToggleW == null) lastToggleW = -1f;
    }

    private void DrawDetailFilterPanel()
    {
        if (rootObject == null) return;
        EditorGUILayout.Space(5);
        for (int i = 0; i < detailFilterWords.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck(); bool active = EditorGUILayout.Toggle(detailFilterActives[i], optW15);
            if (EditorGUI.EndChangeCheck()) { detailFilterActives[i] = active; ApplySorting(); ForceRepaintPreview(); SaveCurrentSettings(); }

            EditorGUI.BeginChangeCheck(); string word = EditorGUILayout.TextField(detailFilterWords[i]);
            if (EditorGUI.EndChangeCheck()) { detailFilterWords[i] = word; ApplySorting(); ForceRepaintPreview(); SaveCurrentSettings(); }

            GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
            if (GUILayout.Button("✕", optW24)) { detailFilterWords.RemoveAt(i); detailFilterActives.RemoveAt(i); ApplySorting(); ForceRepaintPreview(); SaveCurrentSettings(); GUI.backgroundColor = Color.white; EditorGUILayout.EndHorizontal(); GUIUtility.ExitGUI(); }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.Space(5);
        GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);
        if (GUILayout.Button("＋ 条件を追加", GUILayout.Height(24))) { detailFilterWords.Add(""); detailFilterActives.Add(true); ApplySorting(); SaveCurrentSettings(); }
        GUI.backgroundColor = Color.white;
    }

    private void DrawControlPanel()
    {
        float rowWidth = position.width - 18f; if (isFilterWindowOpen) rowWidth -= (filterPanelWidth + 10f);

        Rect row1Rect = EditorGUILayout.GetControlRect(optH18);
        float avatarLabelWidth = 50f; float meshLabelWidth = 65f;
        float firstRowFieldWidth = Mathf.Clamp((rowWidth - (avatarLabelWidth + meshLabelWidth + 10f)) / 2f, 40f, 180f);

        Rect avatarLabelRect = new Rect(row1Rect.x, row1Rect.y, avatarLabelWidth, 18);
        Rect avatarFieldRect = new Rect(avatarLabelRect.xMax, row1Rect.y, firstRowFieldWidth, 18);
        Rect meshLabelRect = new Rect(avatarFieldRect.xMax + 10f, row1Rect.y, meshLabelWidth, 18);
        Rect meshFieldRect = new Rect(meshLabelRect.xMax, row1Rect.y, firstRowFieldWidth, 18);

        GUI.Label(avatarLabelRect, "アバター");
        EditorGUI.BeginChangeCheck();
        rootObject = (GameObject)EditorGUI.ObjectField(avatarFieldRect, rootObject, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck()) RefreshData(true);

        if (rootObject == null)
        {
            Rect helpRect = EditorGUILayout.GetControlRect(optH30);
            EditorGUI.HelpBox(helpRect, "Hierarchyからアバターを選択してください。", MessageType.Info);
            return;
        }

        if (availableSmrs != null && availableSmrs.Count > 0)
        {
            GUI.Label(meshLabelRect, "対象メッシュ");
            EditorGUI.BeginChangeCheck();
            selectedSmrIndex = EditorGUI.Popup(meshFieldRect, selectedSmrIndex, smrNamesCache);
            if (EditorGUI.EndChangeCheck()) { RefreshExpressionCache(); ApplySorting(); ForceRepaintPreview(); BuildMirrorMap(); UpdateCacheArrays(); }
        }

        Rect row2Rect = EditorGUILayout.GetControlRect(optH18);
        Rect layerLabelRect = new Rect(row2Rect.x, row2Rect.y, 70f, 18);
        Rect layerFieldRect = new Rect(layerLabelRect.xMax, row2Rect.y, Mathf.Clamp(rowWidth * 0.50f, 150f, 500f), 18);
        Rect reloadBtnRect = new Rect(row2Rect.x + rowWidth - 70f, row2Rect.y, 70f, 18);

        if (layerNames.Count > 0)
        {
            GUI.Label(layerLabelRect, "対象レイヤー");
            if (GUI.Button(layerFieldRect, cachedLayerContent, EditorStyles.popup)) UnityEditor.PopupWindow.Show(layerFieldRect, new ExpressionEditorLayerPopup(this));
        }

        if (GUI.Button(reloadBtnRect, "リロード")) RefreshData(false);

        EditorGUILayout.BeginHorizontal(); GUILayout.Label("アニメ", optW40);

        if (availableClips.Count > 0)
        {
            EditorGUI.BeginChangeCheck();
            selectedClipIndex = EditorGUILayout.Popup(selectedClipIndex, clipNamesCache, optWMin100, optWMax200);
            if (EditorGUI.EndChangeCheck()) { if (VRC_ExpressionPreview.Instance != null) VRC_ExpressionPreview.Instance.ResetPreviousAnimBlendShapes(clipExpressionValues); UpdateCacheArrays(); RefreshExpressionCache(); ApplySorting(); ForceRepaintPreview(); }
        }
        else EditorGUILayout.LabelField("なし");

        if (isMultiFrameCache) GUILayout.Label(warnIconContent, optW18, optH18);

        if (availableClips.Count > selectedClipIndex && selectedClipIndex >= 0)
        {
            AnimationClip currentClip = availableClips[selectedClipIndex];
            if (manuallyCreatedClips.Contains(currentClip))
            {
                Color prevColor = GUI.backgroundColor; GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
                if (GUILayout.Button(trashIconContent, optW24, optH18)) { RemoveClipFromManualList(currentClip); GUI.backgroundColor = prevColor; GUIUtility.ExitGUI(); }
                GUI.backgroundColor = prevColor;
            }
        }

        if (GUILayout.Button(createIconContent, optW24, optH18)) CreateNewAnimationClip();
        if (GUILayout.Button(openIconContent, optW24, optH18))
        {
            string absPath = EditorUtility.OpenFilePanel("アニメーションファイルを開く", Application.dataPath, "anim");
            if (!string.IsNullOrEmpty(absPath))
            {
                AnimationClip loadedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets" + absPath.Substring(Application.dataPath.Length));
                if (loadedClip != null) SelectNewClipManually(loadedClip);
            }
        }

        AnimationClip dragClip = null;
        EditorGUI.BeginChangeCheck(); dragClip = (AnimationClip)EditorGUILayout.ObjectField(dragClip, typeof(AnimationClip), false, optW30, optWMax165);
        if (EditorGUI.EndChangeCheck() && dragClip != null) SelectNewClipManually(dragClip);
        EditorGUILayout.EndHorizontal();

        Rect row4Rect = EditorGUILayout.GetControlRect(optH18);
        Rect searchFieldRect = new Rect(row4Rect.x, row4Rect.y, Mathf.Clamp(rowWidth - (105f + 84f + 20f + 20f + 30f), 50f, 250f), 18);

        EditorGUI.BeginChangeCheck(); searchFilter = EditorGUI.TextField(searchFieldRect, searchFilter);
        if (EditorGUI.EndChangeCheck()) ApplySorting();

        if (string.IsNullOrEmpty(searchFilter)) GUI.Label(new Rect(searchFieldRect.x + 3f, searchFieldRect.y + 1f, searchFieldRect.width, 18), "絞り込み...", cachedPlaceholderStyle);

        float currentToolsX = searchFieldRect.xMax + 2f;
        Rect gearRect = new Rect(currentToolsX, row4Rect.y, 24, 18);
        if (isFilterWindowOpen) GUI.backgroundColor = new Color(0.7f, 0.9f, 1f);
        EditorGUI.BeginChangeCheck(); bool toggleFilter = GUI.Toggle(gearRect, isFilterWindowOpen, gearIconContent, GUI.skin.button);
        if (EditorGUI.EndChangeCheck()) { isFilterWindowOpen = toggleFilter; SaveCurrentSettings(); ApplySorting(); Repaint(); }
        GUI.backgroundColor = Color.white; currentToolsX = gearRect.xMax + 2f;

        if (!string.IsNullOrEmpty(searchFilter)) { Rect cancelRect = new Rect(currentToolsX, row4Rect.y, 18, 18); if (GUI.Button(cancelRect, "✕")) { searchFilter = ""; GUI.FocusControl(null); ApplySorting(); } currentToolsX = cancelRect.xMax + 2f; }

        Rect sortRect = new Rect(currentToolsX, row4Rect.y, 105f, 18); GUI.Label(sortRect, cachedSortTooltipContent);
        EditorGUI.BeginChangeCheck(); currentSortMode = (SortMode)EditorGUI.Popup(sortRect, (int)currentSortMode, SORT_MODE_NAMES);
        if (EditorGUI.EndChangeCheck()) ApplySorting(); currentToolsX = sortRect.xMax + 5f;

        Rect linkRect = new Rect(currentToolsX, row4Rect.y, 26, 18); Color oldBg = GUI.backgroundColor;
        if (autoLinkShapeKeys) GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
        EditorGUI.BeginChangeCheck(); autoLinkShapeKeys = GUI.Toggle(linkRect, autoLinkShapeKeys, linkIconContent, GUI.skin.button);
        if (EditorGUI.EndChangeCheck()) SaveCurrentSettings(); GUI.backgroundColor = oldBg; currentToolsX = linkRect.xMax + 2f;

        Rect mirrorRect = new Rect(currentToolsX, row4Rect.y, 26, 18);
        if (isMirroringEnabled) GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
        EditorGUI.BeginChangeCheck(); isMirroringEnabled = GUI.Toggle(mirrorRect, isMirroringEnabled, mirrorIconContent, GUI.skin.button);
        if (EditorGUI.EndChangeCheck()) SaveCurrentSettings(); GUI.backgroundColor = oldBg; currentToolsX = mirrorRect.xMax + 2f;

        Rect pasteRect = new Rect(currentToolsX, row4Rect.y, 26, 18);
        if (isCopyPasteMode) GUI.backgroundColor = new Color(0.6f, 0.8f, 1f);
        EditorGUI.BeginChangeCheck(); isCopyPasteMode = GUI.Toggle(pasteRect, isCopyPasteMode, copypasteIconContent, GUI.skin.button);
        if (EditorGUI.EndChangeCheck()) { GUI.backgroundColor = oldBg; GUIUtility.ExitGUI(); }
        GUI.backgroundColor = oldBg;

        Rect jumpRect = new Rect(row4Rect.x + rowWidth - 20f, row4Rect.y, 20f, 18);
        if (GUI.Button(jumpRect, jumpDownIconContent)) leftScrollPos.y = 99999f;

        if (isCopyPasteMode)
        {
            EditorGUILayout.Space(); EditorGUILayout.BeginHorizontal(GUI.skin.box);
            GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);
            if (GUILayout.Button("一括貼り付け", optH22)) PasteClipboardValues();
            GUI.backgroundColor = Color.white;
            if (GUILayout.Button("動いているキーを自動選択", optH22)) AutoSelectActiveShapes();
            if (GUILayout.Button("✕ 選択クリア", optH22)) { copyTargetShapes.Clear(); clipboardValues.Clear(); copyTargetObjects.Clear(); clipboardObjectValues.Clear(); }
            EditorGUILayout.EndHorizontal();
        }

        GUILayout.Box("", optExpandTrue, optH1);
        if (availableSmrs != null && availableSmrs.Count > selectedSmrIndex && availableClips.Count > selectedClipIndex) DrawShapeKeySliders();

        GUILayout.Box("", optExpandTrue, optH1);
        DrawObjectActivePanel();

        GUILayout.Box("", optExpandTrue, optH1);
        DrawActionButtons();
    }

    private void BuildMirrorMap()
    {
        mirrorShapeMap.Clear(); if (availableSmrs == null || availableSmrs.Count <= selectedSmrIndex) return;
        var mesh = availableSmrs[selectedSmrIndex].sharedMesh; if (mesh == null) return;
        HashSet<string> allShapes = new HashSet<string>(); for (int i = 0; i < mesh.blendShapeCount; i++) allShapes.Add(mesh.GetBlendShapeName(i));

        foreach (string shape in allShapes)
        {
            if (mirrorShapeMap.ContainsKey(shape)) continue;
            foreach (var rule in MIRROR_REPLACE_RULES)
            {
                if (shape.Contains(rule.l)) { string rTarget = shape.Replace(rule.l, rule.r); if (allShapes.Contains(rTarget)) { mirrorShapeMap[shape] = rTarget; mirrorShapeMap[rTarget] = shape; break; } }
                else if (shape.Contains(rule.r)) { string lTarget = shape.Replace(rule.r, rule.l); if (allShapes.Contains(lTarget)) { mirrorShapeMap[shape] = lTarget; mirrorShapeMap[lTarget] = shape; break; } }
            }
        }
    }

    private void AutoSelectActiveShapes()
    {
        isCopyPasteMode = true; copyTargetShapes.Clear(); clipboardValues.Clear(); copyTargetObjects.Clear(); clipboardObjectValues.Clear();
        foreach (var kvp in currentExpressionValues) if (!Mathf.Approximately(kvp.Value, 0f)) { copyTargetShapes.Add(kvp.Key); clipboardValues[kvp.Key] = kvp.Value; }
        foreach (var kvp in activeObjectValues) { copyTargetObjects.Add(kvp.Key); clipboardObjectValues[kvp.Key] = kvp.Value; }
    }

    private void PasteClipboardValues()
    {
        if ((clipboardValues.Count == 0 && clipboardObjectValues.Count == 0) || availableClips.Count <= selectedClipIndex) return;
        AnimationClip clip = availableClips[selectedClipIndex];
        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("一括貼り付け");
        foreach (var kvp in clipboardValues) CommitShapeKeyValue(clip, kvp.Key, kvp.Value);
        foreach (var kvp in clipboardObjectValues) CommitObjectActiveValue(clip, kvp.Key, kvp.Value);
        Undo.CollapseUndoOperations(group); RefreshExpressionCache(); ApplySorting(); ForceRepaintPreview();
    }

    private void RemoveClipFromManualList(AnimationClip clip)
    {
        if (clip == null) return;
        if (VRC_ExpressionPreview.Instance != null) VRC_ExpressionPreview.Instance.ResetPreviousAnimBlendShapes(clipExpressionValues);
        manuallyCreatedClips.Remove(clip); UpdateAvailableClips(); selectedClipIndex = 0;
        RefreshExpressionCache(); ApplySorting(); ForceRepaintPreview(); SaveCurrentSettings();
    }

    private void SelectNewClipManually(AnimationClip clip)
    {
        if (VRC_ExpressionPreview.Instance != null) VRC_ExpressionPreview.Instance.ResetPreviousAnimBlendShapes(clipExpressionValues);
        if (!manuallyCreatedClips.Contains(clip)) manuallyCreatedClips.Add(clip);
        UpdateAvailableClips(); int idx = availableClips.IndexOf(clip);
        if (idx != -1) { selectedClipIndex = idx; RefreshExpressionCache(); ApplySorting(); ForceRepaintPreview(); }
        SaveCurrentSettings();
    }

    private void DrawShapeKeySliders()
    {
        AnimationClip clip = availableClips[selectedClipIndex];
        float viewWidth = position.width - 16f; if (isFilterWindowOpen) viewWidth -= (filterPanelWidth + 10f);
        float checkboxWidth = isCopyPasteMode ? 18f : 0f;
        float requiredToolsWidth = checkboxWidth + 15f + 15f + 40f + 40f + 25f;
        float labelLimitWidth = viewWidth - requiredToolsWidth;
        float labelWidth = Mathf.Clamp(viewWidth * 0.40f, 75f, Mathf.Min(cachedMaxShapeNameWidth, labelLimitWidth));
        if (lastShapeLabelW != labelWidth) { lastShapeLabelW = labelWidth; optShapeLabelW = GUILayout.Width(labelWidth); }

        if (cachedShapeContents == null || cachedShapeContents.Length != sortedShapeKeyNames.Count)
        {
            cachedShapeContents = new GUIContent[sortedShapeKeyNames.Count];
            for (int i = 0; i < sortedShapeKeyNames.Count; i++)
            {
                cachedShapeContents[i] = new GUIContent(sortedShapeKeyNames[i], sortedShapeKeyNames[i]);
            }
        }

        for (int i = 0; i < sortedShapeKeyNames.Count; i++)
        {
            string shapeName = sortedShapeKeyNames[i]; GUIContent shapeContent = cachedShapeContents[i];
            float currentValue = currentExpressionValues.ContainsKey(shapeName) ? currentExpressionValues[shapeName] : 0f;
            bool isRegistered = registeredShapeKeys.Contains(shapeName);
            GUIStyle currentLabelStyle = isRegistered ? cachedBoldLabelStyle : cachedNormalLabelStyle;
            GUI.contentColor = isRegistered ? (!Mathf.Approximately(currentValue, 0f) ? new Color(0.4f, 1f, 0.4f) : Color.white) : new Color(0.8f, 0.8f, 0.8f);

            EditorGUILayout.BeginHorizontal();
            float currentCheckboxWidth = 0f;
            if (isCopyPasteMode)
            {
                currentCheckboxWidth = 18f; bool isSelected = copyTargetShapes.Contains(shapeName);
                EditorGUI.BeginChangeCheck(); bool selectNew = GUILayout.Toggle(isSelected, "", optW15);
                if (EditorGUI.EndChangeCheck()) { if (selectNew) { copyTargetShapes.Add(shapeName); clipboardValues[shapeName] = currentValue; } else { copyTargetShapes.Remove(shapeName); clipboardValues.Remove(shapeName); } }
            }

            bool isFav = favoriteShapes.Contains(shapeName);
            Color oldCol = GUI.contentColor; GUI.contentColor = isFav ? new Color(1f, 0.8f, 0.2f) : Color.gray;
            if (GUILayout.Button(isFav ? cachedFavOnContent : cachedFavOffContent, EditorStyles.label, optW15)) { if (isFav) favoriteShapes.Remove(shapeName); else favoriteShapes.Add(shapeName); ApplySorting(); SaveCurrentSettings(); GUIUtility.ExitGUI(); }
            GUI.contentColor = oldCol;

            if (GUILayout.Button(isRegistered ? "●" : "○", cachedDotButtonStyle, optW15)) { if (isRegistered) RemoveShapeKeyValue(clip, shapeName); else CommitShapeKeyValue(clip, shapeName, 0f); ApplySorting(); ForceRepaintPreview(); }

            GUILayout.Label(shapeContent, currentLabelStyle, optShapeLabelW);

            float sliderWidth = Mathf.Max(40f, viewWidth - (currentCheckboxWidth + 15f + 15f + labelWidth + 40f + 25f));
            if (lastSliderW != sliderWidth) { lastSliderW = sliderWidth; optSliderW = GUILayout.Width(sliderWidth); }

            EditorGUI.BeginChangeCheck(); float newValue = GUILayout.HorizontalSlider(Mathf.Clamp(currentValue, 0f, 100f), 0f, 100f, optSliderW);
            if (EditorGUI.EndChangeCheck()) UpdateMemoryValueOnly(shapeName, newValue);

            EditorGUI.BeginChangeCheck(); float textValue = EditorGUILayout.FloatField(currentValue, optW40);
            if (EditorGUI.EndChangeCheck()) UpdateMemoryValueOnly(shapeName, textValue);

            EditorGUILayout.EndHorizontal(); GUI.contentColor = Color.white;
        }
    }

    private void UpdateMemoryValueOnly(string shapeName, float value, bool isMirrorCall = false)
    {
        currentExpressionValues[shapeName] = value; dirtyShapeKeys.Add(shapeName); ForceRepaintPreview();
        if (VRC_ExpressionPreview.Instance != null && availableSmrs != null && availableSmrs.Count > selectedSmrIndex)
            if (smrPathCache.TryGetValue(availableSmrs[selectedSmrIndex], out string path)) VRC_ExpressionPreview.Instance.UpdateSingleBlendShapeImmediate(path, shapeName, value);
        if (isCopyPasteMode && copyTargetShapes.Contains(shapeName)) clipboardValues[shapeName] = value;
        if (!isMirrorCall && isMirroringEnabled && mirrorShapeMap.TryGetValue(shapeName, out string partner)) UpdateMemoryValueOnly(partner, value, true);
    }

    private void CommitShapeKeyValue(AnimationClip clip, string shapeName, float value, bool isMirrorCall = false)
    {
        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("表情シェイプ変更");
        SkinnedMeshRenderer mainSmr = availableSmrs[selectedSmrIndex];
        string mainPath = smrPathCache.ContainsKey(mainSmr) ? smrPathCache[mainSmr] : GetRelativePath(mainSmr.gameObject);

        RegisterShapeKeyConstant(clip, EditorCurveBinding.FloatCurve(mainPath, typeof(SkinnedMeshRenderer), "blendShape." + shapeName), value);

        if (autoLinkShapeKeys)
        {
            foreach (var smr in availableSmrs)
            {
                if (smr == mainSmr || smr.sharedMesh == null) continue;
                if (smr.sharedMesh.GetBlendShapeIndex(shapeName) != -1)
                {
                    string path = smrPathCache.ContainsKey(smr) ? smrPathCache[smr] : GetRelativePath(smr.gameObject);
                    RegisterShapeKeyConstant(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shapeName), value);
                }
            }
        }
        if (!isMirrorCall && isMirroringEnabled && mirrorShapeMap.TryGetValue(shapeName, out string partner)) CommitShapeKeyValue(clip, partner, value, true);
        Undo.CollapseUndoOperations(group); ForceRepaintPreview();
    }

    private void RemoveShapeKeyValue(AnimationClip clip, string shapeName, bool isMirrorCall = false)
    {
        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("シェイプキー登録解除");
        SkinnedMeshRenderer mainSmr = availableSmrs[selectedSmrIndex];
        string mainPath = smrPathCache.ContainsKey(mainSmr) ? smrPathCache[mainSmr] : GetRelativePath(mainSmr.gameObject);

        Undo.RecordObject(clip, "シェイプキー削除");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(mainPath, typeof(SkinnedMeshRenderer), "blendShape." + shapeName), null);

        if (clipExpressionValues.TryGetValue(mainPath, out var dict)) dict.Remove(shapeName);
        currentExpressionValues.Remove(shapeName); registeredShapeKeys.Remove(shapeName); dirtyShapeKeys.Remove(shapeName);

        if (autoLinkShapeKeys)
        {
            foreach (var smr in availableSmrs)
            {
                if (smr == mainSmr || smr.sharedMesh == null) continue;
                if (smr.sharedMesh.GetBlendShapeIndex(shapeName) != -1)
                {
                    string path = smrPathCache.ContainsKey(smr) ? smrPathCache[smr] : GetRelativePath(smr.gameObject);
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shapeName), null);
                    if (clipExpressionValues.TryGetValue(path, out var lDict)) lDict.Remove(shapeName);
                }
            }
        }
        if (!isMirrorCall && isMirroringEnabled && mirrorShapeMap.TryGetValue(shapeName, out string partner)) if (registeredShapeKeys.Contains(partner)) RemoveShapeKeyValue(clip, partner, true);
        Undo.CollapseUndoOperations(group);
    }

    private void DrawObjectActivePanel()
    {
        if (availableClips.Count <= selectedClipIndex) return;
        AnimationClip clip = availableClips[selectedClipIndex];
        float viewWidth = position.width - 16f; if (isFilterWindowOpen) viewWidth -= (filterPanelWidth + 10f);
        float checkboxWidth = isCopyPasteMode ? 18f : 0f;
        float labelWidth = Mathf.Clamp(viewWidth * 0.40f, 75f, Mathf.Min(cachedMaxObjNameWidth, viewWidth - (checkboxWidth + 15f + 40f + 25f)));
        if (lastObjLabelW != labelWidth) { lastObjLabelW = labelWidth; optObjLabelW = GUILayout.Width(labelWidth); }

        EditorGUILayout.BeginHorizontal(); GUILayout.Label("オブジェクト ON / OFF", EditorStyles.boldLabel); GUILayout.FlexibleSpace();
        if (GUILayout.Button(jumpUpIconContent, optW20, optH18)) leftScrollPos.y = 0f;
        EditorGUILayout.EndHorizontal();

        if (cachedActiveObjects == null || cachedActiveObjects.Count != activeObjectValues.Count)
        {
            RecalculateObjNameWidth();
        }

        GUILayout.BeginVertical(GUI.skin.box);
        foreach (var cache in cachedActiveObjects)
        {
            string path = cache.path; GUIContent objContent = cache.content;
            bool isActive = activeObjectValues.ContainsKey(path) ? activeObjectValues[path] : false;

            EditorGUILayout.BeginHorizontal();
            float currentCheckboxWidth = 0f;
            if (isCopyPasteMode)
            {
                currentCheckboxWidth = 18f; bool isSelected = copyTargetObjects.Contains(path);
                EditorGUI.BeginChangeCheck(); bool selectNewVal = GUILayout.Toggle(isSelected, "", optW15);
                if (EditorGUI.EndChangeCheck()) { if (selectNewVal) { copyTargetObjects.Add(path); clipboardObjectValues[path] = isActive; } else { copyTargetObjects.Remove(path); clipboardObjectValues.Remove(path); } }
            }

            if (GUILayout.Button("●", cachedDotButtonStyle, optW15)) { RemoveObjectActiveValue(clip, path); ForceRepaintPreview(); EditorGUILayout.EndHorizontal(); GUIUtility.ExitGUI(); }
            GUILayout.Label(objContent, cachedNormalLabelStyle, optObjLabelW);

            float toggleWidth = Mathf.Max(40f, viewWidth - (currentCheckboxWidth + 15f + labelWidth + 25f));
            if (lastToggleW != toggleWidth) { lastToggleW = toggleWidth; optToggleW = GUILayout.Width(toggleWidth); }

            EditorGUI.BeginChangeCheck(); bool newVal = GUILayout.Toggle(isActive, isActive ? " ON" : " OFF", optToggleW);
            if (EditorGUI.EndChangeCheck()) { CommitObjectActiveValue(clip, path, newVal); ForceRepaintPreview(); }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(); GUI.backgroundColor = new Color(0.9f, 0.9f, 1f);
        if (GUILayout.Button("＋ Hierarchyで選択中のオブジェクトを一括登録", optH24))
        {
            foreach (GameObject obj in Selection.gameObjects) { if (obj == rootObject || !obj.transform.IsChildOf(rootObject.transform)) continue; CommitObjectActiveValue(clip, GetRelativePath(obj), obj.activeSelf); }
            ForceRepaintPreview(); GUIUtility.ExitGUI();
        }
        GUI.backgroundColor = Color.white; GUILayout.EndVertical();
    }

    private void CommitObjectActiveValue(AnimationClip clip, string path, bool isActive)
    {
        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("オブジェクト状態変更"); Undo.RecordObject(clip, "オブジェクト状態変更");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"), new AnimationCurve(new Keyframe(0f, isActive ? 1f : 0f)));
        activeObjectValues[path] = isActive; if (isCopyPasteMode && copyTargetObjects.Contains(path)) clipboardObjectValues[path] = isActive;
        if (VRC_ExpressionPreview.Instance != null) VRC_ExpressionPreview.Instance.UpdateSingleObjectActiveImmediate(path, isActive);
        Undo.CollapseUndoOperations(group); RecalculateObjNameWidth();
    }

    private void RemoveObjectActiveValue(AnimationClip clip, string path)
    {
        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("オブジェクト状態解除"); Undo.RecordObject(clip, "オブジェクト状態解除");
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"), null);
        activeObjectValues.Remove(path); if (isCopyPasteMode) { copyTargetObjects.Remove(path); clipboardObjectValues.Remove(path); }
        Undo.CollapseUndoOperations(group); RecalculateObjNameWidth();
    }

    private void DrawActionButtons()
    {
        GUILayout.BeginVertical(GUI.skin.box); GUILayout.Label("表情ユーティリティ / 一括処理", EditorStyles.boldLabel);
        GUILayout.BeginVertical(GUI.skin.box); GUILayout.Label("参照アニメの項目をコピー", EditorStyles.miniBoldLabel);
        referenceClip = (AnimationClip)EditorGUILayout.ObjectField("参照用", referenceClip, typeof(AnimationClip), false);
        if (GUILayout.Button("参照先の全項目を「0」で追加")) ProcessFromReferenceClip();
        EditorGUILayout.Space();
        GUI.backgroundColor = new Color(0.8f, 1f, 0.8f); if (GUILayout.Button("全ての未登録キーを「0」で埋める")) ProcessShapeKeys(); GUI.backgroundColor = Color.white;
        GUILayout.EndVertical();
        EditorGUILayout.Space();
        GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
        if (GUILayout.Button("値がすべて0のトラックを完全に削除")) if (EditorUtility.DisplayDialog("確認", "削除しますか？", "削除", "キャンセル")) DeleteZeroCurves();
        GUI.backgroundColor = Color.white;
        if (isClampBlendShapesEnabledCache)
        {
            EditorGUILayout.Space(); GUI.backgroundColor = new Color(1f, 0.8f, 0.8f);
            if (GUILayout.Button("限界突破有効化（Clamp BlendShapes解除）")) { UncheckClampBlendShapes(); isClampBlendShapesEnabledCache = false; ForceRepaintPreview(); }
            GUI.backgroundColor = Color.white;
        }
        GUILayout.EndVertical();
    }

    public void UpdateCacheArrays()
    {
        clipNamesCache = availableClips.Select(c => c.name).ToArray();
        smrNamesCache = availableSmrs.Select(s => s.name).ToArray();
        testClipNamesCache = new string[availableClips.Count + 1]; testClipNamesCache[0] = "なし (直接指定)";
        for (int i = 0; i < availableClips.Count; i++) testClipNamesCache[i + 1] = availableClips[i].name;

        isMultiFrameCache = (availableClips.Count > selectedClipIndex && selectedClipIndex >= 0) ? CheckIsMultiFrame(availableClips[selectedClipIndex]) : false;

        if (availableSmrs != null && availableSmrs.Count > selectedSmrIndex)
        {
            var smr = availableSmrs[selectedSmrIndex];
            if (smr != null && smr.sharedMesh != null)
            {
                testShapeNamesCache = new string[smr.sharedMesh.blendShapeCount];
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++) testShapeNamesCache[i] = smr.sharedMesh.GetBlendShapeName(i);
            }
        }
    }

    public void UpdateLayerLabelCache()
    {
        if (layerNames == null || layerNames.Count == 0 || selectedLayers == null) { if (cachedLayerContent != null) cachedLayerContent.text = "選択なし (なし)"; return; }
        int selectedCount = 0; int lastSelectedIndex = -1;
        for (int i = 0; i < selectedLayers.Length; i++) if (selectedLayers[i]) { selectedCount++; lastSelectedIndex = i; }
        string result = "複数選択中...";
        if (selectedCount == selectedLayers.Length) result = "全レイヤー (All)";
        else if (selectedCount == 1 && lastSelectedIndex >= 0) result = layerNames[lastSelectedIndex];
        else if (selectedCount == 0) result = "選択なし (なし)";
        if (cachedLayerContent == null) cachedLayerContent = new GUIContent(result); else cachedLayerContent.text = result;
    }

    public void RefreshData() { RefreshData(false); }
    public void RefreshData(bool loadFromSettings)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;

        if (rootObject == null)
        {
            availableSmrs = new List<SkinnedMeshRenderer>(); availableClips.Clear(); layerNames.Clear();
            currentExpressionValues.Clear(); cachedControllers.Clear(); baseShapeKeyBackup.Clear();
            manuallyCreatedClips.Clear(); selectedClipIndex = 0; selectedSmrIndex = 0; selectedLayers = new bool[0];
            UpdateCacheArrays(); UpdateLayerLabelCache(); return;
        }

        if (rootObject != lastSelectionObject)
        {
            manuallyCreatedClips.Clear(); favoriteShapes.Clear(); detailFilterWords.Clear(); detailFilterActives.Clear();
            referenceClip = null; autoLinkShapeKeys = true; isMirroringEnabled = false; lastSelectionObject = rootObject;
        }

        string prevSmr = (availableSmrs != null && availableSmrs.Count > selectedSmrIndex) ? availableSmrs[selectedSmrIndex].name : "";
        string prevClipName = (availableClips.Count > selectedClipIndex) ? availableClips[selectedClipIndex].name : "";

        Dictionary<string, bool> prevLayers = new Dictionary<string, bool>();
        for (int i = 0; i < layerNames.Count; i++) if (i < selectedLayers.Length) prevLayers[layerNames[i]] = selectedLayers[i];

        availableSmrs = new List<SkinnedMeshRenderer>(); availableClips.Clear(); layerNames.Clear(); currentExpressionValues.Clear(); cachedControllers.Clear();
        baseShapeKeyBackup.Clear();
        availableSmrs = rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderBy(s => s.name.ToLower() == "body" ? 0 : 1).ThenBy(s => s.name).ToList();

        smrPathCache.Clear();
        foreach (var smr in availableSmrs) if (smr != null) smrPathCache[smr] = GetRelativePath(smr.gameObject);

        foreach (var smr in availableSmrs)
        {
            if (smr != null && smr.sharedMesh != null)
            {
                var dict = new Dictionary<string, float>();
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++) dict[smr.sharedMesh.GetBlendShapeName(i)] = smr.GetBlendShapeWeight(i);
                baseShapeKeyBackup[smrPathCache[smr]] = dict;
            }
        }

        Animator animator = rootObject.GetComponentInParent<Animator>();
        if (animator != null && animator.runtimeAnimatorController is AnimatorController ac) cachedControllers.Add(ac);

        Component descriptor = rootObject.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "VRCAvatarDescriptor");
        if (descriptor != null)
        {
            SerializedProperty baseLayers = new SerializedObject(descriptor).FindProperty("baseAnimationLayers");
            if (baseLayers != null)
                for (int i = 0; i < baseLayers.arraySize; i++)
                {
                    var cp = baseLayers.GetArrayElementAtIndex(i).FindPropertyRelative("animatorController");
                    if (cp != null && cp.objectReferenceValue is AnimatorController vac && !cachedControllers.Contains(vac)) cachedControllers.Add(vac);
                }
        }

        layerNames.Clear();
        foreach (var ctrl in cachedControllers) foreach (var l in ctrl.layers) layerNames.Add(ctrl.name + " - " + l.name);

        var entry = settingsAsset?.GetEntry(GetAvatarID());
        if (loadFromSettings && entry != null)
        {
            manuallyCreatedClips.Clear();
            foreach (var path in entry.manualClipPaths) { AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path); if (clip != null && !manuallyCreatedClips.Contains(clip)) manuallyCreatedClips.Add(clip); }
            referenceClip = string.IsNullOrEmpty(entry.lastReferenceClipPath) ? null : AssetDatabase.LoadAssetAtPath<AnimationClip>(entry.lastReferenceClipPath);
            favoriteShapes.Clear(); if (entry.favoriteShapes != null) foreach (var fav in entry.favoriteShapes) favoriteShapes.Add(fav);
            autoLinkShapeKeys = entry.autoLinkShapeKeys; isMirroringEnabled = entry.isMirroringEnabled;
            detailFilterWords.Clear(); if (entry.filterWords != null) detailFilterWords.AddRange(entry.filterWords);
            detailFilterActives.Clear(); if (entry.filterActives != null) detailFilterActives.AddRange(entry.filterActives);
            if (!string.IsNullOrEmpty(entry.lastClipPath)) prevClipName = System.IO.Path.GetFileNameWithoutExtension(entry.lastClipPath);
        }

        if (settingsAsset != null)
        {
            isFilterWindowOpen = settingsAsset.isFilterWindowOpen; filterPanelWidth = settingsAsset.filterPanelWidth;
            if (filterPanelWidth < 100f) filterPanelWidth = 150f;
        }

        if (detailFilterWords.Count == 0) { detailFilterWords.Add(""); detailFilterActives.Add(true); }

        selectedLayers = new bool[layerNames.Count];
        for (int i = 0; i < selectedLayers.Length; i++)
        {
            if (loadFromSettings && entry != null && i < entry.lastSelectedLayers.Count) selectedLayers[i] = entry.lastSelectedLayers[i];
            else if (prevLayers.TryGetValue(layerNames[i], out bool state)) selectedLayers[i] = state;
            else selectedLayers[i] = true;
        }

        UpdateAvailableClips();

        selectedSmrIndex = 0;
        if (loadFromSettings && entry != null) { int sIdx = availableSmrs.FindIndex(s => s.name == entry.lastSmrName); if (sIdx != -1) selectedSmrIndex = sIdx; }
        else if (!string.IsNullOrEmpty(prevSmr)) { int i = availableSmrs.FindIndex(s => s.name == prevSmr); if (i != -1) selectedSmrIndex = i; }

        selectedClipIndex = 0;
        if (!string.IsNullOrEmpty(prevClipName)) { int i = availableClips.FindIndex(c => c.name == prevClipName); if (i != -1) selectedClipIndex = i; }

        BuildMirrorMap(); UpdateCacheArrays(); UpdateLayerLabelCache(); RefreshExpressionCache(); ApplySorting();
        if (loadFromSettings) SaveCurrentSettings();

        if (VRC_ExpressionPreview.Instance != null) { VRC_ExpressionPreview.Instance.UpdateSceneLights(this); VRC_ExpressionPreview.Instance.ForceRebuildDummy(); }
        isClampBlendShapesEnabledCache = IsClampBlendShapesEnabled();
    }

    public void RefreshExpressionCache()
    {
        currentExpressionValues.Clear(); clipExpressionValues.Clear(); registeredShapeKeys.Clear(); dirtyShapeKeys.Clear(); activeObjectValues.Clear();
        if (availableClips.Count <= selectedClipIndex || availableSmrs == null || availableSmrs.Count <= selectedSmrIndex) return;

        AnimationClip clip = availableClips[selectedClipIndex];
        SkinnedMeshRenderer targetSmr = availableSmrs[selectedSmrIndex];
        string targetPath = smrPathCache.ContainsKey(targetSmr) ? smrPathCache[targetSmr] : GetRelativePath(targetSmr.gameObject);
        GameObject dummy = VRC_ExpressionPreview.Instance?.GetPreviewDummy();
        if (dummy != null) clip.SampleAnimation(dummy, 0f);

        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape."))
            {
                string name = binding.propertyName.Replace("blendShape.", ""); float val = 0f; bool fetched = false;
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null && curve.keys.Length > 0) { val = curve.keys[0].value; fetched = true; }

                if (!fetched && dummy != null)
                {
                    Transform t = string.IsNullOrEmpty(binding.path) ? dummy.transform : dummy.transform.Find(binding.path);
                    if (t != null && t.GetComponent<SkinnedMeshRenderer>() is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        int idx = smr.sharedMesh.GetBlendShapeIndex(name); if (idx != -1) { val = smr.GetBlendShapeWeight(idx); fetched = true; }
                    }
                }
                if (!clipExpressionValues.ContainsKey(binding.path)) clipExpressionValues[binding.path] = new Dictionary<string, float>();
                clipExpressionValues[binding.path][name] = val;
                if (binding.path == targetPath) { currentExpressionValues[name] = val; registeredShapeKeys.Add(name); }
            }
            else if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null && curve.keys.Length > 0) activeObjectValues[binding.path] = curve.keys[0].value > 0.5f;
            }
        }
        RecalculateObjNameWidth();
    }

    private void RecalculateObjNameWidth()
    {
        cachedMaxObjNameWidth = 75f; GUIStyle labelStyle = new GUIStyle(EditorStyles.label); cachedActiveObjects.Clear();
        foreach (var kvp in activeObjectValues)
        {
            string path = kvp.Key;
            string objName = path.Contains("/") ? path.Substring(path.LastIndexOf("/") + 1) : path;
            if (string.IsNullOrEmpty(objName)) objName = "アバター直下";

            cachedActiveObjects.Add(new ActiveObjectCache { path = path, content = new GUIContent(objName, path) });

            float nameWidth = labelStyle.CalcSize(new GUIContent(objName)).x;
            if (nameWidth > cachedMaxObjNameWidth) cachedMaxObjNameWidth = nameWidth;
        }
        cachedMaxObjNameWidth += 10f;
    }

    public void UpdateAvailableClips()
    {
        if (rootObject == null) return;

        string lastClipName = (availableClips.Count > selectedClipIndex && selectedClipIndex >= 0) ? availableClips[selectedClipIndex].name : "";
        HashSet<AnimationClip> clipSet = new HashSet<AnimationClip>();

        int layerIdx = 0;
        foreach (var ctrl in cachedControllers)
        {
            for (int i = 0; i < ctrl.layers.Length; i++)
            {
                if (layerIdx < selectedLayers.Length && selectedLayers[layerIdx])
                    ExtractClipsFromStateMachine(ctrl.layers[i].stateMachine, clipSet);
                layerIdx++;
            }
        }

        foreach (var mClip in manuallyCreatedClips) if (mClip != null) clipSet.Add(mClip);
        availableClips = clipSet.Where(c => c != null).Distinct().OrderBy(c => c.name).ToList();

        selectedClipIndex = 0;
        if (!string.IsNullOrEmpty(lastClipName))
        {
            int foundIdx = availableClips.FindIndex(c => c.name == lastClipName);
            if (foundIdx != -1) selectedClipIndex = foundIdx;
        }

        UpdateCacheArrays();
        Repaint();
    }

    public void ApplySorting()
    {
        if (availableSmrs == null || availableSmrs.Count <= selectedSmrIndex || availableClips.Count <= selectedClipIndex) return;
        SkinnedMeshRenderer smr = availableSmrs[selectedSmrIndex];
        var list = new List<string>(); for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++) list.Add(smr.sharedMesh.GetBlendShapeName(i));

        var activeFilters = new List<string>();
        for (int i = 0; i < detailFilterWords.Count; i++)
        {
            if (i < detailFilterActives.Count && detailFilterActives[i] && !string.IsNullOrEmpty(detailFilterWords[i]))
            {
                activeFilters.Add(detailFilterWords[i]);
            }
        }

        sortedShapeKeyNames = list
            .Where(name => {
                if (activeFilters.Count > 0)
                {
                    bool matchedOr = false;
                    foreach (var filter in activeFilters)
                    {
                        if (name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matchedOr = true;
                            break;
                        }
                    }
                    if (!matchedOr) return false;
                }

                if (!string.IsNullOrEmpty(searchFilter))
                {
                    if (name.IndexOf(searchFilter, System.StringComparison.OrdinalIgnoreCase) < 0) return false;
                }

                return true;
            })
            .OrderBy(name => {
                return favoriteShapes.Contains(name) ? 0 : 1;
            }).ThenBy(name => {
                if (currentSortMode == SortMode.Default) return list.IndexOf(name);

                if (currentSortMode == SortMode.ActiveFirst)
                {
                    bool isRegistered = registeredShapeKeys.Contains(name);
                    currentExpressionValues.TryGetValue(name, out float val);
                    return (isRegistered && !Mathf.Approximately(val, 0f)) ? 0 : (isRegistered ? 1 : 2);
                }
                return registeredShapeKeys.Contains(name) ? 0 : 1;
            }).ThenBy(name => list.IndexOf(name)).ToList();

        cachedMaxShapeNameWidth = 75f;
        GUIStyle labelStyle = new GUIStyle(EditorStyles.label);

        cachedShapeContents = new GUIContent[sortedShapeKeyNames.Count];

        for (int i = 0; i < sortedShapeKeyNames.Count; i++)
        {
            string shapeName = sortedShapeKeyNames[i];
            cachedShapeContents[i] = new GUIContent(shapeName, shapeName);

            float nameWidth = labelStyle.CalcSize(cachedShapeContents[i]).x;
            if (nameWidth > cachedMaxShapeNameWidth) cachedMaxShapeNameWidth = nameWidth;
        }
        cachedMaxShapeNameWidth += 10f;
    }

    private void ExtractClipsFromStateMachine(AnimatorStateMachine sm, HashSet<AnimationClip> clipSet) { if (sm == null) return; foreach (var state in sm.states) ExtractClipsFromMotion(state.state.motion, clipSet); foreach (var subSm in sm.stateMachines) ExtractClipsFromStateMachine(subSm.stateMachine, clipSet); }
    private void ExtractClipsFromMotion(Motion motion, HashSet<AnimationClip> clipSet) { if (motion is AnimationClip clip) clipSet.Add(clip); else if (motion is BlendTree tree) foreach (var child in tree.children) ExtractClipsFromMotion(child.motion, clipSet); }

    private void CreateNewAnimationClip()
    {
        string path = EditorUtility.SaveFilePanelInProject("新規表情アニメを作成", rootObject != null ? rootObject.name + "_表情" : "NewExpression", "anim", "");
        if (string.IsNullOrEmpty(path)) return;
        AnimationClip newClip = new AnimationClip();
        var settings = AnimationUtility.GetAnimationClipSettings(newClip); settings.loopTime = false; AnimationUtility.SetAnimationClipSettings(newClip, settings);
        AssetDatabase.CreateAsset(newClip, path); AssetDatabase.SaveAssets();
        AnimationClip loadedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (loadedClip != null) SelectNewClipManually(loadedClip);
        AssetDatabase.Refresh();
    }

    private void RegisterShapeKeyConstant(AnimationClip clip, EditorCurveBinding binding, float value)
    {
        Undo.RecordObject(clip, "シェイプキー変更");
        AnimationCurve curve = new AnimationCurve(new Keyframe(0f, value));
        AnimationUtility.SetEditorCurve(clip, binding, curve);
        string shapeName = binding.propertyName.Replace("blendShape.", ""); string path = binding.path;
        if (!clipExpressionValues.ContainsKey(path)) clipExpressionValues[path] = new Dictionary<string, float>();
        clipExpressionValues[path][shapeName] = value;
        if (availableSmrs != null && availableSmrs.Count > selectedSmrIndex && path == GetRelativePath(availableSmrs[selectedSmrIndex].gameObject)) { currentExpressionValues[shapeName] = value; registeredShapeKeys.Add(shapeName); }
    }

    private void ProcessShapeKeys()
    {
        AnimationClip clip = availableClips[selectedClipIndex]; SkinnedMeshRenderer smr = availableSmrs[selectedSmrIndex];
        string path = smrPathCache.ContainsKey(smr) ? smrPathCache[smr] : GetRelativePath(smr.gameObject);

        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("全シェイプキー0埋め"); Undo.RecordObject(clip, "シェイプ登録");
        for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
        {
            string name = smr.sharedMesh.GetBlendShapeName(i);
            if (AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + name)) == null) CommitShapeKeyValue(clip, name, 0f);
        }
        Undo.CollapseUndoOperations(group); AssetDatabase.SaveAssets(); RefreshExpressionCache(); ApplySorting(); ForceRepaintPreview();
    }

    private void ProcessFromReferenceClip()
    {
        if (referenceClip == null) return; AnimationClip targetClip = availableClips[selectedClipIndex]; SkinnedMeshRenderer smr = availableSmrs[selectedSmrIndex];
        string path = smrPathCache.ContainsKey(smr) ? smrPathCache[smr] : GetRelativePath(smr.gameObject);

        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("参照元コピー"); Undo.RecordObject(targetClip, "参照元から追加");
        foreach (var binding in AnimationUtility.GetCurveBindings(referenceClip))
        {
            if (binding.propertyName.StartsWith("blendShape."))
            {
                string name = binding.propertyName.Replace("blendShape.", "");
                if (AnimationUtility.GetEditorCurve(targetClip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + name)) == null) CommitShapeKeyValue(targetClip, name, 0f);
            }
        }
        Undo.CollapseUndoOperations(group); AssetDatabase.SaveAssets(); RefreshExpressionCache(); ApplySorting(); ForceRepaintPreview();
    }

    private void DeleteZeroCurves()
    {
        AnimationClip clip = availableClips[selectedClipIndex];
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("値が0のトラックを全削除");

        Undo.RecordObject(clip, "削除");
        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            if (!binding.propertyName.StartsWith("blendShape.")) continue;
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve != null && curve.keys.All(k => Mathf.Approximately(k.value, 0f))) AnimationUtility.SetEditorCurve(clip, binding, null);
        }
        Undo.CollapseUndoOperations(group); RefreshExpressionCache(); ApplySorting(); ForceRepaintPreview();
    }

    public string GetRelativePath(Transform root, Transform target)
    {
        if (target == root || target == null) return ""; string path = target.name; Transform p = target.parent;
        while (p != null && p != root) { path = p.name + "/" + path; p = p.parent; }
        return path;
    }

    public string GetRelativePath(GameObject obj) => obj == null || rootObject == null ? "" : GetRelativePath(rootObject.transform, obj.transform);
    public void ForceRepaintPreview() { if (VRC_ExpressionPreview.Instance != null) { VRC_ExpressionPreview.Instance.MarkPreviewDirty(); VRC_ExpressionPreview.Instance.Repaint(); } }
    private bool CheckIsMultiFrame(AnimationClip clip) { if (clip == null) return false; foreach (var binding in AnimationUtility.GetCurveBindings(clip)) { var curve = AssetDatabase.GetAssetPath(clip) == "" ? null : AnimationUtility.GetEditorCurve(clip, binding); if (curve != null && curve.keys.Length > 1 && curve.keys.Any(k => k.time > 0.01f)) return true; } return false; }
}

public class ExpressionEditorLayerPopup : PopupWindowContent
{
    private VRC_ExpressionEditor manager; private Vector2 scrollPos;
    public ExpressionEditorLayerPopup(VRC_ExpressionEditor manager) { this.manager = manager; }
    public override Vector2 GetWindowSize() => new Vector2(250, Mathf.Min(70 + (manager.layerNames.Count * EditorGUIUtility.singleLineHeight), 400));
    public override void OnGUI(Rect rect)
    {
        GUILayout.BeginHorizontal(); GUILayout.Label("レイヤーを複数選択", EditorStyles.boldLabel); GUILayout.FlexibleSpace();
        if (GUILayout.Button("X", GUILayout.Width(25), GUILayout.Height(18))) editorWindow.Close(); GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("全選択")) { for (int i = 0; i < manager.selectedLayers.Length; i++) manager.selectedLayers[i] = true; manager.UpdateLayerLabelCache(); manager.UpdateAvailableClips(); manager.SaveCurrentSettings(); }
        if (GUILayout.Button("全解除")) { for (int i = 0; i < manager.selectedLayers.Length; i++) manager.selectedLayers[i] = false; manager.UpdateLayerLabelCache(); manager.UpdateAvailableClips(); manager.SaveCurrentSettings(); }
        GUILayout.EndHorizontal();

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider); scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
        for (int i = 0; i < manager.layerNames.Count; i++)
        {
            EditorGUI.BeginChangeCheck();
            manager.selectedLayers[i] = EditorGUILayout.ToggleLeft(manager.layerNames[i], manager.selectedLayers[i]);
            if (EditorGUI.EndChangeCheck())
            {
                manager.UpdateLayerLabelCache();
                manager.UpdateAvailableClips();
                manager.SaveCurrentSettings();
            }
        }
        EditorGUILayout.EndScrollView();
    }
}