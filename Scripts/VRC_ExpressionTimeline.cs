using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

public class VRC_ExpressionTimeline : EditorWindow
{
    public static VRC_ExpressionTimeline Instance { get; private set; }
    private bool isPlaying = false;

    // --- 再生・節約管理用の変数 ---
    private float playbackSpeed = 1.0f;
    private double lastUpdateTime;
    private double lastCheckTime;
    private bool isLoop = true;

    // --- 監視用のスナップショット ---
    private AnimationClip lastClip;
    private SkinnedMeshRenderer lastMesh;
    private bool[] lastLayers;
    private ExpressionTrack lastTrackReference;
    private List<float> cachedKeyframeTimes = new List<float>();
    private float maxKeyTime = 0f; // 最終キーフレームの時間

    private GUIStyle tooltipStyle;
    private GUIStyle rulerLabelStyle;

    // ズームとスクロール用の変数
    private float zoomLevel = 1.0f;
    private float visibleDuration = 5.0f; // 動的に上書きされます
    private Vector2 scrollPos;

    // 表情アニメの標準規格：5秒（300フレーム）の初期空き地を確保
    private const float BASE_DURATION = 5.0f;

    [MenuItem("Tools/VRC Expression Editor/Animation Timeline")]
    public static void OpenWindow()
    {
        Instance = GetWindow<VRC_ExpressionTimeline>("表情タイムライン");
        Instance.minSize = new Vector2(400, 95);
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

        // --- 再生中の監視ロジック ---
        var currentFirstTrack = editor.warehouse.Values.FirstOrDefault()?.FirstOrDefault();
        if (currentFirstTrack != lastTrackReference) { StopPlayback(); return; }

        SkinnedMeshRenderer currentMesh = (editor.availableSmrs.Count > editor.selectedSmrIndex) ? editor.availableSmrs[editor.selectedSmrIndex] : null;
        if (currentMesh != lastMesh) { StopPlayback(); lastMesh = currentMesh; return; }

        if (editor.selectedLayers != null && lastLayers != null && !editor.selectedLayers.SequenceEqual(lastLayers)) { StopPlayback(); return; }

        double deltaTime = currentTimeSinceStartup - lastUpdateTime;
        lastUpdateTime = currentTimeSinceStartup;
        editor.currentTime += (float)deltaTime * playbackSpeed;

        // ループ判定の基準時間を「最終キー時間」または「クリップ長」から動的に算出（最小0.01秒を確保）
        float animationLength = Mathf.Max(maxKeyTime, (currentClip != null) ? currentClip.length : BASE_DURATION);
        float loopEndTime = Mathf.Max(animationLength, 0.01f);

        if (editor.currentTime > loopEndTime)
        {
            if (isLoop)
            {
                editor.currentTime = 0f;
            }
            else
            {
                editor.currentTime = Mathf.Round(loopEndTime * 60f) / 60f;
                StopPlayback();
                return;
            }
        }

        foreach (var pathGroup in editor.warehouse.Values)
            foreach (var track in pathGroup) track.currentValue = track.curve.Evaluate(editor.currentTime);

        editor.ForceRepaintPreview(true);
        Repaint();
    }

    public void UpdateKeyframeCache(VRC_ExpressionEditor editor)
    {
        cachedKeyframeTimes.Clear();
        foreach (var list in editor.warehouse.Values)
            foreach (var t in list)
                foreach (var k in t.curve.keys) cachedKeyframeTimes.Add(k.time);
        cachedKeyframeTimes = cachedKeyframeTimes.Distinct().ToList();

        // 最終キーフレームの位置（時間）を更新
        maxKeyTime = cachedKeyframeTimes.Count > 0 ? cachedKeyframeTimes.Max() : 0f;

        // 初期表示範囲（visibleDuration）を、アニメーション全体の長さ＋2%のマージンに自動設定
        AnimationClip clip = (editor.availableClips.Count > editor.selectedClipIndex) ? editor.availableClips[editor.selectedClipIndex] : null;
        float rawLength = Mathf.Max(maxKeyTime, (clip != null) ? clip.length : BASE_DURATION);

        // 極端に短いアニメーションや0秒のアニメの場合でも、最低1.0秒（60フレーム）の表示領域を確保
        visibleDuration = Mathf.Max(1.0f, rawLength * 1.02f);
        zoomLevel = 1.0f; // 表示を初期化

        Repaint();
    }

    private void SyncBillboardToEditor()
    {
        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) return;
        string currentSmrPath = (editor.availableSmrs.Count > editor.selectedSmrIndex) ? editor.GetRelativePath(editor.availableSmrs[editor.selectedSmrIndex].gameObject) : "";
        foreach (var pathGroup in editor.warehouse)
        {
            string path = pathGroup.Key;
            foreach (var track in pathGroup.Value)
            {
                if (editor.clipExpressionValues.ContainsKey(path))
                    editor.clipExpressionValues[path][track.label] = track.currentValue;
                if (path == currentSmrPath)
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

        if (clip != lastClip) { UpdateKeyframeCache(editor); }

        DrawTopToolbar(editor, clip);

        // ズーム計算（HandleZoom）はScrollViewの外側で実行
        HandleZoom();

        float animationLength = Mathf.Max(maxKeyTime, (clip != null) ? clip.length : BASE_DURATION);
        float rawMaxLen = Mathf.Max(animationLength, editor.currentTime);

        float visibleMaxLen;
        float timelineWidth;
        float viewWidth = position.width - 24;
        float startX = 12;

        if (visibleDuration > rawMaxLen)
        {
            visibleMaxLen = visibleDuration;
            timelineWidth = viewWidth;
        }
        else
        {
            // 最大時間に2%のマージンをプラスして右側に余白を作成
            visibleMaxLen = rawMaxLen * 1.02f;
            timelineWidth = viewWidth * (visibleMaxLen / visibleDuration);
        }

        int totalFrames = Mathf.RoundToInt(visibleMaxLen * 60f);

        float toolbarHeight = 22f;
        float scrollbarHeight = 16f;
        float availableHeight = position.height - toolbarHeight - scrollbarHeight;

        // 【修正】GUILayout.BeginScrollView を用いて縦スクロールバーのみ GUIStyle.none（非表示）を割り当て
        scrollPos = GUILayout.BeginScrollView(
            scrollPos,
            false,
            false,
            GUI.skin.horizontalScrollbar,
            GUIStyle.none,
            GUILayout.ExpandHeight(true)
        );

        // 【修正】高さを元の設計に戻し、二重マイナスによるずれを防止
        GUILayout.Label("", GUILayout.Width(timelineWidth), GUILayout.Height(Mathf.Max(availableHeight, 40)));
        Rect areaRect = GUILayoutUtility.GetLastRect();

        float centerY = areaRect.y + (areaRect.height / 2f) + 8f; // 重心調整

        if (Event.current.type == EventType.Repaint)
        {
            // 目盛りの描画（中央揃え・画面外カリング対応版）
            DrawRulerAndGrid(areaRect, startX, timelineWidth, totalFrames, visibleMaxLen, viewWidth, centerY);

            // キーフレーム（白い垂直線・画面内にある場合のみ描画）
            GUI.color = Color.white;
            foreach (float t in cachedKeyframeTimes)
            {
                if (t <= visibleMaxLen)
                {
                    float x = startX + (t / visibleMaxLen) * timelineWidth;
                    if (x >= scrollPos.x - 2f && x <= scrollPos.x + viewWidth + 2f)
                    {
                        GUI.DrawTexture(new Rect(x - 1, centerY - 10, 2, 20), EditorGUIUtility.whiteTexture);
                    }
                }
            }

            // ループ終端（落ち着いたブルーの垂直線・画面内にある場合のみ描画）
            float loopEndX = startX + (maxKeyTime / visibleMaxLen) * timelineWidth;
            if (maxKeyTime <= visibleMaxLen && loopEndX >= scrollPos.x - 2f && loopEndX <= scrollPos.x + viewWidth + 2f)
            {
                // 画像から抽出した目に優しいくすみブルー (#53A3D4)
                GUI.color = new Color(0.325f, 0.639f, 0.831f, 1.0f);
                GUI.DrawTexture(new Rect(loopEndX - 1, areaRect.y + 15, 2, areaRect.height - 15), EditorGUIUtility.whiteTexture);
            }

            // 再生ヘッド（赤い垂直線・画面内にある場合のみ描画）
            float playheadX = startX + (editor.currentTime / visibleMaxLen) * timelineWidth;
            if (playheadX >= scrollPos.x - 2f && playheadX <= scrollPos.x + viewWidth + 2f)
            {
                GUI.color = new Color(1f, 0.3f, 0.3f, 1f);
                GUI.DrawTexture(new Rect(playheadX - 1, areaRect.y + 15, 2, areaRect.height - 15), EditorGUIUtility.whiteTexture);
            }
            GUI.color = Color.white;
        }

        // ホバーツールチップ
        Vector2 mPos = Event.current.mousePosition;
        if (areaRect.Contains(mPos))
        {
            foreach (float t in cachedKeyframeTimes)
            {
                if (t <= visibleMaxLen)
                {
                    float x = startX + (t / visibleMaxLen) * timelineWidth;
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
        }

        // スライダー
        Rect sliderRect = new Rect(startX - 6, centerY - 10, timelineWidth + 12, 20);
        EditorGUI.BeginChangeCheck();
        float nt = GUI.HorizontalSlider(sliderRect, editor.currentTime, 0f, visibleMaxLen);
        if (EditorGUI.EndChangeCheck())
        {
            StopPlayback();
            editor.currentTime = Mathf.Round(nt * 60f) / 60f;
            foreach (var g in editor.warehouse.Values) foreach (var t in g) t.currentValue = t.curve.Evaluate(editor.currentTime);
            SyncBillboardToEditor(); editor.ForceRepaintPreview(true);
        }
        GUILayout.EndScrollView();
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
            editor.currentTime = Mathf.Max(0f, (float)inputFrame / 60f);
            foreach (var g in editor.warehouse.Values) foreach (var t in g) t.currentValue = t.curve.Evaluate(editor.currentTime);
            SyncBillboardToEditor(); editor.ForceRepaintPreview(true);
        }
        GUILayout.Label($"/ {totalFrames} F", EditorStyles.miniLabel);
        if (DrawSpeedButton("x0.5", 0.5f)) playbackSpeed = 0.5f;
        if (DrawSpeedButton("x1", 1.0f)) playbackSpeed = 1.0f;
        if (DrawSpeedButton("x1.5", 1.5f)) playbackSpeed = 1.5f;

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Reset Zoom", EditorStyles.toolbarButton, GUILayout.Width(80))) { StopPlayback(); zoomLevel = 1.0f; UpdateKeyframeCache(editor); editor.currentTime = 0f; }
        GUI.backgroundColor = isLoop ? new Color(0.6f, 1.0f, 0.6f) : oldBg;
        if (GUILayout.Button("🔄 ループ", EditorStyles.toolbarButton, GUILayout.Width(70))) isLoop = !isLoop;
        GUI.backgroundColor = oldBg;
        EditorGUILayout.EndHorizontal();
    }

    private void HandleZoom()
    {
        Event e = Event.current;
        if (e.type == EventType.ScrollWheel)
        {
            float oldZoom = zoomLevel;
            float step = 1.30f;

            var editor = VRC_ExpressionEditor.Instance;
            if (editor == null) return;
            AnimationClip clip = (editor.availableClips.Count > editor.selectedClipIndex) ? editor.availableClips[editor.selectedClipIndex] : null;
            float animationLength = Mathf.Max(maxKeyTime, (clip != null) ? clip.length : BASE_DURATION);

            // ズーム用の最大時間を算出
            float rawMaxLen = Mathf.Max(animationLength, editor.currentTime);

            // 2%のマージンを考慮
            float visibleMaxLenBefore = (visibleDuration > rawMaxLen) ? visibleDuration : rawMaxLen * 1.02f;

            // スクロール内のマウスの絶対座標を計算
            float mouseLocalX = e.mousePosition.x;
            float viewWidth = position.width - 24;
            float oldTimelineWidth = viewWidth * Mathf.Max(1.0f, oldZoom);

            // マウスの真下の「時間（秒）」を、余白を含めた全体の幅から正確に逆算
            float timeAtMouse = ((scrollPos.x + mouseLocalX - 12f) / oldTimelineWidth) * visibleMaxLenBefore;

            if (e.delta.y > 0)
            {
                visibleDuration = Mathf.Min(visibleDuration * step, 500f);
            }
            else
            {
                visibleDuration = Mathf.Max(visibleDuration / step, 0.5f);
            }

            float visibleMaxLenAfter = (visibleDuration > rawMaxLen) ? visibleDuration : rawMaxLen * 1.02f;
            zoomLevel = visibleMaxLenAfter / visibleDuration;
            float newTimelineWidth = viewWidth * Mathf.Max(1.0f, zoomLevel);

            // ズーム後に、マウスの下にあったフレームが完全に同じピクセル位置に留まるようにスクロール位置を調整
            scrollPos.x = (timeAtMouse / visibleMaxLenAfter) * newTimelineWidth - mouseLocalX + 12f;
            scrollPos.x = Mathf.Clamp(scrollPos.x, 0f, Mathf.Max(0f, newTimelineWidth - viewWidth));

            e.Use();
            Repaint();
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

    private void DrawRulerAndGrid(Rect areaRect, float startX, float timelineWidth, int totalFrames, float maxLen, float viewWidth, float centerY)
    {
        float pixelsPerFrame = timelineWidth / Mathf.Max(totalFrames, 1);

        // 主目盛り（長い目盛り）の間隔を決定
        int step = 10;
        if (pixelsPerFrame < 2.5f) step = 30; // 1Fあたり2.5px未満で30F刻みに
        if (pixelsPerFrame < 0.8f) step = 60;
        if (pixelsPerFrame < 0.4f) step = 120;
        if (pixelsPerFrame < 0.1f) step = 300;

        // 現在の表示領域に存在するフレーム番号の範囲のみを算出（画面外カリング）
        int startFrame = Mathf.FloorToInt(scrollPos.x / pixelsPerFrame);
        startFrame = Mathf.Max(0, startFrame - 1); // 画面左端での見切れを防止するためのマージン

        int endFrame = Mathf.CeilToInt((scrollPos.x + viewWidth) / pixelsPerFrame);
        endFrame = Mathf.Min(totalFrames, endFrame + 1); // 画面右端での見切れを防止するためのマージン

        // 元コードの「長い縦線の長さ（高さ）」を算出
        float longLineHeight = areaRect.height - 15f;
        float longLineY = areaRect.y + 15f;

        for (int i = startFrame; i <= endFrame; i++)
        {
            float x = startX + ((i / 60f) / maxLen) * timelineWidth;

            // ①：主目盛り（10F刻みの区切り線 ＋ 数値テキスト）
            if (i % step == 0)
            {
                // テキスト（上部に配置）
                GUI.color = new Color(1f, 1f, 1f, 0.7f);
                Rect labelRect = new Rect(x - 25, areaRect.y, 50, 15);
                GUI.Label(labelRect, i.ToString(), rulerLabelStyle);

                // 上から下まで届く長い縦線（元のコードの仕様に戻してスライダー全体を貫通）
                GUI.color = new Color(1f, 1f, 1f, 0.40f);
                GUI.DrawTexture(new Rect(x, longLineY, 1, longLineHeight), EditorGUIUtility.whiteTexture);
            }
            // ②：副目盛り（1Fごとの細かい短い線・数値なし）
            // 1Fあたりの幅が 3.0ピクセル 以上離れている場合のみ処理
            else if (pixelsPerFrame >= 3.0f)
            {
                // 主目盛りの長さ（longLineHeight）の「50%」の高さで、中央スライダー（centerY）を基準に中央揃えで描画
                float shortLineHeight = longLineHeight * 0.8f;
                float shortLineY = centerY - (shortLineHeight / 2f);

                GUI.color = new Color(1f, 1f, 1f, 0.25f);
                GUI.DrawTexture(new Rect(x, shortLineY, 1, shortLineHeight), EditorGUIUtility.whiteTexture);
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
            rulerLabelStyle.normal.textColor = new Color(1f, 1f, 1f, 0.7f);
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