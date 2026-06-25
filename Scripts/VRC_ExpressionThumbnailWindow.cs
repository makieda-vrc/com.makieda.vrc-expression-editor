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
    private GUIStyle emptyBoxStyle; // 枠なし密着用スタイル

    private const float MIN_SPACING = 0f;
    private const float LABEL_HEIGHT = 20f; // ラベルの高さ固定値

    private float lastWindowWidth;
    private float lastSizeScale;
    private int cachedColumnCount = 1;
    private float cachedSpacing = 0f;

    private int lastHotControl = 0;
    private bool isScaleDirty = false;

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
                    isScaleDirty = true;
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
        isScaleDirty = false;
        lastFxGuid = null;
    }

    private void OnDisable()
    {
        if (Instance == this) Instance = null;
        thumbnailCache.Clear();
        clipPathCache.Clear();

        if (isScaleDirty)
        {
            AssetDatabase.SaveAssets();
            isScaleDirty = false;
        }
    }

    private void ResetLayoutCache()
    {
        lastWindowWidth = -1f;
        lastSizeScale = -1f;
    }

    private void InitializeStyles()
    {
        if (centerLockStyle == null)
        {
            centerLockStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                wordWrap = true
            };

            // ラベル設定：文字サイズ15 / 高さ15 / 余白ゼロ
            labelStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip,
                fixedHeight = LABEL_HEIGHT,
                fontSize = 15,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };
            labelStyle.normal.textColor = Color.white;

            // 密着用コンテナ設定
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

        if (editor.availableClips == null || editor.availableClips.Count == 0)
        {
            EditorGUILayout.HelpBox("表示可能なアニメーションがありません。", MessageType.Info);
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

        // 右端の隙間を消すため、スクロールバー分(15px)だけを引いた幅を使用
        float windowWidth = currentWidth - 15f;
        if (windowWidth < 50f) windowWidth = 50f;

        float itemSize = 128f * currentScale;

        if (!Mathf.Approximately(currentWidth, lastWindowWidth) || !Mathf.Approximately(currentScale, lastSizeScale))
        {
            UpdateLayoutCalculations(windowWidth, itemSize);
            lastWindowWidth = currentWidth;
            lastSizeScale = currentScale;
        }

        int clipCount = editor.availableClips.Count;
        int rowCount = Mathf.CeilToInt((float)clipCount / cachedColumnCount);

        // 1行の高さ = アイテム横幅(正方形) + ラベル高さ(15px)
        float rowHeight = itemSize + LABEL_HEIGHT;
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

            // BeginHorizontalにGUIStyle.noneを指定して隙間をゼロにする
            EditorGUILayout.BeginHorizontal(GUIStyle.none, GUILayout.Height(rowHeight));

            for (int c = 0; c < cachedColumnCount; c++)
            {
                int index = r * cachedColumnCount + c;
                if (index >= clipCount)
                {
                    GUILayout.FlexibleSpace();
                    break;
                }

                AnimationClip clip = editor.availableClips[index];
                if (clip == null) continue;

                DrawThumbnailItem(editor, clip, index, itemSize, currentFxGuid);

                if (c < cachedColumnCount - 1 && index < clipCount - 1)
                {
                    GUILayout.Space(cachedSpacing);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();

        DrawScaleToggle();

        // 保存検知ロジック
        bool isOperating = (GUIUtility.hotControl != 0 || EditorGUIUtility.editingTextField);
        if (!isOperating && isScaleDirty)
        {
            AssetDatabase.SaveAssets();
            isScaleDirty = false;
        }
        lastHotControl = GUIUtility.hotControl;
    }

    private void DrawScaleToggle()
    {
        float areaWidth = 200f;
        float areaHeight = 18f;
        Rect toggleRect = new Rect(position.width - areaWidth - 6f, position.height - areaHeight - 6f, areaWidth, areaHeight);

        EditorGUI.DrawRect(new Rect(toggleRect.x - 2f, toggleRect.y - 2f, toggleRect.width + 4f, toggleRect.height + 4f), new Color(0.15f, 0.15f, 0.15f, 0.85f));

        float currentScale = sizeScaleFloat;
        float nextScale = currentScale;

        Rect btnRect = new Rect(toggleRect.x, toggleRect.y, 32f, areaHeight);
        Rect sliderRect = new Rect(btnRect.xMax + 4f, toggleRect.y, 114f, areaHeight);
        Rect fieldRect = new Rect(sliderRect.xMax + 4f, toggleRect.y, 46f, areaHeight);

        if (GUI.Button(btnRect, "x1", EditorStyles.miniButton))
        {
            nextScale = 1.0f;
            GUIUtility.keyboardControl = 0;
        }

        nextScale = GUI.HorizontalSlider(sliderRect, nextScale, 0.5f, 1.5f);

        EditorGUI.BeginChangeCheck();
        float inputScale = EditorGUI.DelayedFloatField(fieldRect, nextScale);
        if (EditorGUI.EndChangeCheck())
        {
            nextScale = Mathf.Clamp(inputScale, 0.5f, 1.5f);
        }

        if (!Mathf.Approximately(nextScale, currentScale))
        {
            sizeScaleFloat = nextScale;
            Repaint();
        }
    }

    private void DrawThumbnailItem(VRC_ExpressionEditor editor, AnimationClip clip, int index, float size, string fxGuid)
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

        bool isSelected = (editor.selectedClipIndex == index);

        // コンテナ開始（完全に余白ゼロ）
        Rect totalRect = EditorGUILayout.BeginVertical(emptyBoxStyle, GUILayout.Width(size), GUILayout.Height(size + LABEL_HEIGHT));

        // 選択ハイライト描画
        if (isSelected)
        {
            EditorGUI.DrawRect(totalRect, new Color(0.4f, 0.8f, 1f, 0.4f));
        }

        // 画像描画エリア
        Rect imgRect = GUILayoutUtility.GetRect(size, size);
        if (thumbSprite != null)
        {
            GUI.DrawTexture(imgRect, thumbSprite.texture, ScaleMode.ScaleToFit);
        }
        else
        {
            EditorGUI.DrawRect(imgRect, new Color(0.25f, 0.25f, 0.25f, 1f));
        }

        // ラベル描画（固定高さ15px）
        GUILayout.Label(clip.name, labelStyle, GUILayout.Width(size), GUILayout.Height(LABEL_HEIGHT));

        // クリックイベント
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

                if (thumbSprite == null && VRC_ExpressionPreview.Instance != null)
                    VRC_ExpressionPreview.Instance.RequestCapture(savePath);
            }
            Event.current.Use();
        }

        // コンテキストメニュー
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