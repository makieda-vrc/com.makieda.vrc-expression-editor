using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

public class VRC_ExpressionTimeline : EditorWindow
{
    public static VRC_ExpressionTimeline Instance { get; private set; }
    private bool isPlaying = false;
    private float playbackSpeed = 1.0f;
    private double lastUpdateTime;
    private double lastCheckTime;
    private bool isLoop = true;
    private AnimationClip lastClip;
    private SkinnedMeshRenderer lastMesh;
    private bool[] lastLayers;
    private ExpressionTrack lastTrackReference;
    private List<float> cachedKeyframeTimes = new List<float>();
    private GUIStyle tooltipStyle;
    private GUIStyle rulerLabelStyle;
    private float zoomLevel = 1.0f;
    private Vector2 scrollPos;

    [MenuItem("Tools/VRC Expression Editor/Animation Timeline")]
    public static void OpenWindow()
    {
        Instance = GetWindow<VRC_ExpressionTimeline>("表情タイムライン");
        Instance.minSize = new Vector2(400, 75);
        Instance.maxSize = new Vector2(4000, 4000);
        Instance.Show();
    }

    private void OnEnable() { Instance = this; EditorApplication.update += OnUpdate; }
    private void OnDisable() { StopPlayback(); EditorApplication.update -= OnUpdate; }

    public void StopPlayback()
    {
        if (isPlaying)
        {
            var editor = VRC_ExpressionEditor.Instance;
            if (editor != null) editor.currentTime = Mathf.Round(editor.currentTime * 60f) / 60f;
            isPlaying = false;
            SyncBillboardToEditor();
            Repaint();
        }
    }

    public bool IsPlaying() => isPlaying;

    private void OnUpdate()
    {
        double currentTimeSinceStartup = EditorApplication.timeSinceStartup;
        if (!isPlaying)
        {
            if (currentTimeSinceStartup - lastCheckTime < 0.033) return;
            lastCheckTime = currentTimeSinceStartup;
        }

        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) return;

        AnimationClip currentClip = (editor.availableClips.Count > editor.selectedClipIndex) ? editor.availableClips[editor.selectedClipIndex] : null;
        if (currentClip != lastClip)
        {
            lastClip = currentClip;
            UpdateKeyframeCache(editor);
            if (!isPlaying)
            {
                editor.currentTime = 0f;
                foreach (var g in editor.warehouse.Values)
                    foreach (var t in g) t.currentValue = t.curve.Evaluate(0f);
                SyncBillboardToEditor();
            }
            Repaint();
        }

        if (!isPlaying) return;

        var currentFirstTrack = editor.warehouse.Values.FirstOrDefault()?.FirstOrDefault();
        if (currentFirstTrack != lastTrackReference) { StopPlayback(); return; }

        SkinnedMeshRenderer currentMesh = (editor.availableSmrs.Count > editor.selectedSmrIndex) ? editor.availableSmrs[editor.selectedSmrIndex] : null;
        if (currentMesh != lastMesh) { StopPlayback(); lastMesh = currentMesh; return; }

        if (editor.selectedLayers != null && lastLayers != null && !editor.selectedLayers.SequenceEqual(lastLayers)) { StopPlayback(); return; }

        double deltaTime = currentTimeSinceStartup - lastUpdateTime;
        lastUpdateTime = currentTimeSinceStartup;
        editor.currentTime += (float)deltaTime * playbackSpeed;

        float clipLength = (currentClip != null) ? currentClip.length : 1.0f;
        if (editor.currentTime > clipLength)
        {
            if (isLoop) editor.currentTime = 0f;
            else { editor.currentTime = Mathf.Round(clipLength * 60f) / 60f; StopPlayback(); return; }
        }

        foreach (var pathGroup in editor.warehouse.Values)
            foreach (var track in pathGroup) track.currentValue = track.curve.Evaluate(editor.currentTime);

        editor.ForceRepaintPreview(true);
        Repaint();
    }

    private void UpdateKeyframeCache(VRC_ExpressionEditor editor)
    {
        cachedKeyframeTimes.Clear();
        foreach (var list in editor.warehouse.Values)
            foreach (var t in list)
                foreach (var k in t.curve.keys) cachedKeyframeTimes.Add(k.time);
        cachedKeyframeTimes = cachedKeyframeTimes.Distinct().ToList();
    }

    private void SyncBillboardToEditor()
    {
        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) return;
        string currentSmrPath = (editor.availableSmrs.Count > editor.selectedSmrIndex) ? editor.GetRelativePath(editor.availableSmrs[editor.selectedSmrIndex].gameObject) : "";
        foreach (var pathGroup in editor.warehouse)
        {
            foreach (var track in pathGroup.Value)
            {
                if (editor.clipExpressionValues.ContainsKey(pathGroup.Key))
                    editor.clipExpressionValues[pathGroup.Key][track.label] = track.currentValue;
                if (pathGroup.Key == currentSmrPath)
                    editor.currentExpressionValues[track.label] = track.currentValue;
            }
        }
        editor.Repaint();
    }

    private void OnGUI()
    {
        if (EditorStyles.label == null) return;
        InitializeStyles();
        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) { EditorGUILayout.HelpBox("エディタを先に開いてください", MessageType.Info); return; }
        AnimationClip clip = (editor.availableClips.Count > editor.selectedClipIndex) ? editor.availableClips[editor.selectedClipIndex] : null;

        DrawTopToolbar(editor, clip);
        HandleZoomAndScroll();

        float maxLen = clip != null ? clip.length : 0.001f;
        int totalFrames = clip != null ? Mathf.RoundToInt(clip.length * 60f) : 0;

        float toolbarHeight = 22f;
        float scrollbarHeight = 16f;
        float availableHeight = position.height - toolbarHeight - scrollbarHeight;

        float viewWidth = position.width - 24;
        float timelineWidth = viewWidth * zoomLevel;
        float startX = 12;

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, false, false, GUILayout.ExpandHeight(true));

        Rect areaRect = GUILayoutUtility.GetRect(timelineWidth, Mathf.Max(availableHeight, 40));

        // ★修正：垂直方向の中心から 5px 下に重心をずらす
        float centerY = areaRect.y + (areaRect.height / 2f) + 8f;

        if (Event.current.type == EventType.Repaint)
        {
            DrawRulerAndGrid(areaRect, startX, timelineWidth, totalFrames, maxLen);

            // キーフレーム（白い線）：新しく計算した centerY を基準に配置
            GUI.color = Color.white;
            foreach (float t in cachedKeyframeTimes)
            {
                float x = startX + (t / maxLen) * timelineWidth;
                GUI.DrawTexture(new Rect(x - 1, centerY - 10, 2, 20), EditorGUIUtility.whiteTexture);
            }

            // 再生ヘッド（赤い線）
            float playheadX = startX + (editor.currentTime / maxLen) * timelineWidth;
            GUI.color = new Color(1f, 0.3f, 0.3f, 1f);
            GUI.DrawTexture(new Rect(playheadX - 1, areaRect.y, 2, areaRect.height), EditorGUIUtility.whiteTexture);
            GUI.color = Color.white;
        }

        // ホバーツールチップ：centerY を基準に配置
        Vector2 mPos = Event.current.mousePosition;
        if (areaRect.Contains(mPos))
        {
            foreach (float t in cachedKeyframeTimes)
            {
                float x = startX + (t / maxLen) * timelineWidth;
                if (Mathf.Abs(mPos.x - x) < 5f)
                {
                    GUIContent content = new GUIContent($"{Mathf.RoundToInt(t * 60f)} F");
                    Vector2 labelSize = tooltipStyle.CalcSize(content);
                    Rect labelRect = new Rect(x - (labelSize.x / 2f + 4), centerY - labelSize.y - 12, labelSize.x + 8, labelSize.y);
                    GUI.Label(labelRect, content, tooltipStyle);
                    break;
                }
            }
        }

        // スライダー：centerY を基準に配置
        Rect sliderRect = new Rect(startX - 6, centerY - 10, timelineWidth + 12, 20);
        EditorGUI.BeginChangeCheck();
        float nt = GUI.HorizontalSlider(sliderRect, editor.currentTime, 0f, maxLen);
        if (EditorGUI.EndChangeCheck())
        {
            StopPlayback();
            editor.currentTime = Mathf.Round(nt * 60f) / 60f;
            foreach (var g in editor.warehouse.Values) foreach (var t in g) t.currentValue = t.curve.Evaluate(editor.currentTime);
            SyncBillboardToEditor(); editor.ForceRepaintPreview(true);
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawTopToolbar(VRC_ExpressionEditor editor, AnimationClip clip)
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        Color oldBg = GUI.backgroundColor;
        GUI.backgroundColor = isPlaying ? new Color(1.0f, 0.6f, 0.6f) : new Color(0.6f, 1.0f, 0.6f);
        if (GUILayout.Button(isPlaying ? "⏸ 停止" : "▶ 再生", EditorStyles.toolbarButton, GUILayout.Width(60)))
        {
            if (!isPlaying)
            {
                lastUpdateTime = EditorApplication.timeSinceStartup; lastClip = clip;
                lastMesh = (editor.availableSmrs.Count > editor.selectedSmrIndex) ? editor.availableSmrs[editor.selectedSmrIndex] : null;
                lastTrackReference = editor.warehouse.Values.FirstOrDefault()?.FirstOrDefault();
                if (editor.selectedLayers != null) lastLayers = (bool[])editor.selectedLayers.Clone();
                isPlaying = true;
            }
            else StopPlayback();
        }
        GUI.backgroundColor = oldBg;
        GUILayout.Space(5);
        int currentFrame = Mathf.RoundToInt(editor.currentTime * 60f);
        int totalFrames = clip != null ? Mathf.RoundToInt(clip.length * 60f) : 0;
        EditorGUI.BeginChangeCheck();
        int inputFrame = EditorGUILayout.IntField(currentFrame, GUILayout.Width(40));
        if (EditorGUI.EndChangeCheck())
        {
            StopPlayback();
            editor.currentTime = Mathf.Clamp((float)inputFrame / 60f, 0, clip != null ? clip.length : 0);
            foreach (var g in editor.warehouse.Values) foreach (var t in g) t.currentValue = t.curve.Evaluate(editor.currentTime);
            SyncBillboardToEditor(); editor.ForceRepaintPreview(true);
        }
        GUILayout.Label($"/ {totalFrames} F", EditorStyles.miniLabel);
        if (DrawSpeedButton("x0.5", 0.5f)) playbackSpeed = 0.5f;
        if (DrawSpeedButton("x1", 1.0f)) playbackSpeed = 1.0f;
        if (DrawSpeedButton("x1.5", 1.5f)) playbackSpeed = 1.5f;
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Reset Zoom", EditorStyles.toolbarButton, GUILayout.Width(80))) zoomLevel = 1.0f;
        GUI.backgroundColor = isLoop ? new Color(0.6f, 1.0f, 0.6f) : oldBg;
        if (GUILayout.Button("🔄 ループ", EditorStyles.toolbarButton, GUILayout.Width(70))) isLoop = !isLoop;
        GUI.backgroundColor = oldBg;
        EditorGUILayout.EndHorizontal();
    }

    private void HandleZoomAndScroll()
    {
        Event e = Event.current;
        if (e.type == EventType.ScrollWheel)
        {
            float oldZoom = zoomLevel;
            zoomLevel = Mathf.Clamp(zoomLevel - e.delta.y * 0.15f, 1.0f, 100.0f);
            scrollPos.x *= (zoomLevel / oldZoom);
            e.Use();
        }
        if (e.button == 2 || (e.button == 0 && e.alt))
        {
            if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
            {
                if (e.type == EventType.MouseDrag) scrollPos.x -= e.delta.x;
                e.Use(); Repaint();
            }
        }
    }

    private void DrawRulerAndGrid(Rect areaRect, float startX, float timelineWidth, int totalFrames, float maxLen)
    {
        int step = 1;
        float pixelsPerFrame = timelineWidth / Mathf.Max(totalFrames, 1);
        if (pixelsPerFrame < 30) step = 5;
        if (pixelsPerFrame < 15) step = 10;
        if (pixelsPerFrame < 5) step = 30;

        for (int i = 0; i <= totalFrames; i++)
        {
            float x = startX + (i / 60f / maxLen) * timelineWidth;
            if (i % step == 0)
            {
                GUI.color = Color.white;
                Rect labelRect = new Rect(x - 25, areaRect.y, 50, 15);
                GUI.Label(labelRect, i.ToString(), rulerLabelStyle);
                GUI.color = new Color(1, 1, 1, 0.2f);
                GUI.DrawTexture(new Rect(x, areaRect.y + 15, 1, areaRect.height - 15), EditorGUIUtility.whiteTexture);
            }
        }
        GUI.color = Color.white;
    }

    private void InitializeStyles()
    {
        if (tooltipStyle == null)
        {
            tooltipStyle = new GUIStyle(EditorStyles.helpBox) { fontSize = 11, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            tooltipStyle.normal.textColor = Color.white;
            rulerLabelStyle = new GUIStyle(EditorStyles.label) { fontSize = 9, alignment = TextAnchor.LowerCenter };
            rulerLabelStyle.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
        }
    }

    private bool DrawSpeedButton(string label, float speed)
    {
        Color oldBg = GUI.backgroundColor;
        if (Mathf.Approximately(playbackSpeed, speed)) GUI.backgroundColor = new Color(0.4f, 0.8f, 1.0f);
        bool pressed = GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(35));
        GUI.backgroundColor = oldBg;
        return pressed;
    }
}