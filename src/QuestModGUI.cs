using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;
using Il2Cpp;

namespace MissionImpossible
{
    /// <summary>
    /// Quest GUI integration into the Journal/Collections system.
    /// 
    /// STRATEGY (Temporary): Replace collection list slots 0-2 (Notes, Transmitters, Recipes)
    /// with Daily/Weekly/Monthly quests. This avoids navigation bounds issues entirely — indices
    /// 0-2 are always accessible and their click handlers work out of the box.
    /// 
    /// Once all features (selection, highlighting, detail panel) are verified working,
    /// we can reorder entries properly to append after Surveyed Locations (slots 8-10).
    /// </summary>
    public class QuestModGUI
    {
        private static Panel_Log _panelLog;
        private static bool _hasTracedCollectionsList = false;
        private static readonly string[] QuestTypes = new[] { "Daily", "Weekly", "Monthly" };

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
                    harmony.Patch(
                        refreshMethod,
                        prefix: new HarmonyMethod(typeof(QuestModGUI), nameof(Prefix_Panel_Log_Refresh))
                    );
                    patchedCount++;
                }

                var initMethod = AccessTools.Method(typeof(Panel_Log), "Initialize");
                if (initMethod != null)
                {
                    harmony.Patch(
                        initMethod,
                        postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_Panel_Log_Initialize))
                    );
                    patchedCount++;
                }

                var buildCollectionsMethod = AccessTools.Method(typeof(Panel_Log), "BuildCollectionsList");
                if (buildCollectionsMethod != null)
                {
                    harmony.Patch(
                        buildCollectionsMethod,
                        postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_BuildCollectionsList))
                    );
                    patchedCount++;
                }
                else
                {
                    MelonLogger.Error("[QuestModGUI] Failed to find Panel_Log.BuildCollectionsList() method");
                }

                var updateMethod = AccessTools.Method(typeof(Panel_Log), "Update");
                if (updateMethod != null)
                {
                    harmony.Patch(
                        updateMethod,
                        postfix: new HarmonyMethod(typeof(QuestModGUI), nameof(Postfix_Panel_Log_Update))
                    );
                    patchedCount++;
                }
                else
                {
                    MelonLogger.Error("[QuestModGUI] Failed to find Panel_Log.Update() method");
                }

                MelonLogger.Msg($"[QuestModGUI] GUI system initialized - {patchedCount}/4 patches applied");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error applying Harmony patches: {ex.Message}");
                MelonLogger.Error($"[QuestModGUI] Stack trace: {ex.StackTrace}");
            }
        }

        private static void Prefix_Panel_Log_Refresh(Panel_Log __instance)
        {
            _panelLog = __instance;
        }

        private static void Postfix_Panel_Log_Initialize(Panel_Log __instance)
        {
            _panelLog = __instance;
            MelonLogger.Msg("[QuestModGUI] Panel_Log initialized");
        }

        private static void Postfix_BuildCollectionsList(Panel_Log __instance)
        {
            if (__instance == null)
                return;

            // Skip GUI updates if the quest mod isn't fully initialized yet
            if (QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
                return;

            try
            {
                if (!_hasTracedCollectionsList)
                {
                    TraceCollectionDataList(__instance);
                    _hasTracedCollectionsList = true;
                }

                SyncCollectionDataList(__instance);
                EnsureQuestEntries(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error in Postfix_BuildCollectionsList: {ex.Message}");
                MelonLogger.Error($"[QuestModGUI] Stack trace: {ex.StackTrace}");
            }
        }

        private static void Postfix_Panel_Log_Update(Panel_Log __instance)
        {
            if (__instance == null)
                return;

            // Skip GUI updates if the quest mod isn't fully initialized yet
            if (QuestMod.Instance == null || !QuestMod.Instance._modSettingsAvailable)
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

        /// <summary>
        /// Keeps m_CollectionDataList in sync with enabled quest types at slots 0-2.
        /// </summary>
        private static void SyncCollectionDataList(Panel_Log panelLog)
        {
            var dataList = panelLog.m_CollectionDataList;
            if (dataList == null || QuestMod.Instance == null || dataList.Count < 3)
                return;

            var assignments = new[] {
                ("Daily", 0),
                ("Weekly", 1),
                ("Monthly", 2)
            };

            foreach (var (type, slot) in assignments)
            {
                var summary = QuestMod.Instance.GetQuestSummary(type);

                var dataItem = dataList[slot];
                if (dataItem != null)
                {
                    dataItem.m_NameLocID = $"QUESTMOD_{type}";
                    dataItem.m_DescLocID = $"QUESTMOD_{type}_Desc";
                    
                    // Show "Disabled" if quest type is disabled, otherwise show progress
                    if (!summary.enabled)
                    {
                        dataItem.m_ProgressString = "Disabled";
                    }
                    else
                    {
                        dataItem.m_ProgressString = $"{summary.completed} / {summary.total}";
                    }
                    
                    dataItem.m_ListIconName = "ico_log_Notes";
                    dataItem.m_BigIconName = "Collections_large_notes";
                    dataItem.m_CollectionType = Panel_Log.CollectionsType.General;
                    dataItem.m_SubScreenToOpen = Panel_Log.WhatIKnowType.SelectScreen;
                }
            }
        }

        /// <summary>
        /// Replaces collection list slots 0-2 (Notes, Transmitters, Recipes) with Daily/Weekly/Monthly quests.
        /// This avoids navigation bounds issues — indices 0-2 are always accessible and click handlers work.
        /// </summary>
        private static void EnsureQuestEntries(Panel_Log panelLog)
        {
            if (QuestMod.Instance == null || panelLog.m_CollectionList == null || panelLog.m_CollectionDataList == null)
                return;

            if (panelLog.m_CollectionList.Count < 3 || panelLog.m_CollectionDataList.Count < 3)
                return;

            var assignments = new[] {
                ("Daily", 0),
                ("Weekly", 1),
                ("Monthly", 2)
            };

            foreach (var (type, slot) in assignments)
            {
                var summary = QuestMod.Instance.GetQuestSummary(type);

                var visualItem = panelLog.m_CollectionList[slot];
                var dataItem = panelLog.m_CollectionDataList[slot];

                if (visualItem != null && dataItem != null)
                {
                    string label = $"{type} Quests";
                    string progress = !summary.enabled ? "Disabled" : $"{summary.completed} / {summary.total}";

                    // Update visual row
                    visualItem.SetItemInfo(dataItem);
                    if (visualItem.m_CollectionNameLabel != null)
                        visualItem.m_CollectionNameLabel.text = label;
                    if (visualItem.m_CompletionLabel != null)
                        visualItem.m_CompletionLabel.text = progress;
                }
            }
        }

        /// <summary>
        /// Override the right-side detail panel when a quest entry is selected.
        /// </summary>
        private static void ApplyQuestDetailOverrideIfNeeded(Panel_Log panelLog)
        {
            if (panelLog == null || panelLog.m_CollectionList == null || QuestMod.Instance == null)
                return;

            try
            {
                int idx = panelLog.m_CollectionListSelectedIndex;
                if (idx < 0 || idx >= panelLog.m_CollectionList.Count)
                    return;

                var selectedItem = panelLog.m_CollectionList[idx];
                if (selectedItem == null || selectedItem.m_ItemInfo == null)
                    return;

                string questType = GetQuestTypeFromItemInfo(selectedItem.m_ItemInfo);
                if (questType == null)
                    return; // not one of ours

                string name = $"{questType} Quests";
                
                // Check if this quest type is enabled
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

        /// <summary>
        /// Builds multi-line objective list in the new format:
        /// 1# Collect GEAR_Cloth - 4/4 - Complete
        /// Format: 1# Collect Charcol - 0/2 - Active (or with reward if ShowReward enabled)
        /// Status shows "Complete" or "Active" based on quest status.
        /// </summary>
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
                
                // Strip GEAR_ prefix from item names
                string itemName = quest.CollectKey.StartsWith("GEAR_") 
                    ? quest.CollectKey.Substring(5) 
                    : quest.CollectKey;
                
                string objectiveLine = $"{i + 1}# Collect {itemName} - {quest.CurrentAmount}/{quest.RequiredAmount} - {status}";
                
                // Add reward info if ShowReward is enabled
                if (QuestMod.Instance._settings.ShowReward)
                {
                    string rewardName = quest.RewardKey.StartsWith("GEAR_")
                        ? quest.RewardKey.Substring(5)
                        : quest.RewardKey;
                    objectiveLine += $" - Reward: {quest.RewardAmount} {rewardName}";
                }
                
                lines.Add(objectiveLine);
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Determines if a CollectionListItemInfo is one of our quest entries.
        /// </summary>
        private static string GetQuestTypeFromItemInfo(CollectionListItemInfo itemInfo)
        {
            if (itemInfo == null || itemInfo.m_NameLocID == null)
                return null;

            foreach (var type in QuestTypes)
            {
                if (itemInfo.m_NameLocID == $"QUESTMOD_{type}")
                    return type;
            }

            return null;
        }

        /// <summary>
        /// One-time diagnostic dump of real entries for reference.
        /// </summary>
        private static void TraceCollectionDataList(Panel_Log panelLog)
        {
            try
            {
                var dataList = panelLog.m_CollectionDataList;
                if (dataList == null)
                    return;

                int count = dataList.Count;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModGUI] Error tracing collection data list: {ex.Message}");
            }
        }

        public static Panel_Log GetPanel_Log()
        {
            return _panelLog;
        }
    }
}