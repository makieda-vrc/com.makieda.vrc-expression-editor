using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class VRC_ExpressionThumbnailWindow : EditorWindow
{
    public static VRC_ExpressionThumbnailWindow Instance { get; private set; }

    private Vector2 scrollPos;
    private Dictionary<string, Sprite> thumbnailCache = new Dictionary<string, Sprite>();
    private Dictionary<AnimationClip, string> clipPathCache = new Dictionary<AnimationClip, string>();
    private string lastFxGuid = null;

    private const string THUMBNAIL_DIR = "Assets/Editor/VRC_ExpressionEditor/Thumbnails";

    private GUIStyle centerLockStyle;
    private GUIStyle labelStyle;
    private GUIStyle detailLabelStyle;
    private GUIStyle emptyBoxStyle;

    private const float MIN_SPACING = 0f;
    private const float TITLE_HEIGHT = 18f;
    private const float DETAIL_HEIGHT = 12f;

    private float lastWindowWidth;
    private float lastSizeScale;
    private bool lastShowDetail;
    private int cachedColumnCount = 1;
    private float cachedSpacing = 0f;

    private int lastHotControl = 0;
    private bool isSettingDirty = false;

    private float sizeScaleFloat
    {
        get
        {
            var editor = VRC_ExpressionEditor.Instance;
            if (editor != null && editor.GetSettings() != null)
            {
                int saved = editor.GetSettings().thumbnailSizeScale;
                if (saved == 1) return 1.0f;
                if (saved == 2) return 1.5f;
                if (saved >= 50 && saved <= 150) return saved / 100f;
            }
            return 1.0f;
        }
        set
        {
            var editor = VRC_ExpressionEditor.Instance;
            if (editor != null && editor.GetSettings() != null)
            {
                var settings = editor.GetSettings();
                int intValue = Mathf.RoundToInt(value * 100f);
                if (settings.thumbnailSizeScale != intValue)
                {
                    settings.thumbnailSizeScale = intValue;
                    EditorUtility.SetDirty(settings);
                    isSettingDirty = true;
                }
            }
        }
    }

    private bool showDetailInfo
    {
        get
        {
            var editor = VRC_ExpressionEditor.Instance;
            if (editor != null && editor.GetSettings() != null)
                return editor.GetSettings().showDetailInfo;
            return false;
        }
        set
        {
            var editor = VRC_ExpressionEditor.Instance;
            if (editor != null && editor.GetSettings() != null)
            {
                var settings = editor.GetSettings();
                if (settings.showDetailInfo != value)
                {
                    settings.showDetailInfo = value;
                    EditorUtility.SetDirty(settings);
                    isSettingDirty = true;
                }
            }
        }
    }

    public static void OpenWindow()
    {
        Instance = GetWindow<VRC_ExpressionThumbnailWindow>("表情一覧");
        Instance.minSize = new Vector2(250, 300);
        Instance.Show();
    }

    private void OnEnable()
    {
        Instance = this;
        thumbnailCache.Clear();
        clipPathCache.Clear();
        ResetLayoutCache();
        isSettingDirty = false;
        lastFxGuid = null;
    }

    private void OnDisable()
    {
        if (Instance == this) Instance = null;
        thumbnailCache.Clear();
        clipPathCache.Clear();

        if (isSettingDirty)
        {
            AssetDatabase.SaveAssets();
            isSettingDirty = false;
        }
    }

    private void ResetLayoutCache()
    {
        lastWindowWidth = -1f;
        lastSizeScale = -1f;
        lastShowDetail = false;
    }

    private void InitializeStyles()
    {
        if (centerLockStyle == null)
        {
            centerLockStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 14, wordWrap = true };

            labelStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip,
                fixedHeight = TITLE_HEIGHT,
                fontSize = 14,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };
            labelStyle.normal.textColor = Color.white;

            detailLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip,
                fixedHeight = DETAIL_HEIGHT,
                fontSize = 10,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };
            detailLabelStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f, 1f);

            emptyBoxStyle = new GUIStyle();
            emptyBoxStyle.margin = new RectOffset(0, 0, 0, 0);
            emptyBoxStyle.padding = new RectOffset(0, 0, 0, 0);
        }
    }

    private void UpdateLayoutCalculations(float windowWidth, float itemSize)
    {
        cachedColumnCount = Mathf.Max(1, Mathf.FloorToInt(windowWidth / itemSize));
        cachedSpacing = MIN_SPACING;

        if (cachedColumnCount > 1)
        {
            cachedSpacing = (windowWidth - (cachedColumnCount * itemSize)) / (cachedColumnCount - 1);
            if (cachedSpacing < MIN_SPACING)
            {
                cachedColumnCount = Mathf.Max(1, cachedColumnCount - 1);
                cachedSpacing = (cachedColumnCount > 1) ? (windowWidth - (cachedColumnCount * itemSize)) / (cachedColumnCount - 1) : MIN_SPACING;
            }
        }
    }

    private void OnGUI()
    {
        InitializeStyles();

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("▶ 再生モード中です\n安全のため表情一覧をロックしています", centerLockStyle);
            GUILayout.FlexibleSpace();
            return;
        }

        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null || editor.rootObject == null)
        {
            EditorGUILayout.HelpBox("メインのエディタでアバターを設定してください。", MessageType.Info);
            return;
        }

        string currentFxGuid = editor.GetAvatarID();
        if (string.IsNullOrEmpty(currentFxGuid)) currentFxGuid = "DefaultFX";

        if (lastFxGuid != currentFxGuid)
        {
            lastFxGuid = currentFxGuid;
            clipPathCache.Clear();
            thumbnailCache.Clear();
        }

        scrollPos.x = 0f;
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, false, false);

        float currentWidth = position.width;
        float currentScale = sizeScaleFloat;
        bool currentShowDetail = showDetailInfo;

        float windowWidth = currentWidth - 15f;
        if (windowWidth < 50f) windowWidth = 50f;

        float itemSize = 128f * currentScale;

        if (!Mathf.Approximately(currentWidth, lastWindowWidth) || !Mathf.Approximately(currentScale, lastSizeScale) || currentShowDetail != lastShowDetail)
        {
            UpdateLayoutCalculations(windowWidth, itemSize);
            lastWindowWidth = currentWidth;
            lastSizeScale = currentScale;
            lastShowDetail = currentShowDetail;
        }

        int clipCount = editor.availableClips.Count;
        int rowCount = Mathf.CeilToInt((float)clipCount / cachedColumnCount);

        float labelAreaHeight = TITLE_HEIGHT + (currentShowDetail ? DETAIL_HEIGHT : 0f);
        float rowHeight = itemSize + labelAreaHeight;

        float viewTop = scrollPos.y;
        float viewBottom = scrollPos.y + position.height;

        for (int r = 0; r < rowCount; r++)
        {
            float rowTop = r * rowHeight;
            float rowBottom = rowTop + rowHeight;

            if (rowBottom < viewTop - rowHeight || rowTop > viewBottom + rowHeight)
            {
                GUILayout.Space(rowHeight);
                continue;
            }

            EditorGUILayout.BeginHorizontal(GUIStyle.none, GUILayout.Height(rowHeight));

            for (int c = 0; c < cachedColumnCount; c++)
            {
                int index = r * cachedColumnCount + c;
                if (index >= clipCount) { GUILayout.FlexibleSpace(); break; }

                AnimationClip clip = editor.availableClips[index];
                if (clip == null) continue;

                DrawThumbnailItem(editor, clip, index, itemSize, currentFxGuid, currentShowDetail, labelAreaHeight);

                if (c < cachedColumnCount - 1 && index < clipCount - 1) GUILayout.Space(cachedSpacing);
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
        DrawScaleToggle();

        bool isOperating = (GUIUtility.hotControl != 0 || EditorGUIUtility.editingTextField);
        if (!isOperating && isSettingDirty)
        {
            AssetDatabase.SaveAssets();
            isSettingDirty = false;
        }
        lastHotControl = GUIUtility.hotControl;
    }

    private void DrawScaleToggle()
    {
        float areaWidth = 230f;
        float areaHeight = 18f;
        Rect toggleRect = new Rect(position.width - areaWidth - 6f, position.height - areaHeight - 6f, areaWidth, areaHeight);

        EditorGUI.DrawRect(new Rect(toggleRect.x - 2f, toggleRect.y - 2f, toggleRect.width + 4f, toggleRect.height + 4f), new Color(0.15f, 0.15f, 0.15f, 0.85f));

        float currentScale = sizeScaleFloat;
        float nextScale = currentScale;
        bool nextShowDetail = showDetailInfo;

        Rect detailBtnRect = new Rect(toggleRect.x, toggleRect.y, 24f, areaHeight);
        Rect btnRect = new Rect(detailBtnRect.xMax + 4f, toggleRect.y, 32f, areaHeight);
        Rect sliderRect = new Rect(btnRect.xMax + 4f, toggleRect.y, 114f, areaHeight);
        Rect fieldRect = new Rect(sliderRect.xMax + 4f, toggleRect.y, 46f, areaHeight);

        Color oldBg = GUI.backgroundColor;
        if (nextShowDetail) GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
        if (GUI.Button(detailBtnRect, new GUIContent("S", "詳細情報（レイヤー/ステート名）を表示切替"), EditorStyles.miniButton))
        {
            nextShowDetail = !nextShowDetail;
        }
        GUI.backgroundColor = oldBg;

        if (GUI.Button(btnRect, "x1", EditorStyles.miniButton))
        {
            nextScale = 1.0f;
            GUIUtility.keyboardControl = 0;
        }

        nextScale = GUI.HorizontalSlider(sliderRect, nextScale, 0.5f, 1.5f);

        EditorGUI.BeginChangeCheck();
        float inputScale = EditorGUI.DelayedFloatField(fieldRect, nextScale);
        if (EditorGUI.EndChangeCheck()) nextScale = Mathf.Clamp(inputScale, 0.5f, 1.5f);

        if (!Mathf.Approximately(nextScale, currentScale)) { sizeScaleFloat = nextScale; Repaint(); }
        if (nextShowDetail != showDetailInfo) { showDetailInfo = nextShowDetail; Repaint(); }
    }

    private void DrawThumbnailItem(VRC_ExpressionEditor editor, AnimationClip clip, int index, float size, string fxGuid, bool showDetail, float labelAreaHeight)
    {
        if (!clipPathCache.TryGetValue(clip, out string savePath))
        {
            string safeFileName = clip.name;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safeFileName = safeFileName.Replace(c, '_');
            savePath = $"{THUMBNAIL_DIR}/{fxGuid}/{safeFileName}.png";
            clipPathCache[clip] = savePath;
        }

        if (!thumbnailCache.TryGetValue(savePath, out Sprite thumbSprite) || thumbSprite == null)
        {
            thumbSprite = AssetDatabase.LoadAssetAtPath<Sprite>(savePath);
            if (thumbSprite != null) thumbnailCache[savePath] = thumbSprite;
        }

        string tooltip = "";
        string detailLine = "";
        if (editor.clipLocationCache.TryGetValue(clip, out List<string> locations))
        {
            tooltip = string.Join("\n", locations);
            if (locations.Count > 0)
            {
                string suffix = (locations.Count > 1) ? "..." : "";
                detailLine = "[" + locations[0] + suffix + "]";
            }
        }

        bool isSelected = (editor.selectedClipIndex == index);
        Rect totalRect = EditorGUILayout.BeginVertical(emptyBoxStyle, GUILayout.Width(size), GUILayout.Height(size + labelAreaHeight));

        GUI.contentColor = Color.white;
        GUIContent content = new GUIContent("", tooltip);
        GUI.Label(totalRect, content);

        if (isSelected) EditorGUI.DrawRect(totalRect, new Color(0.4f, 0.8f, 1f, 0.4f));

        Rect imgRect = GUILayoutUtility.GetRect(size, size);
        if (thumbSprite != null) GUI.DrawTexture(imgRect, thumbSprite.texture, ScaleMode.ScaleToFit);
        else EditorGUI.DrawRect(imgRect, new Color(0.25f, 0.25f, 0.25f, 1f));

        GUILayout.Label(clip.name, labelStyle, GUILayout.Width(size), GUILayout.Height(TITLE_HEIGHT));

        if (showDetail)
        {
            GUILayout.Label(detailLine, detailLabelStyle, GUILayout.Width(size), GUILayout.Height(DETAIL_HEIGHT));
        }

        if (Event.current.type == EventType.MouseDown && totalRect.Contains(Event.current.mousePosition))
        {
            if (editor.selectedClipIndex != index || thumbSprite == null)
            {
                if (VRC_ExpressionPreview.Instance != null && editor.selectedClipIndex != index)
                    VRC_ExpressionPreview.Instance.ResetPreviousAnimBlendShapes(editor.clipExpressionValues);

                editor.selectedClipIndex = index;
                editor.UpdateCacheArrays();
                editor.RefreshExpressionCache();
                editor.ApplySorting();
                editor.ForceRepaintPreview();

                // 【重要】メインエディタに再描画を命じる
                editor.Repaint();

                if (thumbSprite == null && VRC_ExpressionPreview.Instance != null)
                    VRC_ExpressionPreview.Instance.RequestCapture(savePath);
            }
            Event.current.Use();
        }

        if (Event.current.type == EventType.ContextClick && totalRect.Contains(Event.current.mousePosition))
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("この表情を再撮影"), false, () => {
                if (VRC_ExpressionPreview.Instance != null) VRC_ExpressionPreview.Instance.RequestCapture(savePath);
            });
            menu.ShowAsContext();
            Event.current.Use();
        }

        EditorGUILayout.EndVertical();
    }

    public void OnCaptureComplete()
    {
        thumbnailCache.Clear();
        Repaint();
    }
}