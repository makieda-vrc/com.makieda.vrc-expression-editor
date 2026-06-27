using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.Collections.Generic;
using System.Linq;
using System.IO;

// 【統合パッケージ】これ一つに今の数値（掲示板）とカーブ（倉庫）をまとめる
public class ExpressionTrack
{
    public string label;          // 名前（例: Smile）
    public string path;           // 住所
    public System.Type type;      // 種類（メッシュか物体か）
    public string propertyName;   // プロパティ名
    public float currentValue;    // 【掲示板】プレビューが実際に見る数値
    public AnimationCurve curve;  // 【倉庫】アニメーションの全データ
}

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
    public Dictionary<string, List<ExpressionTrack>> warehouse = new Dictionary<string, List<ExpressionTrack>>();

    // 【エディタ全体の共通情報】今いじっている時間（秒）。デフォルトは0秒（0F）
    public float currentTime = 0f;

    // 【新機能】クリップがどのレイヤーのどのステートにあるかを記録する辞書
    public Dictionary<AnimationClip, List<string>> clipLocationCache = new Dictionary<AnimationClip, List<string>>();

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

    // ★追加：現在タイムラインの絞り込み対象になっているシェイプキーの名前を保持する一時キャッシュ
    public HashSet<string> timelineFilteredShapes = new HashSet<string>();

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
    private GUIContent trashIconContent;
    private GUIContent gridIconContent;
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
    private bool wasPlaying;
    // ★追加：並び替えの予約フラグ
    private bool needsSorting = false;

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

    public bool IsDraggingSlider()
    {
        // 現在フォーカスがある（アクティブな）ウィンドウが、このメイン画面ではないなら、
        // スライダーを操作しているはずがないので、強制的に「操作中ではない(false)」と判定する
        if (EditorWindow.focusedWindow != this) return false;

        // メイン画面がアクティブな時だけ、つかみ判定（hotControl）をチェックする
        return GUIUtility.hotControl != 0;
    }

    [MenuItem("Tools/VRC Expression Editor")]
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

    private void OnDestroy()
    {
        if (VRC_ExpressionThumbnailWindow.Instance != null)
        {
            VRC_ExpressionThumbnailWindow.Instance.Close();
        }
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
        if (VRC_ExpressionTimeline.Instance != null)
        {
            VRC_ExpressionTimeline.Instance.UpdateKeyframeCache(this);
        }
        // プレビューはすでに最新になっているので、ここでの更新命令は完全に削除します
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

    public string GetAvatarID()
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
            trashIconContent = EditorGUIUtility.IconContent("TreeEditor.Trash"); trashIconContent.tooltip = "このアニメを編集リストから除外する";
            gridIconContent = EditorGUIUtility.IconContent("d_Mesh Icon"); gridIconContent.tooltip = "表情サムネイル一覧を開く";
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
        // ★追加：一時キャッシュの初期化
        if (timelineFilteredShapes == null) timelineFilteredShapes = new HashSet<string>();

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
        if (clipLocationCache == null) clipLocationCache = new Dictionary<AnimationClip, List<string>>();
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
        // ★追加：再生中かどうかを判定
        bool isPlaying = VRC_ExpressionTimeline.Instance != null && VRC_ExpressionTimeline.Instance.IsPlaying();

        // ★追加：再生状態が変わった瞬間に、自分（エディタ窓）を強制的に再描画させる
        // これにより、クリックしなくても即座にグレーアウトされるようになります
        if (isPlaying != wasPlaying)
        {
            wasPlaying = isPlaying;
            Repaint();
        }
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
            if (EditorGUI.EndChangeCheck()) { 
                if (VRC_ExpressionPreview.Instance != null) VRC_ExpressionPreview.Instance.ResetPreviousAnimBlendShapes(clipExpressionValues);
                UpdateCacheArrays();
                RefreshExpressionCache();
                ApplySorting();
                ForceRepaintPreview(true);
            }
        }
        else EditorGUILayout.LabelField("なし");
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

        bool isThumbWindowOpen = VRC_ExpressionThumbnailWindow.Instance != null;
        if (isThumbWindowOpen) GUI.backgroundColor = new Color(0.7f, 0.9f, 1f);
        if (GUILayout.Button(gridIconContent, optW24, optH18))
        {
            if (isThumbWindowOpen)
                VRC_ExpressionThumbnailWindow.Instance.Close();
            else
                VRC_ExpressionThumbnailWindow.OpenWindow();
        }
        GUI.backgroundColor = Color.white;

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

        // ★追加：ここから下の「設定・編集UI」をすべてロック（無効化）する
        EditorGUI.BeginDisabledGroup(isPlaying);

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
        if (GUI.Button(jumpRect, jumpDownIconContent)) rightScrollPos.y = 99999f;

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
        // ★追加：ここでロックを解除
        EditorGUI.EndDisabledGroup();
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
        if (VRC_ExpressionPreview.Instance != null) { VRC_ExpressionPreview.Instance.ResetPreviousAnimBlendShapes(clipExpressionValues); }
        if (!manuallyCreatedClips.Contains(clip)) { manuallyCreatedClips.Add(clip); }
        UpdateAvailableClips(); int idx = availableClips.IndexOf(clip);
        if (idx != -1)
        {
            selectedClipIndex = idx;
            UpdateCacheArrays(); // ★ここで新しいアニメーションの警告判定を再計算します
            RefreshExpressionCache();
            ApplySorting();
            ForceRepaintPreview();
        }
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

        string currentPath = (availableSmrs != null && availableSmrs.Count > selectedSmrIndex)
            ? GetRelativePath(availableSmrs[selectedSmrIndex].gameObject) : "";

        // マウスの位置を取得
        Vector2 mousePos = Event.current.mousePosition;

        for (int i = 0; i < sortedShapeKeyNames.Count; i++)
        {
            string shapeName = sortedShapeKeyNames[i];
            GUIContent shapeContent = cachedShapeContents[i];
            float currentValue = currentExpressionValues.ContainsKey(shapeName) ? currentExpressionValues[shapeName] : 0f;
            bool isRegistered = registeredShapeKeys.Contains(shapeName);

            // 1. 【判定】今のフレーム（currentTime）にキーがあるか調べる
            bool hasKeyAtCurrentTime = false;
            if (isRegistered && warehouse.TryGetValue(currentPath, out var tracks))
            {
                var track = tracks.Find(t => t.label == shapeName);
                if (track != null)
                {
                    foreach (var key in track.curve.keys)
                    {
                        if (Mathf.Approximately(key.time, currentTime))
                        {
                            hasKeyAtCurrentTime = true;
                            break;
                        }
                    }
                }
            }

            // 2. 【決定】状態に合わせて「色」と「記号（マーク）」を決める
            Color labelColor;
            string iconMark = hasKeyAtCurrentTime ? "●" : "⦿";

            if (!isRegistered)
            {
                labelColor = Color.gray;
                iconMark = "○";
            }
            else if (hasKeyAtCurrentTime)
            {
                labelColor = Mathf.Approximately(currentValue, 0f) ? new Color(0.4f, 0.8f, 1f) : new Color(0.4f, 1f, 0.4f);
            }
            else
            {
                labelColor = Mathf.Approximately(currentValue, 0f) ? Color.white : new Color(1f, 0.6f, 0.2f);
            }

            GUIStyle currentLabelStyle = isRegistered ? cachedBoldLabelStyle : cachedNormalLabelStyle;
            GUI.contentColor = labelColor;

            // スライダーの行（水平グループ）の範囲を記録
            Rect rowRect = EditorGUILayout.BeginHorizontal();

            // ★変更：選択中なら横全体を濃い青、選択されておらずホバー中なら薄い青を塗る
            bool isFiltered = timelineFilteredShapes.Contains(shapeName);
            if (Event.current.type == EventType.Repaint)
            {
                if (isFiltered)
                {
                    // 選択中：Unity標準の選択青色で横全体を塗りつぶす
                    EditorGUI.DrawRect(rowRect, new Color(0.172f, 0.364f, 0.529f, 1.0f));
                }
                else if (rowRect.Contains(mousePos))
                {
                    // ホバー中：うっすらとした青
                    EditorGUI.DrawRect(rowRect, new Color(0.3f, 0.5f, 0.8f, 0.12f));
                }
            }

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

            // 左クリック時の挙動（このフレームのキーだけを ON / OFF）
            if (GUILayout.Button(iconMark, cachedDotButtonStyle, optW15))
            {
                if (!isRegistered)
                {
                    CommitShapeKeyValue(clip, shapeName, 0f);
                }
                else if (hasKeyAtCurrentTime)
                {
                    RemoveKeyframeAtTime(clip, currentPath, shapeName, currentTime);
                }
                else
                {
                    CommitShapeKeyValue(clip, shapeName, currentValue);
                }

                // ★追加：キーの追加・削除後にエディタの情報を更新し、タイムライン側へ即座に反映・通知する
                RefreshExpressionCache();
                if (VRC_ExpressionTimeline.Instance != null)
                {
                    VRC_ExpressionTimeline.Instance.UpdateKeyframeCache(this);
                    VRC_ExpressionTimeline.Instance.Repaint();
                }

                ForceRepaintPreview();
            }

            // ★変更：クリック判定とハイライト描画機能付きのラベル処理
            // 2. ラベル用の領域（Rect）をあらかじめ確保して描画準備
            Rect labelRect = GUILayoutUtility.GetRect(shapeContent, currentLabelStyle, optShapeLabelW);

            // 3. 描画（Repaint）イベントのときに、文字を綺麗に塗る
            if (Event.current.type == EventType.Repaint)
            {
                if (isFiltered)
                {
                    // 文字色を「白」に反転させて描画（背景は変更1の処理で横全体に描画済み）
                    Color originalTextColor = currentLabelStyle.normal.textColor;
                    currentLabelStyle.normal.textColor = Color.white;
                    GUI.Label(labelRect, shapeContent, currentLabelStyle);
                    currentLabelStyle.normal.textColor = originalTextColor; // 元の色に戻す
                }
                else
                {
                    // 非選択時は通常通り描画
                    GUI.Label(labelRect, shapeContent, currentLabelStyle);
                }
            }

            // 4. マウス左クリック時の判定（MouseDownイベントかつ左ボタン＝0 の時だけ超軽量に動作）
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                // マウスがこのラベルRectの範囲内にあるか判定
                if (labelRect.Contains(Event.current.mousePosition))
                {
                    if (Event.current.shift)
                    {
                        // Shiftキーを押しながらクリック：複数選択（トグル）
                        if (isFiltered) timelineFilteredShapes.Remove(shapeName);
                        else timelineFilteredShapes.Add(shapeName);
                    }
                    else
                    {
                        // 通常クリック：単一選択
                        if (isFiltered && timelineFilteredShapes.Count == 1)
                        {
                            // すでに自分だけが選択されていたら解除
                            timelineFilteredShapes.Clear();
                        }
                        else
                        {
                            // 他をすべてクリアして自分だけを選択
                            timelineFilteredShapes.Clear();
                            timelineFilteredShapes.Add(shapeName);
                        }
                    }

                    // タイムラインのキャッシュを再計算させて、表示を更新
                    if (VRC_ExpressionTimeline.Instance != null)
                    {
                        VRC_ExpressionTimeline.Instance.UpdateKeyframeCache(this);
                    }

                    // 画面の再描画を命令
                    Repaint();
                    if (VRC_ExpressionTimeline.Instance != null) VRC_ExpressionTimeline.Instance.Repaint();

                    // イベントを消費（他のUIにクリックを貫通させない）
                    Event.current.Use();
                }
            }

            float sliderWidth = Mathf.Max(40f, viewWidth - (currentCheckboxWidth + 15f + 15f + labelWidth + 40f + 25f));
            if (lastSliderW != sliderWidth) { lastSliderW = sliderWidth; optSliderW = GUILayout.Width(sliderWidth); }

            EditorGUI.BeginChangeCheck(); float newValue = GUILayout.HorizontalSlider(Mathf.Clamp(currentValue, 0f, 100f), 0f, 100f, optSliderW);
            if (EditorGUI.EndChangeCheck()) UpdateMemoryValueOnly(shapeName, newValue);

            EditorGUI.BeginChangeCheck(); float textValue = EditorGUILayout.FloatField(currentValue, optW40);
            if (EditorGUI.EndChangeCheck()) UpdateMemoryValueOnly(shapeName, textValue);

            EditorGUILayout.EndHorizontal();
            GUI.contentColor = Color.white;

            // ★追加②：【名前入り右クリックメニュー ＆ 確認ポップアップ廃止】
            // スライダーの行を右クリックしたときに、対象の名前をメニューに表示して、即座に削除する
            if (Event.current.type == EventType.ContextClick && rowRect.Contains(mousePos))
            {
                GenericMenu menu = new GenericMenu();
                // メニュー自体に「『Smile』をアニメから削除」と表示させます
                menu.AddItem(new GUIContent($"「{shapeName}」をアニメから削除"), false, () => {
                    RemoveShapeKeyValue(clip, shapeName); // ポップアップを挟まず、即座に削除！
                });
                menu.ShowAsContext();
                Event.current.Use();
            }
        }
    }

    private void UpdateMemoryValueOnly(string shapeName, float value, bool isMirrorCall = false)
    {
        // 1. UI表示用のメモを更新
        currentExpressionValues[shapeName] = value;

        // 2. 「後で保存してねリスト」に登録
        dirtyShapeKeys.Add(shapeName);

        // 3. 【3層構造：掲示板の更新】
        string currentPath = (availableSmrs != null && availableSmrs.Count > selectedSmrIndex)
            ? GetRelativePath(availableSmrs[selectedSmrIndex].gameObject) : "";

        if (warehouse.ContainsKey(currentPath))
        {
            var tracks = warehouse[currentPath];
            var targetTrack = tracks.Find(t => t.label == shapeName);
            if (targetTrack != null)
            {
                targetTrack.currentValue = value; // 掲示板を更新
            }
        }

        // ★【ここが抜けていました！配線の復元】
        // プレビューアバターの「動かしたシェイプキーだけ」を狙い撃ちで直接書き換える
        if (VRC_ExpressionPreview.Instance != null && availableSmrs != null && availableSmrs.Count > selectedSmrIndex)
        {
            if (smrPathCache.TryGetValue(availableSmrs[selectedSmrIndex], out string path))
            {
                // プレビュー側にある「狙い撃ち更新関数」に直接値を送る
                VRC_ExpressionPreview.Instance.UpdateSingleBlendShapeImmediate(path, shapeName, value);
            }
        }

        // 4. プレビュー画面の塗り直しだけを命令する（全体リセットは走らないので爆速）
        ForceRepaintPreview(false);

        // 5. 【機能維持】コピペモードの処理
        if (isCopyPasteMode && copyTargetShapes.Contains(shapeName)) clipboardValues[shapeName] = value;

        // 6. 【機能維持】左右対称（ミラー）モードの処理
        if (!isMirrorCall && isMirroringEnabled && mirrorShapeMap.TryGetValue(shapeName, out string partner))
        {
            UpdateMemoryValueOnly(partner, value, true);
        }
    }

    // ★カッコの末尾に customTime = -1f を追加
    private void CommitShapeKeyValue(AnimationClip clip, string shapeName, float value, bool isMirrorCall = false, float customTime = -1f)
    {
        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("表情シェイプ変更");
        SkinnedMeshRenderer mainSmr = availableSmrs[selectedSmrIndex];
        string mainPath = smrPathCache.ContainsKey(mainSmr) ? smrPathCache[mainSmr] : GetRelativePath(mainSmr.gameObject);

        // ★最後の引数に customTime を追加してバトンタッチする
        RegisterShapeKeyConstant(clip, EditorCurveBinding.FloatCurve(mainPath, typeof(SkinnedMeshRenderer), "blendShape." + shapeName), value, customTime);

        if (autoLinkShapeKeys)
        {
            foreach (var smr in availableSmrs)
            {
                if (smr == mainSmr || smr.sharedMesh != null) continue;
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

        // ★追加：メモリ上の棚（warehouse）からも完全に削除して整合性を保つ
        if (warehouse.TryGetValue(mainPath, out var tracks))
        {
            tracks.RemoveAll(t => t.label == shapeName);
        }

        if (clipExpressionValues.TryGetValue(mainPath, out var dict)) dict.Remove(shapeName);
        currentExpressionValues.Remove(shapeName); registeredShapeKeys.Remove(shapeName); dirtyShapeKeys.Remove(shapeName);

        if (autoLinkShapeKeys)
        {
            foreach (var smr in availableSmrs)
            {
                if (smr == mainSmr || smr.sharedMesh != null) continue;
                if (smr.sharedMesh.GetBlendShapeIndex(shapeName) != -1)
                {
                    string path = smrPathCache.ContainsKey(smr) ? smrPathCache[smr] : GetRelativePath(smr.gameObject);
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shapeName), null);
                    
                    if (warehouse.TryGetValue(path, out var lTracks)) lTracks.RemoveAll(t => t.label == shapeName);
                    if (clipExpressionValues.TryGetValue(path, out var lDict)) lDict.Remove(shapeName);
                }
            }
        }
        if (!isMirrorCall && isMirroringEnabled && mirrorShapeMap.TryGetValue(shapeName, out string partner)) if (registeredShapeKeys.Contains(partner)) RemoveShapeKeyValue(clip, partner, true);
        Undo.CollapseUndoOperations(group);
        RefreshExpressionCache();
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
        if (GUILayout.Button(jumpUpIconContent, optW20, optH18)) rightScrollPos.y = 0f;
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
        // ★ホバー時のツールチップ説明を追加
        if (GUILayout.Button(new GUIContent("参照先の全項目を「0」で追加", "参照用アニメの全項目を、0Fの表情に対して「0」の値で追加します。"))) ProcessFromReferenceClip();
        EditorGUILayout.Space();
        GUI.backgroundColor = new Color(0.8f, 1f, 0.8f);

        // ★ホバー時のツールチップ説明を追加
        if (GUILayout.Button(new GUIContent("全ての未登録キーを「0」で埋める", "このメッシュのすべての未登録シェイプキーを、0Fの表情に対して「0」の値で埋めます。"))) ProcessShapeKeys();

        GUI.backgroundColor = Color.white;
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

        // ★追加：アバター切替やリロード時のみ、絞り込み状態を初期化する
        if (timelineFilteredShapes != null) timelineFilteredShapes.Clear();

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

        if (VRC_ExpressionPreview.Instance != null) { VRC_ExpressionPreview.Instance.FindAndCacheSceneLight(); VRC_ExpressionPreview.Instance.ForceRebuildDummy(); }
        isClampBlendShapesEnabledCache = IsClampBlendShapesEnabled();
    }

    public void RefreshExpressionCache()
    {
        // 1. まずは「棚」と「既存の掲示板」を綺麗に空にする
        warehouse.Clear();
        currentExpressionValues.Clear();
        clipExpressionValues.Clear();
        registeredShapeKeys.Clear();
        dirtyShapeKeys.Clear();
        activeObjectValues.Clear();

        // 2. アニメーションが選択されていない場合は何もしない
        if (availableClips.Count <= selectedClipIndex) return;

        AnimationClip clip = availableClips[selectedClipIndex];

        // 3. 本物のアニメーションファイル（.anim）の中身をスキャンして「全住所」を取得
        var bindings = AnimationUtility.GetCurveBindings(clip);

        // 4. 今エディタで選んでいるメッシュの「住所（パス）」を取得しておく
        string currentSmrPath = (availableSmrs.Count > selectedSmrIndex)
            ? GetRelativePath(availableSmrs[selectedSmrIndex].gameObject) : "";

        foreach (var b in bindings)
        {
            // 5. 棚にこの住所（パス）のコーナーがなければ作る
            if (!warehouse.ContainsKey(b.path)) warehouse[b.path] = new List<ExpressionTrack>();

            // 6. パッケージを作成し、ファイルから取り出した「倉庫（カーブ）」や住所を詰め込む
            var curve = AnimationUtility.GetEditorCurve(clip, b);
            var track = new ExpressionTrack
            {
                path = b.path,
                type = b.type,
                propertyName = b.propertyName,
                label = b.propertyName.Replace("blendShape.", "").Replace("m_IsActive", "有効/無効"),
                curve = curve,
                currentValue = curve.Evaluate(currentTime)
            };

            // 7. 棚（warehouse）にパッケージを並べる
            warehouse[b.path].Add(track);

            // 8. 【互換性のための処理】以前のコードが使っていた掲示板（辞書）もついでに更新しておく
            if (!clipExpressionValues.ContainsKey(b.path)) clipExpressionValues[b.path] = new Dictionary<string, float>();
            clipExpressionValues[b.path][track.label] = track.currentValue;

            // 9. もし今のパッケージが、今いじっているメッシュのものなら、スライダー用データとして登録
            if (b.path == currentSmrPath && b.type == typeof(SkinnedMeshRenderer))
            {
                currentExpressionValues[track.label] = track.currentValue;
                registeredShapeKeys.Add(track.label);
            }
            else if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive")
            {
                activeObjectValues[b.path] = track.currentValue > 0.5f;
            }
        }
        // 10. スライダーの並び順を整理する（元の関数の末尾にあった処理）
        ApplySorting();
        // ★追加：もしタイムラインが開いていたら、目盛りの点（ドット）を即座に再計算させる
        if (VRC_ExpressionTimeline.Instance != null)
        {
            VRC_ExpressionTimeline.Instance.UpdateKeyframeCache(this);
        }
    }

    private void RecalculateObjNameWidth()
    {
        // ★追加：エラー回避
        if (EditorStyles.label == null) return;
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

        clipLocationCache.Clear();

        int layerIdx = 0;
        foreach (var ctrl in cachedControllers)
        {
            for (int i = 0; i < ctrl.layers.Length; i++)
            {
                if (layerIdx < selectedLayers.Length && selectedLayers[layerIdx])
                {
                    var layer = ctrl.layers[i];
                    ExtractClipsFromStateMachine(layer.stateMachine, clipSet, layer.name);
                }
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
        // ★追加：Unityのデザイン準備ができていない場合は、一旦処理を飛ばす（エラー回避）
        if (EditorStyles.label == null) return;
        if (availableSmrs == null || availableSmrs.Count <= selectedSmrIndex || availableClips.Count <= selectedClipIndex) return;
        SkinnedMeshRenderer smr = availableSmrs[selectedSmrIndex];
        var list = new List<string>(); for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++) list.Add(smr.sharedMesh.GetBlendShapeName(i));

        // 今選んでいるメッシュの住所（パス）
        string currentPath = GetRelativePath(smr.gameObject);

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
                // 1. 最優先：お気に入り（★）に入っているものを上に集める
                return favoriteShapes.Contains(name) ? 0 : 1;
            }).ThenBy(name => {
                if (currentSortMode == SortMode.Default) return list.IndexOf(name);

                if (currentSortMode == SortMode.ActiveFirst)
                {
                    // 登録状態と、現在の数値の取得
                    bool isRegistered = registeredShapeKeys.Contains(name);
                    currentExpressionValues.TryGetValue(name, out float val);
                    bool isZero = Mathf.Approximately(val, 0f);

                    // 今のフレーム（currentTime）にキーがあるか調べる
                    bool hasKeyAtCurrentTime = false;
                    if (isRegistered && warehouse.TryGetValue(currentPath, out var tracks))
                    {
                        var track = tracks.Find(t => t.label == name);
                        if (track != null)
                        {
                            // 倉庫のカーブを調べて、現在の時間にキーがあるか確認
                            hasKeyAtCurrentTime = track.curve.keys.Any(k => Mathf.Approximately(k.time, currentTime));
                        }
                    }

                    // 2. ★新規実装：5段階の優先度（スコア）を決定して並び替える
                    if (!isRegistered) return 4; // グレー（未登録）

                    if (hasKeyAtCurrentTime)
                    {
                        return isZero ? 1 : 0; // 0以外＝緑（0番手）、0＝水色（1番手）
                    }
                    else
                    {
                        return isZero ? 3 : 2; // 0以外＝オレンジ（2番手）、0＝白（3番手）
                    }
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

    private void ExtractClipsFromStateMachine(AnimatorStateMachine sm, HashSet<AnimationClip> clipSet, string layerName)
    {
        if (sm == null) return;
        foreach (var state in sm.states)
        {
            ExtractClipsFromMotion(state.state.motion, clipSet, layerName, state.state.name);
        }
        foreach (var subSm in sm.stateMachines)
        {
            ExtractClipsFromStateMachine(subSm.stateMachine, clipSet, layerName);
        }
    }

    private void ExtractClipsFromMotion(Motion motion, HashSet<AnimationClip> clipSet, string layerName, string stateName)
    {
        if (motion == null) return;
        if (motion is AnimationClip clip)
        {
            clipSet.Add(clip);
            RegisterClipLocation(clip, layerName, stateName);
        }
        else if (motion is BlendTree tree)
        {
            foreach (var child in tree.children)
            {
                ExtractClipsFromMotion(child.motion, clipSet, layerName, stateName);
            }
        }
    }

    private void RegisterClipLocation(AnimationClip clip, string layerName, string stateName)
    {
        if (!clipLocationCache.ContainsKey(clip))
        {
            clipLocationCache[clip] = new List<string>();
        }
        string locationInfo = $"{layerName} / {stateName}";
        if (!clipLocationCache[clip].Contains(locationInfo))
        {
            clipLocationCache[clip].Add(locationInfo);
        }
    }

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

    // ★カッコの末尾に customTime = -1f を追加
    private void RegisterShapeKeyConstant(AnimationClip clip, EditorCurveBinding binding, float value, float customTime = -1f)
    {
        Undo.RecordObject(clip, "シェイプキー変更");
        string shapeName = binding.propertyName.Replace("blendShape.", "");
        string path = binding.path;

        // ★指定時間（customTime）が0以上ならそれを使い、そうでなければエディタの現在の時間（currentTime）を使う
        float time = (customTime >= 0f) ? customTime : currentTime;

        // 1. 【倉庫の更新】自前の棚（warehouse）から既存のカーブを取り出す
        AnimationCurve curve = null;
        if (!warehouse.ContainsKey(path)) warehouse[path] = new List<ExpressionTrack>(); // 住所がない場合はコーナーを作る

        if (warehouse.TryGetValue(path, out var tracks))
        {
            var track = tracks.Find(t => t.label == shapeName);
            if (track != null)
            {
                curve = track.curve;
                track.currentValue = value; // 掲示板（currentValue）も最新にしておく
            }
            else
            {
                // ★【ここを追加！】新規登録（グレーからの昇格）なので、新しくパッケージを作って棚に並べる
                curve = new AnimationCurve();
                var newTrack = new ExpressionTrack
                {
                    label = shapeName,
                    path = path,
                    type = binding.type,
                    propertyName = binding.propertyName,
                    curve = curve,
                    currentValue = value
                };
                tracks.Add(newTrack); // 棚に追加！
            }
        }

        // 2. 【未来対応】0f だった部分を time（変数）に変更
        bool found = false;
        for (int i = 0; i < curve.keys.Length; i++)
        {
            if (Mathf.Approximately(curve.keys[i].time, time)) // time（変数）にする
            {
                curve.MoveKey(i, new Keyframe(time, value)); // time（変数）にする
                found = true;
                break;
            }
        }
        if (!found) curve.AddKey(time, value); // time（変数）にする

        // 3. 【ファイルの更新】安全に更新されたカーブを保存
        AnimationUtility.SetEditorCurve(clip, binding, curve);

        // 4. 【オリジナルの機能維持】既存の掲示板（辞書）も同期しておく
        if (!clipExpressionValues.ContainsKey(path)) clipExpressionValues[path] = new Dictionary<string, float>();
        clipExpressionValues[path][shapeName] = value;

        if (availableSmrs != null && availableSmrs.Count > selectedSmrIndex && path == GetRelativePath(availableSmrs[selectedSmrIndex].gameObject))
        {
            currentExpressionValues[shapeName] = value;
            registeredShapeKeys.Add(shapeName);
        }
    }

    private void ProcessShapeKeys()
    {
        AnimationClip clip = availableClips[selectedClipIndex]; SkinnedMeshRenderer smr = availableSmrs[selectedSmrIndex];
        string path = smrPathCache.ContainsKey(smr) ? smrPathCache[smr] : GetRelativePath(smr.gameObject);

        int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("全シェイプキー0埋め"); Undo.RecordObject(clip, "シェイプ登録");
        for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
        {
            string name = smr.sharedMesh.GetBlendShapeName(i);
            if (AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + name)) == null)
                // 末尾に「, false, 0f」を追加して、強制的に0秒目（0F）に書き込ませます
                CommitShapeKeyValue(clip, name, 0f, false, 0f);
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
                if (AnimationUtility.GetEditorCurve(targetClip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + name)) == null)
                    // 同様に、末尾に「, false, 0f」を追加
                    CommitShapeKeyValue(targetClip, name, 0f, false, 0f);
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

    // fullRebuild が true の時だけ顔をすっぴんに戻し、普段（false）は画面の塗り直しだけにする
    public void ForceRepaintPreview(bool fullRebuild = true)
    {
        if (VRC_ExpressionPreview.Instance != null)
        {
            if (fullRebuild)
            {
                VRC_ExpressionPreview.Instance.MarkPreviewDirty(); // すっぴんに戻すフラグを立てる
            }
            VRC_ExpressionPreview.Instance.Repaint();
        }
    }
    // ★新規追加：指定した時間（フレーム）のキーだけを削除する
    private void RemoveKeyframeAtTime(AnimationClip clip, string path, string shapeName, float time)
    {
        var binding = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shapeName);
        AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
        if (curve == null) return;

        // 指定された時間のキーフレームを探す
        int index = -1;
        for (int i = 0; i < curve.keys.Length; i++)
        {
            if (Mathf.Approximately(curve.keys[i].time, time))
            {
                index = i;
                break;
            }
        }

        // キーが見つかったら削除する
        if (index != -1)
        {
            Undo.RecordObject(clip, "キーフレーム削除");
            curve.RemoveKey(index);

            // ★もし他にキーが1つも残っていないなら、項目ごと完全に削除する（グレーに戻る）
            if (curve.keys.Length == 0)
            {
                AnimationUtility.SetEditorCurve(clip, binding, null);
                if (warehouse.TryGetValue(path, out var tracks))
                {
                    tracks.RemoveAll(t => t.label == shapeName);
                }
            }
            else
            {
                // まだ他のフレームにキーがあるなら、キーを減らしたカーブを上書き保存する（白かオレンジに戻る）
                AnimationUtility.SetEditorCurve(clip, binding, curve);

                // メモリ上の倉庫のカーブも最新にする
                if (warehouse.TryGetValue(path, out var tracks))
                {
                    var track = tracks.Find(x => x.label == shapeName);
                    if (track != null) track.curve = curve;
                }
            }

            RefreshExpressionCache();
        }
    }
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