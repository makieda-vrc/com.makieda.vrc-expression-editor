using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class VRC_ExpressionThumbnailWindow : EditorWindow
{
    public static VRC_ExpressionThumbnailWindow Instance { get; private set; }

    private Vector2 scrollPos;
    private Dictionary<string, Sprite> thumbnailCache = new Dictionary<string, Sprite>();

    private const string THUMBNAIL_DIR = "Assets/Editor/VRC_ExpressionEditor/Thumbnails";

    private GUIStyle centerLockStyle;
    private GUIStyle labelStyle;
    private GUIStyle gridBoxStyle;

    // ★修正：OS/Unity依存の EditorPrefs を完全に撤去し、プロジェクト内の ExpressionEditorSettings アセットへ一元保存します
    private int sizeScale
    {
        get
        {
            var editor = VRC_ExpressionEditor.Instance;
            if (editor != null && editor.GetSettings() != null)
            {
                return editor.GetSettings().thumbnailSizeScale;
            }
            return 1;
        }
        set
        {
            var editor = VRC_ExpressionEditor.Instance;
            if (editor != null && editor.GetSettings() != null)
            {
                var settings = editor.GetSettings();
                if (settings.thumbnailSizeScale != value)
                {
                    settings.thumbnailSizeScale = value;
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets(); // ディスク保存を強制
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
    }

    private void OnDisable()
    {
        if (Instance == this) Instance = null;
        thumbnailCache.Clear();
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

            // 文字まわりのフォントサイズと高さを削り、余白を完全に除去
            labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                fixedHeight = 18,
                fontSize = 10,
                padding = new RectOffset(1, 1, 1, 1),
                margin = new RectOffset(0, 0, 0, 0)
            };

            gridBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(1, 1, 1, 1),
                margin = new RectOffset(1, 1, 1, 1)
            };
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

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        float windowWidth = position.width;
        float itemSize = sizeScale == 2 ? 192f : 128f; // x1(128f) と x1.5(192f) の切り替え
        float spacing = 2f;
        int columnCount = Mathf.Max(1, Mathf.FloorToInt((windowWidth - 10f) / (itemSize + spacing)));

        int clipCount = editor.availableClips.Count;
        int rowCount = Mathf.CeilToInt((float)clipCount / columnCount);

        for (int r = 0; r < rowCount; r++)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(spacing);

            for (int c = 0; c < columnCount; c++)
            {
                int index = r * columnCount + c;
                if (index >= clipCount)
                {
                    GUILayout.Space(itemSize);
                    GUILayout.Space(spacing);
                    continue;
                }

                AnimationClip clip = editor.availableClips[index];
                if (clip == null) continue;

                DrawThumbnailItem(editor, clip, index, itemSize);
                GUILayout.Space(spacing);
            }

            EditorGUILayout.EndHorizontal();
            GUILayout.Space(spacing);
        }

        EditorGUILayout.EndScrollView();

        DrawScaleToggle();
    }

    private void DrawScaleToggle()
    {
        float btnWidth = 60f;
        float btnHeight = 16f;

        Rect toggleRect = new Rect(position.width - btnWidth - 6f, position.height - btnHeight - 6f, btnWidth, btnHeight);

        EditorGUI.DrawRect(new Rect(toggleRect.x - 2f, toggleRect.y - 2f, toggleRect.width + 4f, toggleRect.height + 4f), new Color(0.15f, 0.15f, 0.15f, 0.85f));

        int currentScale = sizeScale;
        int nextScale = currentScale;

        Rect btnX1 = new Rect(toggleRect.x, toggleRect.y, btnWidth / 2f, btnHeight);
        Rect btnX2 = new Rect(toggleRect.x + btnWidth / 2f, toggleRect.y, btnWidth / 2f, btnHeight);

        GUIStyle miniBtnStyle = new GUIStyle(EditorStyles.miniButton)
        {
            fontSize = 8,
            padding = new RectOffset(0, 0, 0, 0)
        };

        Color oldColor = GUI.backgroundColor;

        if (currentScale == 1) GUI.backgroundColor = new Color(0.4f, 0.8f, 1f, 1f);
        if (GUI.Button(btnX1, "x1", miniBtnStyle)) nextScale = 1;
        GUI.backgroundColor = oldColor;

        if (currentScale == 2) GUI.backgroundColor = new Color(0.4f, 0.8f, 1f, 1f);
        if (GUI.Button(btnX2, "x1.5", miniBtnStyle)) nextScale = 2;
        GUI.backgroundColor = oldColor;

        if (nextScale != currentScale)
        {
            sizeScale = nextScale;
            GUI.FocusControl(null);
            Repaint();
        }
    }

    private void DrawThumbnailItem(VRC_ExpressionEditor editor, AnimationClip clip, int index, float size)
    {
        // アバター名（rootObject.name）の代わりに、FXコントローラー（アセット名）をフォルダ名に決定
        string fxGuid = editor.GetAvatarID();
        if (string.IsNullOrEmpty(fxGuid)) fxGuid = "DefaultFX";

        // ファイル名として使用できないOSの禁止文字を安全にアンダースコア（_）に置換します
        string safeFileName = clip.name;
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
        {
            safeFileName = safeFileName.Replace(c, '_');
        }

        string savePath = $"{THUMBNAIL_DIR}/{fxGuid}/{safeFileName}.png";

        // 生成されるアセットパス（savePath）自体をキャッシュのキーに設定
        if (!thumbnailCache.TryGetValue(savePath, out Sprite thumbSprite) || thumbSprite == null)
        {
            thumbSprite = AssetDatabase.LoadAssetAtPath<Sprite>(savePath);
            if (thumbSprite != null)
            {
                thumbnailCache[savePath] = thumbSprite;
            }
        }

        bool isSelected = (editor.selectedClipIndex == index);

        Color originalBgColor = GUI.backgroundColor;
        if (isSelected) GUI.backgroundColor = new Color(0.4f, 0.8f, 1f, 1f);

        EditorGUILayout.BeginVertical(gridBoxStyle, GUILayout.Width(size), GUILayout.Height(size + 20f));
        GUI.backgroundColor = originalBgColor;

        Rect rect = GUILayoutUtility.GetRect(size - 4f, size - 4f);

        if (thumbSprite != null)
        {
            GUI.DrawTexture(rect, thumbSprite.texture, ScaleMode.ScaleToFit);
        }
        else
        {
            EditorGUI.DrawRect(rect, new Color(0.25f, 0.25f, 0.25f, 1f));
        }

        GUILayout.Label(clip.name, labelStyle, GUILayout.Width(size - 4f));

        if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
        {
            if (editor.selectedClipIndex != index || thumbSprite == null)
            {
                if (VRC_ExpressionPreview.Instance != null && editor.selectedClipIndex != index)
                {
                    VRC_ExpressionPreview.Instance.ResetPreviousAnimBlendShapes(editor.clipExpressionValues);
                }

                editor.selectedClipIndex = index;
                editor.UpdateCacheArrays();
                editor.RefreshExpressionCache();
                editor.ApplySorting();
                editor.ForceRepaintPreview();

                if (thumbSprite == null && VRC_ExpressionPreview.Instance != null)
                {
                    VRC_ExpressionPreview.Instance.RequestCapture(savePath);
                }
            }
            Event.current.Use();
        }

        if (Event.current.type == EventType.ContextClick && rect.Contains(Event.current.mousePosition))
        {
            GenericMenu menu = new GenericMenu();
            menu.AddItem(new GUIContent("この表情を再撮影"), false, () =>
            {
                if (VRC_ExpressionPreview.Instance != null)
                {
                    VRC_ExpressionPreview.Instance.RequestCapture(savePath);
                }
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