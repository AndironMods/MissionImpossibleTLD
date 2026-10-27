using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;
using Il2Cpp;
using Il2CppInterop.Runtime;

namespace MissionImpossible
{
    public class QuestModGUI
    {
        private static Panel_Log _panelLog;
        private static readonly string[] QuestTypes = new[] { "Daily", "Weekly", "Monthly" };

        private const float QUEST_ROW_SCALE = 0.8f;
        private const float FIXED_QUEST_ANCHOR_Y = -347.20f;
        private const float FIXED_QUEST_SPACING = 49.6f;

        private static readonly Dictionary<string, CollectionListItem> _cachedEntries = new Dictionary<string, CollectionListItem>();
        private static Transform _cachedGrandparent = null;

        private static bool _offsetsCompressed = false;
        private static Vector2 _originalOffsetOneAway;
        private static Vector2 _originalOffsetOthers;

        public QuestModGUI()
        {
            ApplyHarmonyPatches();
        }

        private static void SetLabelText(UILabel label, string text)
        {
            if (label != null && label.text != text)
                label.text = text;
        }

        // Journal texts only change with the quest data, the "show reward" setting or the game language
        private static readonly Dictionary<string, (int revision, bool showReward, string language, string text)> _detailTextCache =
            new Dictionary<string, (int, bool, string, string)>();
        private static readonly Dictionary<string, (int revision, string language, string text)> _progressTextCache =
            new Dictionary<string, (int, string, string)>();

        private static string GetCachedDetailText(string questType)
        {
            var mod = QuestMod.Instance;
            int revision = mod.QuestDataRevision;
            bool showReward = mod.GetShowRewardSetting();
            string language = Localization.Language;

            if (_detailTextCache.TryGetValue(questType, out var cached)
                && cached.revision == revision && cached.showReward == showReward && cached.language == language)
                return cached.text;

            var summary = mod.GetQuestSummary(questType);
            string text = !summary.enabled ? "Disabled" : BuildQuestObjectiveText(questType);
            _detailTextCache[questType] = (revision, showReward, language, text);
            return text;
        }

        private static string GetCachedProgressText(string questType)
        {
            var mod = QuestMod.Instance;
            int revision = mod.QuestDataRevision;
            string language = Localization.Language;

            if (_progressTextCache.TryGetValue(questType, out var cached) && cached.revision == revision && cached.language == language)
                return cached.text;

            var summary = mod.GetQuestSummary(questType);
            string text = !summary.enabled ? "Disabled" : $"{summary.completed} / {summary.total}";
            _progressTextCache[questType] = (revision, language, text);
            return text;
        }

        private void ApplyHarmonyPatches()
        {
            try
            {
                var harmony = new HarmonyLib.Harmony("com.missionimpossible.questgui");
                int patchedCount = 0;

                var refreshMethod = AccessTools.Method(typeof(Panel_Log), "Refresh");
                if (refreshMethod != null)
                {
                    harmony.Patch(refreshMethod, prefix: new HarmonyMethod(typeof(QuestModGUI), nameof(Prefix_Panel_Log_Refresh)));
                    patchedCount++;
                }

                var initMethod = AccessTools.Method(typeof(Panel_Log), "Initialize");
                if (initMethod != null)
                {
                    harmony.Patch(initMethod, postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_Panel_Log_Initialize)));
                    patchedCount++;
                }

                var buildCollectionsMethod = AccessTools.Method(typeof(Panel_Log), "BuildCollectionsList");
                if (buildCollectionsMethod != null)
                {
                    harmony.Patch(buildCollectionsMethod, prefix: new HarmonyMethod(typeof(QuestModGUI), nameof(Prefix_BuildCollectionsList)));
                    harmony.Patch(buildCollectionsMethod, postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_BuildCollectionsList)));
                    patchedCount++;
                }

                var updateMethod = AccessTools.Method(typeof(Panel_Log), "Update");
                if (updateMethod != null)
                {
                    harmony.Patch(updateMethod, postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_Panel_Log_Update)));
                    patchedCount++;
                }

                var panelLogLateUpdateMethod = AccessTools.Method(typeof(Panel_Log), "LateUpdate");
                if (panelLogLateUpdateMethod != null)
                {
                    harmony.Patch(panelLogLateUpdateMethod, postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_Panel_Log_LateUpdate)));
                    patchedCount++;
                }

                var scrollListUpdateMethod = AccessTools.Method(typeof(ScrollList), "Update");
                if (scrollListUpdateMethod != null)
                {
                    harmony.Patch(scrollListUpdateMethod, postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_ScrollList_Update)));
                    patchedCount++;
                }

                var refreshPositioningMethod = AccessTools.Method(typeof(ScrollList), "RefreshPositioning");
                if (refreshPositioningMethod != null)
                {
                    harmony.Patch(refreshPositioningMethod, postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_ScrollList_Update)));
                    patchedCount++;
                }

                if (patchedCount < 7)
                    ModLog.Warning($"[QuestModGUI] Only {patchedCount}/7 GUI patches could be applied - the quest entries in the journal may not work");
                else
                    ModLog.Info($"[QuestModGUI] GUI system initialized - {patchedCount}/7 patches applied");
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error applying Harmony patches: {ex.Message}");
            }
        }

        public void OnApplicationQuit()
        {
            CleanupQuestEntries();
        }

        private static void CleanupQuestEntries()
        {
            try
            {
                foreach (var kvp in _cachedEntries)
                {
                    if (kvp.Value?.gameObject != null)
                    {
                        try
                        {
                            UnityEngine.Object.Destroy(kvp.Value.gameObject);
                        }
                        catch { }
                    }
                }
            }
            catch { }

            _cachedEntries.Clear();
            _cachedGrandparent = null;
            _panelLog = null;
            _offsetsCompressed = false;
            _bigIconTransform = null;
            _bigIconSearchFailed = false;
        }

        private static void Prefix_Panel_Log_Refresh(Panel_Log __instance)
        {
            _panelLog = __instance;
        }

        private static bool IsOnCollectionsScreen(Panel_Log panelLog)
        {
            try
            {
                return panelLog.IsInCollectionsSelectScreen();
            }
            catch
            {
                return panelLog.m_CollectionList != null && panelLog.m_CollectionList.Count > 0;
            }
        }

        private static Panel_Log _lastKnownPanelLogInstance = null;

        private static void Postfix_Panel_Log_Initialize(Panel_Log __instance)
        {
            _panelLog = __instance;

            bool isGenuinelyNewInstance = _lastKnownPanelLogInstance != __instance;
            _lastKnownPanelLogInstance = __instance;

            if (isGenuinelyNewInstance)
            {
                CleanupQuestEntries();
            }
        }

        // Compresses native row spacing to QUEST_ROW_SCALE, caching original values once
        private static void CompressScrollListOffsets(ScrollList scrollList)
        {
            if (!_offsetsCompressed)
            {
                _originalOffsetOneAway = scrollList.m_OffsetOneAway;
                _originalOffsetOthers = scrollList.m_OffsetOthers;
                _offsetsCompressed = true;
            }

            Vector2 targetOneAway = _originalOffsetOneAway * QUEST_ROW_SCALE;
            Vector2 targetOthers = _originalOffsetOthers * QUEST_ROW_SCALE;

            if (scrollList.m_OffsetOneAway == targetOneAway && scrollList.m_OffsetOthers == targetOthers)
                return;

            scrollList.m_OffsetOneAway = targetOneAway;
            scrollList.m_OffsetOthers = targetOthers;

            try
            {
                scrollList.CreateOffsetVectors();
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] CreateOffsetVectors error: {ex.Message}");
            }
        }

        // Must run before native BuildCollectionsList, or rows overlap on first build
        private static void Prefix_BuildCollectionsList(Panel_Log __instance)
        {
            if (__instance == null || QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                var scrollList = __instance.m_CollectionsScrollList;
                if (scrollList == null)
                    return;

                CompressScrollListOffsets(scrollList);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in Prefix_BuildCollectionsList: {ex.Message}");
            }
        }

        private static void Postfix_BuildCollectionsList(Panel_Log __instance)
        {
            if (__instance == null || QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                EnsureQuestEntries(__instance);
                ResizeAndRepositionAllEntries(__instance);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in Postfix_BuildCollectionsList: {ex.Message}");
            }
        }

        private static void Postfix_Panel_Log_Update(Panel_Log __instance)
        {
            if (__instance == null || QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                if (!IsOnCollectionsScreen(__instance))
                    return;

                EnsureQuestEntries(__instance);
                ResizeAndRepositionAllEntries(__instance);
                ApplyQuestDetailOverrideIfNeeded(__instance);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in Postfix_Panel_Log_Update: {ex.Message}");
            }
        }

        private static void Postfix_Panel_Log_LateUpdate(Panel_Log __instance)
        {
            if (__instance == null || QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                if (!IsOnCollectionsScreen(__instance))
                    return;

                // Last word of the frame: the game moves the list rows itself when you navigate with the arrow keys
                ResizeAndRepositionAllEntries(__instance);
                ApplyQuestDetailOverrideIfNeeded(__instance);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in Postfix_Panel_Log_LateUpdate: {ex.Message}");
            }
        }

        private static void Postfix_ScrollList_Update(ScrollList __instance)
        {
            if (_panelLog == null || __instance == null || QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                var ownScrollList = _panelLog.m_CollectionsScrollList;
                if (ownScrollList == null || __instance.Pointer != ownScrollList.Pointer)
                    return;

                if (!IsOnCollectionsScreen(_panelLog))
                    return;

                EnsureQuestEntries(_panelLog);
                ResizeAndRepositionAllEntries(_panelLog);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in Postfix_ScrollList_Update: {ex.Message}");
            }
        }

        private static void EnsureQuestEntries(Panel_Log panelLog)
        {
            if (panelLog.m_CollectionList == null || panelLog.m_CollectionList.Count < 1)
                return;

            foreach (var type in QuestTypes)
            {
                try
                {
                    var item = FindOrCreateQuestEntry(panelLog, type);
                    if (item == null)
                        continue;

                    SetLabelText(item.m_CollectionNameLabel, QuestTitle(type));
                    SetLabelText(item.m_CompletionLabel, GetCachedProgressText(type));
                }
                catch (Exception ex)
                {
                    ModLog.Error($"[QuestModGUI] Error ensuring quest entry for {type}: {ex.Message}");
                }
            }
        }

        // Quest rows are children of GRANDPARENT (fixed), not pooled slots, so the scroll list can't reparent them
        private static CollectionListItem FindOrCreateQuestEntry(Panel_Log panelLog, string type)
        {
            try
            {
                if (_cachedEntries.TryGetValue(type, out var cached) && cached != null)
                {
                    bool inVisualList = false;
                    for (int i = 0; i < panelLog.m_CollectionList.Count; i++)
                    {
                        if (panelLog.m_CollectionList[i] == cached)
                        {
                            inVisualList = true;
                            break;
                        }
                    }
                    if (!inVisualList)
                        panelLog.m_CollectionList.Add(cached);

                    if (cached.m_ItemInfo != null)
                    {
                        bool inDataList = false;
                        for (int i = 0; i < panelLog.m_CollectionDataList.Count; i++)
                        {
                            if (panelLog.m_CollectionDataList[i] == cached.m_ItemInfo)
                            {
                                inDataList = true;
                                break;
                            }
                        }
                        if (!inDataList)
                            panelLog.m_CollectionDataList.Add(cached.m_ItemInfo);
                    }

                    return cached;
                }

                for (int i = 0; i < panelLog.m_CollectionList.Count; i++)
                {
                    var existing = panelLog.m_CollectionList[i];
                    if (existing != null && existing.m_ItemInfo != null && existing.m_ItemInfo.m_NameLocID == $"QUESTMOD_{type}")
                    {
                        _cachedEntries[type] = existing;
                        return existing;
                    }
                }

                Transform grandparent = null;
                for (int i = 0; i < panelLog.m_CollectionList.Count; i++)
                {
                    var candidate = panelLog.m_CollectionList[i];
                    if (candidate != null && candidate.transform?.parent?.parent != null)
                    {
                        grandparent = candidate.transform.parent.parent;
                        break;
                    }
                }

                if (grandparent == null)
                {
                    ModLog.Error("[QuestModGUI] Could not find grandparent container");
                    return null;
                }

                if (_cachedGrandparent == null)
                    _cachedGrandparent = grandparent;

                var template = panelLog.m_CollectionList[0];
                if (template?.gameObject == null)
                    return null;

                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, _cachedGrandparent);
                if (clone == null)
                {
                    ModLog.Error($"[QuestModGUI] Instantiate failed for {type}");
                    return null;
                }

                clone.name = $"QuestEntry_{type}";
                clone.transform.SetAsLastSibling();

                // Position immediately - clone starts at (0,0,0) and would flash-overlap the Notes row for a frame
                int questTypeIndex = Array.IndexOf(QuestTypes, type);
                if (questTypeIndex >= 0)
                {
                    float initialOffset = FIXED_QUEST_SPACING * (questTypeIndex + 1);
                    clone.transform.localPosition = new Vector3(0f, FIXED_QUEST_ANCHOR_Y - initialOffset, 0f);
                }

                var cloneComp = clone.GetComponent<CollectionListItem>();
                if (cloneComp == null)
                {
                    ModLog.Error($"[QuestModGUI] Clone missing CollectionListItem component");
                    UnityEngine.Object.Destroy(clone);
                    return null;
                }

                var info = new CollectionListItemInfo
                {
                    m_NameLocID = $"QUESTMOD_{type}",
                    m_DescLocID = $"QUESTMOD_{type}_Desc",
                    m_ListIconName = "ico_log_Notes",
                    m_BigIconName = "Collections_large_notes",
                    m_CollectionType = Panel_Log.CollectionsType.Notes,
                    m_SubScreenToOpen = Panel_Log.WhatIKnowType.SelectScreen
                };

                cloneComp.SetItemInfo(info);

                panelLog.m_CollectionList.Add(cloneComp);
                panelLog.m_CollectionDataList.Add(info);
                _cachedEntries[type] = cloneComp;

                try
                {
                    var listener = UIEventListener.Get(clone);
                    listener.onClick += DelegateSupport.ConvertDelegate<UIEventListener.VoidDelegate>(new Action<GameObject>(OnQuestEntryClicked));
                }
                catch (Exception ex)
                {
                    ModLog.Error($"[QuestModGUI] Could not wire click listener: {ex.Message}");
                }

                return cloneComp;
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in FindOrCreateQuestEntry: {ex.Message}");
                return null;
            }
        }

        private static void ResizeAndRepositionAllEntries(Panel_Log panelLog)
        {
            if (panelLog.m_CollectionList == null || panelLog.m_CollectionList.Count < 1)
                return;

            try
            {
                var scrollList = panelLog.m_CollectionsScrollList;
                if (scrollList == null)
                    return;

                CompressScrollListOffsets(scrollList);

                // With the arrow keys the game scrolls its own rows up by one as soon as the last quest row is
                // selected, which leaves a gap above the (fixed) quest rows. Everything fits on screen, so the
                // list stays unscrolled (with the mouse it never scrolls either).
                if (scrollList.m_SelectedIndex != 0 || scrollList.m_TargetIndex != 0)
                    scrollList.SetTargetIndex(0, false);

                Vector3? nativeScale = panelLog.m_CollectionList[0]?.transform?.localScale;

                // The quest rows continue the list below the game's own rows with the same row distance. Both depend
                // on how many rows the game has (a new collection type adds one), so they are measured from the
                // game's rows; the fixed values are only the fallback while the layout is not ready.
                Vector3 anchorPos = new Vector3(0f, FIXED_QUEST_ANCHOR_Y, 0f);
                float spacing = FIXED_QUEST_SPACING;
                if (_cachedGrandparent != null && TryMeasureNativeRows(panelLog, _cachedGrandparent, out Vector3 lastNativePos, out float nativePitch))
                {
                    anchorPos = lastNativePos;
                    spacing = nativePitch;
                }

                int questIndex = 0;
                foreach (var type in QuestTypes)
                {
                    if (!_cachedEntries.TryGetValue(type, out var item) || item?.transform == null)
                    {
                        questIndex++;
                        continue;
                    }

                    float offset = spacing * (questIndex + 1);
                    Vector3 target = new Vector3(anchorPos.x, anchorPos.y - offset, anchorPos.z);

                    if (item.transform.localPosition != target)
                        item.transform.localPosition = target;

                    if (nativeScale.HasValue && item.transform.localScale != nativeScale.Value)
                        item.transform.localScale = nativeScale.Value;

                    questIndex++;
                }
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in ResizeAndRepositionAllEntries: {ex.Message}");
            }
        }

        private static bool IsQuestEntry(CollectionListItem row)
        {
            foreach (var entry in _cachedEntries.Values)
            {
                if (entry == row)
                    return true;
            }

            return false;
        }

        // Position of the game's last own row and the distance between its last two rows, in the coordinates the
        // quest rows live in. False while there are fewer than two rows or the layout is not set up yet.
        private static bool TryMeasureNativeRows(Panel_Log panelLog, Transform space, out Vector3 lastPos, out float pitch)
        {
            lastPos = default;
            pitch = 0f;

            Transform previous = null;
            Transform last = null;

            var rows = panelLog.m_CollectionList;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null || row.transform == null || IsQuestEntry(row))
                    continue;

                previous = last;
                last = row.transform;
            }

            if (previous == null || last == null)
                return false;

            Vector3 previousPos = space.InverseTransformPoint(previous.position);
            lastPos = space.InverseTransformPoint(last.position);
            pitch = Mathf.Abs(previousPos.y - lastPos.y);

            return pitch >= 1f;
        }

        private static void OnQuestEntryClicked(GameObject clickedGo)
        {
            try
            {
                if (_panelLog?.m_CollectionList == null || clickedGo == null)
                    return;

                var clickedComp = clickedGo.GetComponent<CollectionListItem>();
                if (clickedComp == null)
                    return;

                int foundIndex = -1;
                for (int i = 0; i < _panelLog.m_CollectionList.Count; i++)
                {
                    if (_panelLog.m_CollectionList[i] == clickedComp)
                    {
                        foundIndex = i;
                        break;
                    }
                }

                if (foundIndex < 0)
                    return;

                _panelLog.m_CollectionListSelectedIndex = foundIndex;

                for (int i = 0; i < _panelLog.m_CollectionList.Count; i++)
                {
                    try
                    {
                        _panelLog.m_CollectionList[i]?.SetSelected(i == foundIndex);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in OnQuestEntryClicked: {ex.Message}");
            }
        }

        // Shared "big icon" GameObject in the collection detail panel (right side of the collections screen).
        // Cached once by name lookup since it lives outside the per-row clones we create.
        private static Transform _bigIconTransform;
        private static bool _bigIconSearchFailed;

        private static Transform FindChildByName(Transform root, string name)
        {
            if (root.name == name)
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChildByName(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static void ApplyQuestDetailOverrideIfNeeded(Panel_Log panelLog)
        {
            if (panelLog?.m_CollectionList == null || QuestMod.Instance == null)
                return;

            try
            {
                int idx = panelLog.m_CollectionListSelectedIndex;
                if (idx < 0 || idx >= panelLog.m_CollectionList.Count)
                    return;

                var selectedItem = panelLog.m_CollectionList[idx];
                if (selectedItem == null)
                    return;

                string questType = null;
                foreach (var kvp in _cachedEntries)
                {
                    if (kvp.Value == selectedItem)
                    {
                        questType = kvp.Key;
                        break;
                    }
                }

                // Big icon on the right side: hide only while a Quest entry is selected, restore for
                // every other (non-quest) collections entry so real Notes/collectibles keep their icon (v1.4)
                if (_bigIconTransform == null && !_bigIconSearchFailed)
                {
                    _bigIconTransform = FindChildByName(panelLog.transform, "CollectibleImage_new");
                    if (_bigIconTransform == null)
                        _bigIconSearchFailed = true;
                }

                if (_bigIconTransform != null)
                {
                    bool shouldBeHidden = questType != null;
                    if (_bigIconTransform.gameObject.activeSelf == shouldBeHidden)
                        _bigIconTransform.gameObject.SetActive(!shouldBeHidden);
                }

                if (questType == null)
                    return;

                SetLabelText(panelLog.m_CollectionsNameLabel, QuestTitle(questType));
                SetLabelText(panelLog.m_CollectionsDescLabel, GetCachedDetailText(questType));
            }
            catch (Exception ex)
            {
                ModLog.Error($"[QuestModGUI] Error in ApplyQuestDetailOverrideIfNeeded: {ex.Message}");
            }
        }

        private static string QuestTitle(string questType)
        {
            return questType + " Quests";
        }

        private static string BuildQuestObjectiveText(string questType)
        {
            var quests = QuestMod.Instance.GetActiveQuestsOfType(questType);

            if (quests == null || quests.Count == 0)
                return "No active quests.";

            var lines = new List<string>();
            for (int i = 0; i < quests.Count; i++)
            {
                var quest = quests[i];
                string status = quest.Status == "Complete" ? "Complete" : "Active";
                string itemName = QuestMod.Instance.GetDisplayName(quest.CollectKey);
                
                string objectiveLine = $"{i + 1}# Collect {itemName} - {quest.CurrentAmount}/{quest.RequiredAmount} - {status}";
                
                if (QuestMod.Instance._settings.ShowReward)
                {
                    string rewardName = QuestMod.Instance.GetDisplayName(quest.RewardKey);
                    objectiveLine += $" - Reward: {quest.RewardAmount}x {rewardName}";
                }
                
                lines.Add(objectiveLine);
            }

            return string.Join("\n", lines);
        }

        public static Panel_Log GetPanel_Log() => _panelLog;
    }
}