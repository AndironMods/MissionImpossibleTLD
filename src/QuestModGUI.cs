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

        public QuestModGUI()
        {
            ApplyHarmonyPatches();
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
                    harmony.Patch(panelLogLateUpdateMethod, postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_Panel_Log_Update)));
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

                MelonLogger.Msg($"[QuestModGUI] GUI system initialized - {patchedCount}/7 patches applied");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error applying Harmony patches: {ex.Message}");
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
        }

        private static void Prefix_Panel_Log_Refresh(Panel_Log __instance)
        {
            _panelLog = __instance;
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

        private static void Postfix_BuildCollectionsList(Panel_Log __instance)
        {
            if (__instance == null || QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                EnsureQuestEntries(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error in Postfix_BuildCollectionsList: {ex.Message}");
            }
        }

        private static void Postfix_Panel_Log_Update(Panel_Log __instance)
        {
            if (__instance == null || QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                bool onCollectionsScreen;
                try
                {
                    onCollectionsScreen = __instance.IsInCollectionsSelectScreen();
                }
                catch
                {
                    onCollectionsScreen = __instance.m_CollectionList != null && __instance.m_CollectionList.Count > 0;
                }

                if (!onCollectionsScreen)
                    return;

                EnsureQuestEntries(__instance);
                ApplyQuestDetailOverrideIfNeeded(__instance);


            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error in Postfix_Panel_Log_Update: {ex.Message}");
            }
        }

        private static void Postfix_ScrollList_Update(ScrollList __instance)
        {

            if (_panelLog == null || __instance == null || QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                ResizeAndRepositionAllEntries(_panelLog);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error in Postfix_ScrollList_Update: {ex.Message}");
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

                    var summary = QuestMod.Instance.GetQuestSummary(type);
                    string label = $"{type} Quests";
                    string progress = !summary.enabled ? "Disabled" : $"{summary.completed} / {summary.total}";

                    if (item.m_CollectionNameLabel != null)
                        item.m_CollectionNameLabel.text = label;
                    if (item.m_CompletionLabel != null)
                        item.m_CompletionLabel.text = progress;
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[QuestModGUI] Error ensuring quest entry for {type}: {ex.Message}");
                }
            }

            try
            {
                ResizeAndRepositionAllEntries(panelLog);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error resizing/repositioning entries: {ex.Message}");
            }
        }

        /// <summary>
        /// Quest rows must be instantiated as children of GRANDPARENT (fixed container),
        /// not pooled slots. This prevents the scroll list from reparenting them.
        /// </summary>
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
                    MelonLogger.Error("[QuestModGUI] Could not find grandparent container");
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
                    MelonLogger.Error($"[QuestModGUI] Instantiate failed for {type}");
                    return null;
                }

                clone.name = $"QuestEntry_{type}";
                clone.transform.SetAsLastSibling();

                var cloneComp = clone.GetComponent<CollectionListItem>();
                if (cloneComp == null)
                {
                    MelonLogger.Error($"[QuestModGUI] Clone missing CollectionListItem component");
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
                    MelonLogger.Error($"[QuestModGUI] Could not wire click listener: {ex.Message}");
                }

                return cloneComp;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error in FindOrCreateQuestEntry: {ex.Message}");
                return null;
            }
        }

        private static bool _offsetsCompressed = false;
        private static Vector2 _originalOffsetOneAway;
        private static Vector2 _originalOffsetOthers;

        private static void ResizeAndRepositionAllEntries(Panel_Log panelLog)
        {
            if (panelLog.m_CollectionList == null || panelLog.m_CollectionList.Count < 1)
                return;

            try
            {
                var scrollList = panelLog.m_CollectionsScrollList;
                if (scrollList == null)
                    return;

                if (!_offsetsCompressed)
                {
                    _originalOffsetOneAway = scrollList.m_OffsetOneAway;
                    _originalOffsetOthers = scrollList.m_OffsetOthers;
                    _offsetsCompressed = true;
                }

                scrollList.m_OffsetOneAway = _originalOffsetOneAway * QUEST_ROW_SCALE;
                scrollList.m_OffsetOthers = _originalOffsetOthers * QUEST_ROW_SCALE;

                try
                {
                    scrollList.CreateOffsetVectors();
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[QuestModGUI] CreateOffsetVectors error: {ex.Message}");
                }

                Vector3? nativeScale = panelLog.m_CollectionList[0]?.transform?.localScale;
                Vector3 anchorPos = new Vector3(0f, FIXED_QUEST_ANCHOR_Y, 0f);

                int questIndex = 0;
                foreach (var type in QuestTypes)
                {
                    if (!_cachedEntries.TryGetValue(type, out var item) || item?.transform == null)
                    {
                        questIndex++;
                        continue;
                    }

                    float offset = FIXED_QUEST_SPACING * (questIndex + 1);
                    item.transform.localPosition = new Vector3(anchorPos.x, anchorPos.y - offset, anchorPos.z);

                    if (nativeScale.HasValue)
                        item.transform.localScale = nativeScale.Value;

                    questIndex++;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error in ResizeAndRepositionAllEntries: {ex.Message}");
            }
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
                MelonLogger.Error($"[QuestModGUI] Error in OnQuestEntryClicked: {ex.Message}");
            }
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

                if (questType == null)
                    return;

                string name = $"{questType} Quests";
                var summary = QuestMod.Instance.GetQuestSummary(questType);
                string desc = !summary.enabled ? "Disabled" : BuildQuestObjectiveText(questType);

                if (panelLog.m_CollectionsNameLabel != null)
                    panelLog.m_CollectionsNameLabel.text = name;
                if (panelLog.m_CollectionsDescLabel != null)
                    panelLog.m_CollectionsDescLabel.text = desc;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error in ApplyQuestDetailOverrideIfNeeded: {ex.Message}");
            }
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
                string itemName = quest.CollectKey.StartsWith("GEAR_") ? quest.CollectKey.Substring(5) : quest.CollectKey;
                
                string objectiveLine = $"{i + 1}# Collect {itemName} - {quest.CurrentAmount}/{quest.RequiredAmount} - {status}";
                
                if (QuestMod.Instance._settings.ShowReward)
                {
                    string rewardName = quest.RewardKey.StartsWith("GEAR_") ? quest.RewardKey.Substring(5) : quest.RewardKey;
                    objectiveLine += $" - Reward: {quest.RewardAmount} {rewardName}";
                }
                
                lines.Add(objectiveLine);
            }

            return string.Join("\n", lines);
        }

        public static Panel_Log GetPanel_Log() => _panelLog;
    }
}