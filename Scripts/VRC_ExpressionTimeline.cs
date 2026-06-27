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
    private bool isKeySnap = true; // キー吸着モード

    // --- 監視用のスナップショット ---
    private AnimationClip lastClip;
    private SkinnedMeshRenderer lastMesh;
    private bool[] lastLayers;
    private ExpressionTrack lastTrackReference;
    private List<float> cachedKeyframeTimes = new List<float>(); // 白い線用の実データ時間
    private float maxKeyTime = 0f;

    // --- UI表示・選択用の変数（実データと同期） ---
    private List<float> displayKeyTimes = new List<float>();
    private HashSet<float> selectedKeyTimes = new HashSet<float>();

    // ドラッグ制御用の一時変数
    private bool isDraggingKey = false;
    private float dragStartMouseX = 0f;
    private List<float> dragStartKeyTimes = new List<float>();

    // 範囲選択（ボックス選択）用の変数
    private bool isBoxSelecting = false;
    private Vector2 boxSelectStart;
    private Rect boxSelectRect;

    // 確定した選択ボックスの維持・ドラッグ用変数
    private float selectedRangeStart = -1f; // 確定選択範囲の開始時間（秒）
    private float selectedRangeEnd = -1f;   // 確定選択範囲の終了時間（秒）
    private bool isDraggingRange = false;   // 選択ボックス自体をドラッグ中か
    private float dragStartRangeStart = 0f;
    private float dragStartRangeEnd = 0f;

    private GUIStyle rulerLabelStyle;

    private float zoomLevel = 1.0f;
    private float visibleDuration = 5.0f;
    private Vector2 scrollPos;

    private const float BASE_DURATION = 5.0f;

    [MenuItem("Tools/VRC Expression Editor/Animation Timeline")]
    public static void OpenWindow()
    {
        Instance = GetWindow<VRC_ExpressionTimeline>("表情タイムライン");
        Instance.minSize = new Vector2(400, 115);
        Instance.maxSize = new Vector2(4000, 4000);
        Instance.Show();
    }

    private void OnEnable()
    {
        Instance = this;
        EditorApplication.update += OnUpdate;
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    private void OnDisable()
    {
        StopPlayback();
        EditorApplication.update -= OnUpdate;
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    private void OnUndoRedo()
    {
        var editor = VRC_ExpressionEditor.Instance;
        if (editor != null)
        {
            editor.RefreshExpressionCache();
            UpdateKeyframeCache(editor);
            editor.ForceRepaintPreview(true);
            Repaint();
        }
    }

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

        float animationLength = Mathf.Max(maxKeyTime, (currentClip != null) ? currentClip.length : BASE_DURATION);
        float loopEndTime = Mathf.Max(animationLength, 0.01f);

        if (editor.currentTime > loopEndTime)
        {
            if (isLoop) editor.currentTime = 0f;
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

        displayKeyTimes = new List<float>(cachedKeyframeTimes);
        selectedKeyTimes.Clear();
        selectedRangeStart = -1f;
        selectedRangeEnd = -1f;

        maxKeyTime = cachedKeyframeTimes.Count > 0 ? cachedKeyframeTimes.Max() : 0f;
        AnimationClip clip = (editor.availableClips.Count > editor.selectedClipIndex) ? editor.availableClips[editor.selectedClipIndex] : null;
        float rawLength = Mathf.Max(maxKeyTime, (clip != null) ? clip.length : BASE_DURATION);

        visibleDuration = Mathf.Max(1.0f, rawLength * 1.02f);
        zoomLevel = 1.0f;

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

    private float GetSnappedTime(float rawTime)
    {
        if (isKeySnap && displayKeyTimes.Count > 0)
        {
            float closestKey = displayKeyTimes.OrderBy(t => Mathf.Abs(t - rawTime)).First();
            if (Mathf.Abs(closestKey - rawTime) <= 1.5f / 60f) return closestKey;
        }
        return Mathf.Round(rawTime * 60f) / 60f;
    }

    private float GetSnappedTimeAtX(float pixelX, float startX, float timelineWidth, float visibleMaxLen)
    {
        float rawTime = ((pixelX - startX) / timelineWidth) * visibleMaxLen;
        rawTime = Mathf.Clamp(rawTime, 0f, visibleMaxLen);
        return GetSnappedTime(rawTime);
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
        HandleZoom();

        float animationLength = Mathf.Max(maxKeyTime, (clip != null) ? clip.length : BASE_DURATION);
        float rawMaxLen = Mathf.Max(animationLength, editor.currentTime);

        float visibleMaxLen = (visibleDuration > rawMaxLen) ? visibleDuration : rawMaxLen * 1.02f;
        float viewWidth = position.width - 24;
        float timelineWidth = viewWidth * (visibleMaxLen / visibleDuration);
        float startX = 12;

        int totalFrames = Mathf.RoundToInt(visibleMaxLen * 60f);
        float availableHeight = position.height - 22f - 16f;

        scrollPos = GUILayout.BeginScrollView(
            scrollPos,
            false,
            false,
            GUI.skin.horizontalScrollbar,
            GUIStyle.none,
            GUILayout.ExpandHeight(true)
        );

        float contentHeight = Mathf.Max(availableHeight, 65f);
        GUILayout.Label("", GUILayout.Width(timelineWidth), GUILayout.Height(contentHeight));
        Rect areaRect = GUILayoutUtility.GetLastRect();

        float centerY = areaRect.y + (areaRect.height / 2f) + 16f;
        float keyLaneY = areaRect.y + 15f;

        HandleMouseEvents(startX, timelineWidth, visibleMaxLen, viewWidth, keyLaneY);

        if (Event.current.type == EventType.Repaint)
        {
            DrawRulerAndGrid(areaRect, startX, timelineWidth, totalFrames, visibleMaxLen, viewWidth, centerY);

            Rect bgRect = new Rect(scrollPos.x, keyLaneY, viewWidth + 24, 16f);
            EditorGUI.DrawRect(bgRect, new Color(0.18f, 0.18f, 0.18f, 1.0f));

            if (!isBoxSelecting && selectedRangeStart >= 0f && selectedKeyTimes.Count > 0)
            {
                float rxStart = startX + (selectedRangeStart / visibleMaxLen) * timelineWidth;
                float rxEnd = startX + (selectedRangeEnd / visibleMaxLen) * timelineWidth;
                Rect rangeBoxRect = new Rect(rxStart, keyLaneY, Mathf.Max(2f, rxEnd - rxStart), 16f);
                DrawOutlineRect(rangeBoxRect, new Color(0.325f, 0.639f, 0.831f, 0.08f), new Color(0.325f, 0.639f, 0.831f, 0.40f));
            }

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
            GUI.color = Color.white;

            foreach (float t in displayKeyTimes)
            {
                if (t <= visibleMaxLen)
                {
                    float x = startX + (t / visibleMaxLen) * timelineWidth;
                    if (x >= scrollPos.x - 10f && x <= scrollPos.x + viewWidth + 10f)
                    {
                        float diamondY = keyLaneY + 8f;
                        if (selectedKeyTimes.Contains(t))
                            DrawDiamond(x, diamondY, 8f, new Color(0.325f, 0.639f, 0.831f, 1.0f), true);
                        else
                            DrawDiamond(x, diamondY, 8f, Color.white, false);
                    }
                }
            }
            GUI.color = Color.white;

            float loopEndX = startX + (maxKeyTime / visibleMaxLen) * timelineWidth;
            if (maxKeyTime <= visibleMaxLen && loopEndX >= scrollPos.x - 2f && loopEndX <= scrollPos.x + viewWidth + 2f)
            {
                GUI.color = new Color(0.325f, 0.639f, 0.831f, 1.0f);
                GUI.DrawTexture(new Rect(loopEndX - 1, areaRect.y + 15, 2, areaRect.height - 15), EditorGUIUtility.whiteTexture);
            }

            float playheadX = startX + (editor.currentTime / visibleMaxLen) * timelineWidth;
            if (playheadX >= scrollPos.x - 2f && playheadX <= scrollPos.x + viewWidth + 2f)
            {
                GUI.color = new Color(1f, 0.3f, 0.3f, 1f);
                GUI.DrawTexture(new Rect(playheadX - 1, areaRect.y + 15, 2, areaRect.height - 15), EditorGUIUtility.whiteTexture);
            }
            GUI.color = Color.white;

            if (isBoxSelecting)
            {
                DrawOutlineRect(boxSelectRect, new Color(0.325f, 0.639f, 0.831f, 0.15f), new Color(0.325f, 0.639f, 0.831f, 0.8f));
            }
        }

        Rect sliderRect = new Rect(startX - 6, centerY - 10f, timelineWidth + 12, 20);
        EditorGUI.BeginChangeCheck();
        float nt = GUI.HorizontalSlider(sliderRect, editor.currentTime, 0f, visibleMaxLen);
        if (EditorGUI.EndChangeCheck())
        {
            StopPlayback();
            editor.currentTime = GetSnappedTime(nt);
            foreach (var g in editor.warehouse.Values) foreach (var t in g) t.currentValue = t.curve.Evaluate(editor.currentTime);
            SyncBillboardToEditor(); editor.ForceRepaintPreview(true);
        }
        GUILayout.EndScrollView();
    }

    private void HandleMouseEvents(float startX, float timelineWidth, float visibleMaxLen, float viewWidth, float keyLaneY)
    {
        Event e = Event.current;
        Vector2 mPos = e.mousePosition;

        switch (e.type)
        {
            case EventType.MouseDown:
                if (e.button == 0)
                {
                    bool insideKeyLane = mPos.y >= keyLaneY && mPos.y <= keyLaneY + 20f;

                    if (insideKeyLane)
                    {
                        float clickedTime = -1f;
                        foreach (float t in displayKeyTimes)
                        {
                            float x = startX + (t / visibleMaxLen) * timelineWidth;
                            if (Mathf.Abs(mPos.x - x) <= 8f)
                            {
                                clickedTime = t;
                                break;
                            }
                        }

                        float clickTimeSnap = GetSnappedTimeAtX(mPos.x, startX, timelineWidth, visibleMaxLen);
                        bool clickedInsideSelectionBox = selectedRangeStart >= 0f &&
                                                         clickTimeSnap >= selectedRangeStart &&
                                                         clickTimeSnap <= selectedRangeEnd;

                        if (clickedInsideSelectionBox)
                        {
                            isDraggingRange = true;
                            dragStartMouseX = mPos.x;
                            dragStartRangeStart = selectedRangeStart;
                            dragStartRangeEnd = selectedRangeEnd;
                            dragStartKeyTimes = new List<float>(selectedKeyTimes);
                            e.Use();
                        }
                        else if (clickedTime >= 0f)
                        {
                            if (e.shift)
                            {
                                if (selectedKeyTimes.Contains(clickedTime)) selectedKeyTimes.Remove(clickedTime);
                                else selectedKeyTimes.Add(clickedTime);
                            }
                            else
                            {
                                if (!selectedKeyTimes.Contains(clickedTime))
                                {
                                    selectedKeyTimes.Clear();
                                    selectedKeyTimes.Add(clickedTime);
                                }
                            }

                            isDraggingKey = true;
                            dragStartMouseX = mPos.x;
                            dragStartKeyTimes = new List<float>(selectedKeyTimes);

                            if (selectedKeyTimes.Count > 0)
                            {
                                selectedRangeStart = selectedKeyTimes.Min();
                                selectedRangeEnd = selectedKeyTimes.Max();
                            }

                            e.Use();
                        }
                        else
                        {
                            selectedKeyTimes.Clear();
                            selectedRangeStart = -1f;
                            selectedRangeEnd = -1f;

                            isBoxSelecting = true;
                            boxSelectStart = mPos;
                            float tSnap = GetSnappedTimeAtX(mPos.x, startX, timelineWidth, visibleMaxLen);
                            float xSnap = startX + (tSnap / visibleMaxLen) * timelineWidth;
                            boxSelectRect = new Rect(xSnap, keyLaneY, 0, 16f);
                            e.Use();
                        }
                    }
                    else
                    {
                        if (selectedKeyTimes.Count > 0)
                        {
                            selectedKeyTimes.Clear();
                            selectedRangeStart = -1f;
                            selectedRangeEnd = -1f;
                            Repaint();
                        }
                    }
                }
                break;

            case EventType.MouseDrag:
                if (e.button == 0)
                {
                    if (isDraggingRange)
                    {
                        float deltaX = mPos.x - dragStartMouseX;
                        float deltaTime = Mathf.Round((deltaX / timelineWidth) * visibleMaxLen * 60f) / 60f;

                        selectedRangeStart = Mathf.Clamp(dragStartRangeStart + deltaTime, 0f, visibleMaxLen);
                        selectedRangeEnd = Mathf.Clamp(dragStartRangeEnd + deltaTime, 0f, visibleMaxLen);

                        List<float> newTimes = new List<float>();
                        for (int i = 0; i < dragStartKeyTimes.Count; i++)
                        {
                            float newTime = Mathf.Clamp(dragStartKeyTimes[i] + deltaTime, 0f, visibleMaxLen);
                            newTimes.Add(newTime);
                        }

                        // エラー修正箇所：実データから描画情報を再構築することで安全に更新
                        displayKeyTimes = new List<float>(cachedKeyframeTimes);
                        selectedKeyTimes.Clear();
                        for (int i = 0; i < dragStartKeyTimes.Count; i++)
                        {
                            displayKeyTimes.Remove(dragStartKeyTimes[i]);
                            displayKeyTimes.Add(newTimes[i]);
                            selectedKeyTimes.Add(newTimes[i]);
                        }

                        e.Use();
                        Repaint();
                    }
                    else if (isDraggingKey)
                    {
                        float deltaX = mPos.x - dragStartMouseX;
                        float deltaTime = (deltaX / timelineWidth) * visibleMaxLen;

                        List<float> newTimes = new List<float>();
                        for (int i = 0; i < dragStartKeyTimes.Count; i++)
                        {
                            float origTime = dragStartKeyTimes[i];
                            float newTime = Mathf.Round((origTime + deltaTime) * 60f) / 60f;
                            newTime = Mathf.Clamp(newTime, 0f, visibleMaxLen);
                            newTimes.Add(newTime);
                        }

                        // エラー修正箇所：実データから描画情報を再構築することで安全に更新
                        displayKeyTimes = new List<float>(cachedKeyframeTimes);
                        selectedKeyTimes.Clear();
                        for (int i = 0; i < dragStartKeyTimes.Count; i++)
                        {
                            displayKeyTimes.Remove(dragStartKeyTimes[i]);
                            displayKeyTimes.Add(newTimes[i]);
                            selectedKeyTimes.Add(newTimes[i]);
                        }

                        if (selectedKeyTimes.Count > 0)
                        {
                            selectedRangeStart = selectedKeyTimes.Min();
                            selectedRangeEnd = selectedKeyTimes.Max();
                        }

                        e.Use();
                        Repaint();
                    }
                    else if (isBoxSelecting)
                    {
                        float tStart = GetSnappedTimeAtX(boxSelectStart.x, startX, timelineWidth, visibleMaxLen);
                        float tEnd = GetSnappedTimeAtX(mPos.x, startX, timelineWidth, visibleMaxLen);

                        float xStart = startX + (Mathf.Min(tStart, tEnd) / visibleMaxLen) * timelineWidth;
                        float xEnd = startX + (Mathf.Max(tStart, tEnd) / visibleMaxLen) * timelineWidth;

                        boxSelectRect = new Rect(xStart, keyLaneY, Mathf.Max(1f, xEnd - xStart), 16f);
                        e.Use();
                        Repaint();
                    }
                }
                break;

            case EventType.MouseUp:
                if (e.button == 0)
                {
                    if (isDraggingRange)
                    {
                        isDraggingRange = false;
                        float deltaX = mPos.x - dragStartMouseX;
                        float deltaTime = Mathf.Round((deltaX / timelineWidth) * visibleMaxLen * 60f) / 60f;

                        if (!Mathf.Approximately(deltaTime, 0f))
                        {
                            CommitKeyMove(VRC_ExpressionEditor.Instance, dragStartKeyTimes, deltaTime);
                        }
                        else
                        {
                            displayKeyTimes = displayKeyTimes.Select(t => Mathf.Round(t * 60f) / 60f).Distinct().ToList();
                            selectedKeyTimes = new HashSet<float>(selectedKeyTimes.Select(t => Mathf.Round(t * 60f) / 60f).Distinct());
                            if (selectedKeyTimes.Count > 0)
                            {
                                selectedRangeStart = selectedKeyTimes.Min();
                                selectedRangeEnd = selectedKeyTimes.Max();
                            }
                            else
                            {
                                selectedRangeStart = -1f;
                                selectedRangeEnd = -1f;
                            }
                        }
                        e.Use();
                        Repaint();
                    }
                    else if (isDraggingKey)
                    {
                        isDraggingKey = false;
                        float deltaX = mPos.x - dragStartMouseX;
                        float deltaTime = (deltaX / timelineWidth) * visibleMaxLen;
                        deltaTime = Mathf.Round(deltaTime * 60f) / 60f;

                        if (!Mathf.Approximately(deltaTime, 0f))
                        {
                            CommitKeyMove(VRC_ExpressionEditor.Instance, dragStartKeyTimes, deltaTime);
                        }
                        else
                        {
                            displayKeyTimes = displayKeyTimes.Select(t => Mathf.Round(t * 60f) / 60f).Distinct().ToList();
                            selectedKeyTimes = new HashSet<float>(selectedKeyTimes.Select(t => Mathf.Round(t * 60f) / 60f).Distinct());
                            if (selectedKeyTimes.Count > 0)
                            {
                                selectedRangeStart = selectedKeyTimes.Min();
                                selectedRangeEnd = selectedKeyTimes.Max();
                            }
                        }
                        e.Use();
                        Repaint();
                    }
                    else if (isBoxSelecting)
                    {
                        isBoxSelecting = false;
                        float tStart = GetSnappedTimeAtX(boxSelectStart.x, startX, timelineWidth, visibleMaxLen);
                        float tEnd = GetSnappedTimeAtX(mPos.x, startX, timelineWidth, visibleMaxLen);

                        float selStart = Mathf.Min(tStart, tEnd);
                        float selEnd = Mathf.Max(tStart, tEnd);

                        foreach (float t in displayKeyTimes)
                        {
                            if (t >= selStart && t <= selEnd)
                            {
                                if (!selectedKeyTimes.Contains(t)) selectedKeyTimes.Add(t);
                            }
                        }

                        if (selectedKeyTimes.Count > 0)
                        {
                            selectedRangeStart = selectedKeyTimes.Min();
                            selectedRangeEnd = selectedKeyTimes.Max();
                        }
                        else
                        {
                            selectedRangeStart = -1f;
                            selectedRangeEnd = -1f;
                        }

                        e.Use();
                        Repaint();
                    }
                }
                break;
        }
    }

    private void CommitKeyMove(VRC_ExpressionEditor editor, List<float> targetTimes, float offset)
    {
        if (editor.availableClips.Count <= editor.selectedClipIndex) return;
        AnimationClip clip = editor.availableClips[editor.selectedClipIndex];

        Undo.RecordObject(clip, "キーフレーム移動");

        foreach (var pathGroup in editor.warehouse.Values)
        {
            foreach (var track in pathGroup)
            {
                AnimationCurve curve = track.curve;
                bool changed = false;

                for (int i = curve.keys.Length - 1; i >= 0; i--)
                {
                    float time = curve.keys[i].time;
                    if (targetTimes.Contains(time))
                    {
                        Keyframe kf = curve.keys[i];
                        float newTime = Mathf.Round((time + offset) * 60f) / 60f;
                        newTime = Mathf.Max(0f, newTime);

                        for (int j = curve.keys.Length - 1; j >= 0; j--)
                        {
                            if (Mathf.Approximately(curve.keys[j].time, newTime) && !Mathf.Approximately(time, newTime))
                            {
                                curve.RemoveKey(j);
                            }
                        }

                        curve.RemoveKey(i);
                        kf.time = newTime;
                        curve.AddKey(kf);
                        changed = true;
                    }
                }

                if (changed)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(track.path, track.type, track.propertyName), curve);
                }
            }
        }

        AssetDatabase.SaveAssets();
        editor.RefreshExpressionCache();
        UpdateKeyframeCache(editor);
    }

    private void DrawDiamond(float x, float y, float size, Color color, bool fill)
    {
        float half = size / 2f;
        Vector3[] points = new Vector3[] {
            new Vector3(x, y - half, 0),
            new Vector3(x + half, y, 0),
            new Vector3(x, y + half, 0),
            new Vector3(x - half, y, 0)
        };
        Color oldColor = Handles.color;
        Handles.color = color;
        if (fill) Handles.DrawAAConvexPolygon(points);
        else Handles.DrawAAPolyLine(1.5f, new Vector3[] { points[0], points[1], points[2], points[3], points[0] });
        Handles.color = oldColor;
    }

    private void DrawOutlineRect(Rect rect, Color fill, Color border)
    {
        EditorGUI.DrawRect(rect, fill);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), border);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height - 1, rect.width, 1), border);
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), border);
        EditorGUI.DrawRect(new Rect(rect.x + rect.width - 1, rect.y, 1, rect.height), border);
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
        if (GUILayout.Button("ループ", EditorStyles.toolbarButton, GUILayout.Width(70))) isLoop = !isLoop;
        GUI.backgroundColor = oldBg;

        GUI.backgroundColor = isKeySnap ? new Color(0.6f, 1.0f, 0.6f) : oldBg;
        if (GUILayout.Button("キー吸着", EditorStyles.toolbarButton, GUILayout.Width(70))) isKeySnap = !isKeySnap;
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

            float rawMaxLen = Mathf.Max(animationLength, editor.currentTime);
            float visibleMaxLenBefore = (visibleDuration > rawMaxLen) ? visibleDuration : rawMaxLen * 1.02f;

            float mouseLocalX = e.mousePosition.x;
            float viewWidth = position.width - 24;
            float oldTimelineWidth = viewWidth * Mathf.Max(1.0f, oldZoom);

            float timeAtMouse = ((scrollPos.x + mouseLocalX - 12f) / oldTimelineWidth) * visibleMaxLenBefore;

            if (e.delta.y > 0) visibleDuration = Mathf.Min(visibleDuration * step, 500f);
            else visibleDuration = Mathf.Max(visibleDuration / step, 0.5f);

            float visibleMaxLenAfter = (visibleDuration > rawMaxLen) ? visibleDuration : rawMaxLen * 1.02f;
            zoomLevel = visibleMaxLenAfter / visibleDuration;
            float newTimelineWidth = viewWidth * Mathf.Max(1.0f, zoomLevel);

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

        int step = 10;
        if (pixelsPerFrame < 2.5f) step = 30;
        if (pixelsPerFrame < 0.8f) step = 60;
        if (pixelsPerFrame < 0.4f) step = 120;
        if (pixelsPerFrame < 0.1f) step = 300;

        int startFrame = Mathf.FloorToInt(scrollPos.x / pixelsPerFrame);
        startFrame = Mathf.Max(0, startFrame - 1);

        int endFrame = Mathf.CeilToInt((scrollPos.x + viewWidth) / pixelsPerFrame);
        endFrame = Mathf.Min(totalFrames, endFrame + 1);

        float longLineHeight = areaRect.height - 15f;
        float shortLineHeight = longLineHeight * 0.7f;
        float shortLineY = centerY - (shortLineHeight / 2f);

        for (int i = startFrame; i <= endFrame; i++)
        {
            float x = startX + ((i / 60f) / maxLen) * timelineWidth;

            if (i % step == 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.7f);
                Rect labelRect = new Rect(x - 25, areaRect.y + 2f, 50, 15);
                GUI.Label(labelRect, i.ToString(), rulerLabelStyle);

                GUI.color = new Color(1f, 1f, 1f, 0.40f);
                GUI.DrawTexture(new Rect(x, areaRect.y + 15f, 1, longLineHeight), EditorGUIUtility.whiteTexture);
            }
            else if (pixelsPerFrame >= 3.0f)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.25f);
                GUI.DrawTexture(new Rect(x, shortLineY, 1, shortLineHeight), EditorGUIUtility.whiteTexture);
            }
        }
        GUI.color = Color.white;
    }

    private void InitializeStyles()
    {
        if (rulerLabelStyle == null)
        {
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