using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class ExpressionSettingEntry
{
    public string fxGuid;
    public string lastClipPath;
    public string lastSmrName;
    public List<bool> lastSelectedLayers = new List<bool>();
    public List<string> manualClipPaths = new List<string>();
    public string lastReferenceClipPath;

    public List<string> favoriteShapes = new List<string>();

    public bool autoLinkShapeKeys = true;
    public bool isMirroringEnabled = false;

    public List<string> filterWords = new List<string>();
    public List<bool> filterActives = new List<bool>();
}

public class ExpressionEditorSettings : ScriptableObject
{
    public List<ExpressionSettingEntry> settings = new List<ExpressionSettingEntry>();
    public string lastIconSavePath = "Assets";

    public bool isFilterWindowOpen = false;
    public float filterPanelWidth = 150f;

    // 🟢 追加：前回選択していたアバターの名前をグローバルに記憶
    public string lastAvatarName = "";

    public ExpressionSettingEntry GetEntry(string guid) => settings.Find(x => x.fxGuid == guid);

    public void SaveEntry(string guid, string clipPath, string smrName, bool[] selectedLayers, List<string> manualClips, string refClipPath, List<string> favShapes, bool autoLink, bool isMirror, List<string> fWords, List<bool> fActives)
    {
        if (string.IsNullOrEmpty(guid)) return;
        var entry = GetEntry(guid);
        if (entry == null) { entry = new ExpressionSettingEntry { fxGuid = guid }; settings.Add(entry); }
        entry.lastClipPath = clipPath;
        entry.lastSmrName = smrName;
        entry.lastSelectedLayers.Clear();
        if (selectedLayers != null) entry.lastSelectedLayers.AddRange(selectedLayers);
        entry.manualClipPaths.Clear();
        if (manualClips != null) entry.manualClipPaths.AddRange(manualClips);
        entry.lastReferenceClipPath = refClipPath;

        entry.favoriteShapes.Clear();
        if (favShapes != null) entry.favoriteShapes.AddRange(favShapes);

        entry.autoLinkShapeKeys = autoLink;
        entry.isMirroringEnabled = isMirror;

        entry.filterWords.Clear();
        if (fWords != null) entry.filterWords.AddRange(fWords);
        entry.filterActives.Clear();
        if (fActives != null) entry.filterActives.AddRange(fActives);
    }
}