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
    private bool isLoop = true;

    private AnimationClip lastClip;
    private int lastDirtyCount;
    private bool[] lastLayers;
    private ExpressionTrack lastTrackReference;

    // ★追加：キーフレームの時間を覚えておくためのメモ
    private List<float> cachedKeyframeTimes = new List<float>();

    [MenuItem("Tools/VRC Expression Editor/Animation Timeline")]
    public static void OpenWindow()
    {
        Instance = GetWindow<VRC_ExpressionTimeline>("表情タイムライン");
        Instance.minSize = new Vector2(400, 85);
        Instance.Show();
    }

    private void OnEnable() { Instance = this; EditorApplication.update += OnUpdate; }
    private void OnDisable() { StopPlayback(); EditorApplication.update -= OnUpdate; }

    public void StopPlayback()
    {
        if (isPlaying)
        {
            isPlaying = false;
            SyncBillboardToEditor();
            Repaint();
        }
    }

    private void OnUpdate()
    {
        if (!isPlaying) return;

        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) return;

        AnimationClip currentClip = (editor.availableClips.Count > editor.selectedClipIndex) ? editor.availableClips[editor.selectedClipIndex] : null;
        if (currentClip != lastClip) { StopPlayback(); return; }
        if (editor.GetDirtyCount() != lastDirtyCount) { StopPlayback(); return; }
        var currentFirstTrack = editor.warehouse.Values.FirstOrDefault()?.FirstOrDefault();
        if (currentFirstTrack != lastTrackReference) { StopPlayback(); return; }
        if (editor.selectedLayers != null && lastLayers != null && !editor.selectedLayers.SequenceEqual(lastLayers)) { StopPlayback(); return; }
        if (VRC_ExpressionPreview.Instance != null && (VRC_ExpressionPreview.Instance.GetIsComparing() || VRC_ExpressionPreview.Instance.GetIsMuted())) { StopPlayback(); return; }

        double deltaTime = EditorApplication.timeSinceStartup - lastUpdateTime;
        lastUpdateTime = EditorApplication.timeSinceStartup;
        editor.currentTime += (float)deltaTime * playbackSpeed;

        float clipLength = (currentClip != null) ? currentClip.length : 1.0f;
        if (editor.currentTime > clipLength)
        {
            if (isLoop) editor.currentTime = 0f;
            else { editor.currentTime = clipLength; StopPlayback(); return; }
        }

        foreach (var pathGroup in editor.warehouse.Values)
            foreach (var track in pathGroup) track.currentValue = track.curve.Evaluate(editor.currentTime);

        editor.ForceRepaintPreview(true);
        Repaint();
    }

    // ★追加：倉庫を調べて「何秒目にキーがあるか」のリストを作る
    private void UpdateKeyframeCache(VRC_ExpressionEditor editor)
    {
        cachedKeyframeTimes.Clear();
        foreach (var pathGroup in editor.warehouse.Values)
        {
            foreach (var track in pathGroup)
            {
                // 全てのカーブから「時間」だけを抜き出す
                foreach (var key in track.curve.keys)
                {
                    cachedKeyframeTimes.Add(key.time);
                }
            }
        }
        // 重複した時間を削って、整理する
        cachedKeyframeTimes = cachedKeyframeTimes.Distinct().ToList();
    }

    private void SyncBillboardToEditor()
    {
        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) return;
        foreach (var pathGroup in editor.warehouse)
        {
            string path = pathGroup.Key;
            foreach (var track in pathGroup.Value)
            {
                if (editor.clipExpressionValues.ContainsKey(path))
                    editor.clipExpressionValues[path][track.label] = track.currentValue;
                string currentSmrPath = (editor.availableSmrs.Count > editor.selectedSmrIndex) ? editor.GetRelativePath(editor.availableSmrs[editor.selectedSmrIndex].gameObject) : "";
                if (path == currentSmrPath)
                    editor.currentExpressionValues[track.label] = track.currentValue;
            }
        }
        editor.Repaint();
    }

    private void OnGUI()
    {
        if (EditorStyles.label == null) return;

        var editor = VRC_ExpressionEditor.Instance;
        if (editor == null) { EditorGUILayout.HelpBox("エディタを先に開いてください", MessageType.Info); return; }
        AnimationClip clip = (editor.availableClips.Count > editor.selectedClipIndex) ? editor.availableClips[editor.selectedClipIndex] : null;

        // ★クリップが変わっていたらドットの位置を再計算する
        if (clip != lastClip) { UpdateKeyframeCache(editor); }

        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        Color oldBg = GUI.backgroundColor;

        // 1. 再生ボタン
        GUI.backgroundColor = isPlaying ? new Color(1.0f, 0.6f, 0.6f) : new Color(0.6f, 1.0f, 0.6f);
        if (GUILayout.Button(isPlaying ? "⏸ 停止" : "▶ 再生", EditorStyles.toolbarButton, GUILayout.Width(60)))
        {
            if (!isPlaying)
            {
                lastUpdateTime = EditorApplication.timeSinceStartup;
                lastClip = clip;
                lastDirtyCount = editor.GetDirtyCount();
                lastTrackReference = editor.warehouse.Values.FirstOrDefault()?.FirstOrDefault();
                if (editor.selectedLayers != null) lastLayers = (bool[])editor.selectedLayers.Clone();
                UpdateKeyframeCache(editor); // 再生開始時にも念のため更新
                isPlaying = true;
            }
            else StopPlayback();
        }
        GUI.backgroundColor = oldBg;

        GUILayout.Space(5);

        // 2. フレーム入力
        int currentFrame = Mathf.RoundToInt(editor.currentTime * 60f);
        int totalFrames = clip != null ? Mathf.RoundToInt(clip.length * 60f) : 0;
        EditorGUI.BeginChangeCheck();
        int inputFrame = EditorGUILayout.IntField(currentFrame, GUILayout.Width(45));
        if (EditorGUI.EndChangeCheck())
        {
            StopPlayback();
            editor.currentTime = Mathf.Max(0, inputFrame / 60f);
            foreach (var group in editor.warehouse.Values)
                foreach (var track in group) track.currentValue = track.curve.Evaluate(editor.currentTime);
            SyncBillboardToEditor();
            editor.ForceRepaintPreview(true);
        }
        GUILayout.Label($"/ {totalFrames} F", EditorStyles.miniLabel);

        GUILayout.Space(10);

        // 3. 速度ボタン
        if (DrawSpeedButton("x0.5", 0.5f)) playbackSpeed = 0.5f;
        if (DrawSpeedButton("x1", 1.0f)) playbackSpeed = 1.0f;
        if (DrawSpeedButton("x1.5", 1.5f)) playbackSpeed = 1.5f;

        GUILayout.FlexibleSpace();

        // 4. ループボタン
        GUI.backgroundColor = isLoop ? new Color(0.6f, 1.0f, 0.6f) : oldBg;
        if (GUILayout.Button("🔄 ループ", EditorStyles.toolbarButton, GUILayout.Width(70))) isLoop = !isLoop;
        GUI.backgroundColor = oldBg;

        EditorGUILayout.EndHorizontal();

        // --- 5. タイムラインスライダーとドットの描画 ---
        float maxLen = clip != null ? clip.length : 0.001f;

        // スライダーの描画エリア（Rect）を先に確保する
        Rect sliderRect = EditorGUILayout.GetControlRect(GUILayout.Height(20));

        // A. 【背景】キーフレーム（ドット）の描画
        if (Event.current.type == EventType.Repaint)
        {
            GUI.color = new Color(1, 1, 1, 0.5f); // 少し透明な白
            foreach (float t in cachedKeyframeTimes)
            {
                // スライダー内での横位置を計算
                float x = sliderRect.x + (t / Mathf.Max(maxLen, 0.001f)) * (sliderRect.width - 12) + 6;
                // 小さな縦線を描く
                GUI.DrawTexture(new Rect(x - 1, sliderRect.y + 4, 2, 8), EditorGUIUtility.whiteTexture);
            }
            GUI.color = Color.white;
        }

        // B. 【ホバー】マウスがドットの近くにあるかチェック
        Vector2 mousePos = Event.current.mousePosition;
        if (sliderRect.Contains(mousePos))
        {
            foreach (float t in cachedKeyframeTimes)
            {
                float x = sliderRect.x + (t / Mathf.Max(maxLen, 0.001f)) * (sliderRect.width - 12) + 6;
                if (Mathf.Abs(mousePos.x - x) < 5f) // マウスが横5ピクセル以内にあれば
                {
                    // ツールチップ的なラベルを表示
                    GUI.Label(new Rect(mousePos.x, mousePos.y - 20, 60, 18), $"{Mathf.RoundToInt(t * 60f)} F", EditorStyles.helpBox);
                    break;
                }
            }
        }

        // C. 【本体】スライダーを重ねて描画
        EditorGUI.BeginChangeCheck();
        float newTime = GUI.HorizontalSlider(sliderRect, editor.currentTime, 0f, Mathf.Max(maxLen, editor.currentTime));
        if (EditorGUI.EndChangeCheck())
        {
            StopPlayback();
            editor.currentTime = newTime;
            foreach (var group in editor.warehouse.Values)
                foreach (var track in group) track.currentValue = track.curve.Evaluate(editor.currentTime);
            SyncBillboardToEditor();
            editor.ForceRepaintPreview(true);
        }
    }

    private bool DrawSpeedButton(string label, float speed)
    {
        Color oldBg = GUI.backgroundColor;
        if (Mathf.Approximately(playbackSpeed, speed)) GUI.backgroundColor = Color.cyan;
        bool pressed = GUILayout.Button(label, EditorStyles.toolbarButton, GUILayout.Width(40));
        GUI.backgroundColor = oldBg;
        return pressed;
    }
}