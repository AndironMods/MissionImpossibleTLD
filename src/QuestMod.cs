using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using MelonLoader;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

[assembly: MelonInfo(typeof(MissionImpossible.QuestMod), "Mission Impossible", "1.0.0", "Andiron")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace MissionImpossible
{
    public class GearEntry
    {
        public string category { get; set; }
        public string spawn_name { get; set; }
        public int required { get; set; }
        public int reward_amount { get; set; }
        public string quest_type { get; set; }
        public bool enabled { get; set; }
    }

    public class GearLookup
    {
        public Dictionary<string, List<GearEntry>> items_to_collect { get; set; }
        public Dictionary<string, List<GearEntry>> items_as_reward { get; set; }
    }

    public class Quest
    {
        public string Type { get; set; } = "Daily";
        public string CollectKey { get; set; }
        public int RequiredAmount { get; set; }
        public int CurrentAmount { get; set; }
        public string RewardKey { get; set; }
        public int RewardAmount { get; set; }
        public int StartDay { get; set; }
        public float StartHour { get; set; }
        public int EndDay { get; set; }  // When the quest period ends
        public float EndHour { get; set; }  // When the quest period ends
        public string Status { get; set; } = "Ongoing";  // Ongoing or Complete
    }

    public class QuestState
    {
        public List<Quest> ActiveQuests { get; set; } = new List<Quest>();
    }

    public class QuestMod : MelonMod
    {
        public static QuestMod Instance { get; private set; }

        private QuestModSettings _settings;
        private GearLookup _lookup;
        private QuestState _questState;
        private System.Random _random = new System.Random();

        private bool _initialized = false;
        private bool _suppressLogging = false;
        private DateTime _lastCheckTime;
        private DateTime _lastQuestCompletionTime = DateTime.MinValue;  // Delay between completions
        private bool _isGivingReward = false;  // Guard against re-entrancy in reward giving
        private bool _modSettingsAvailable = false;

        // Track m_Units changes for stacked items
        private Dictionary<GearItem, int> _lastTrackedUnits = new Dictionary<GearItem, int>();
        private Dictionary<GearItem, DateTime> _lastCallbackTime = new Dictionary<GearItem, DateTime>();  // Track when callback fired
        private DateTime _lastUnitsCheckTime = DateTime.Now;
        private bool _needsSave = false;  // Flag to defer saves from callbacks

        private TimeOfDay TOD => GameManager.GetTimeOfDayComponent();
        private float CurrentHour => TOD.GetHoursPlayedNotPaused() % 24f;
        private bool IsNineAM() => CurrentHour >= 9f;

        private string GearLookupPath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Mods", "GearLookup.json");

        private string QuestDataPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TheLongDark", "Mods", "MissionImpossible", "QuestData.json");

        public override void OnInitializeMelon()
        {
            Instance = this;
            MelonLogger.Msg("[QuestMod] Initializing Mission Impossible...");

            // Check for ModSettings prerequisite
            if (!CheckModSettingsPrerequisite())
            {
                MelonLogger.Error("[QuestMod] FATAL: ModSettings.dll is required but not found!");
                return;
            }

            _modSettingsAvailable = true;

            // Initialize settings
            try
            {
                _settings = new QuestModSettings();
                _settings.InitializeSettings();
                MelonLogger.Msg("[QuestMod] Settings registered successfully!");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error registering settings: {ex.Message}");
                return;
            }

            // Load data
            LoadLookup();
            LoadData();
            InventoryPatches.ApplyAll(HarmonyInstance);

            if (_questState == null)
            {
                _questState = new QuestState();
            }

            // Only regenerate if NO quests exist yet (first run)
            if (_questState.ActiveQuests.Count == 0)
            {
                MelonLogger.Msg("[QuestMod] No quests found - generating initial quests...");
                RegenerateQuestsForSettingsChange();
            }
            else
            {
                MelonLogger.Msg($"[QuestMod] Loaded {_questState.ActiveQuests.Count} existing quests from disk");
            }

            SaveData();
            MelonLogger.Msg("[QuestMod] Initialization complete!");
            MelonLogger.Msg($"[QuestMod] Difficulty: {_settings.GetDifficultyDescription()}");
        }

        private bool CheckModSettingsPrerequisite()
        {
            try
            {
                var modSettingsType = System.Type.GetType("ModSettings.ModSettingsBase, ModSettings");
                if (modSettingsType != null)
                {
                    MelonLogger.Msg("[QuestMod] ModSettings.dll prerequisite check: PASSED");
                    return true;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QuestMod] ModSettings prerequisite check error: {ex.Message}");
            }

            return false;
        }

        public override void OnApplicationQuit()
        {
            try
            {
                MelonLogger.Msg("[QuestMod] Shutting down - saving data...");

                if (_questState != null && _lookup != null)
                {
                    SaveData();
                    MelonLogger.Msg("[QuestMod] Quest data saved on exit.");
                }
                else
                {
                    MelonLogger.Warning("[QuestMod] Quest data or lookup is null, skipping save on exit.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error during shutdown: {ex.Message}");
            }

            Instance = null;
            MelonLogger.Msg("[QuestMod] Shutdown complete.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"[QuestMod] Scene loaded: {sceneName} (index: {buildIndex})");

            _initialized = false;
            _suppressLogging = true;
            _lastCheckTime = DateTime.Now;

            System.Threading.Thread.Sleep(200);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"[QuestMod] Scene unloaded: {sceneName}");

            try
            {
                if (_questState != null)
                {
                    SaveData();
                    MelonLogger.Msg("[QuestMod] Data saved before scene unload.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error saving data on scene unload: {ex.Message}");
            }
        }

        private Dictionary<string, int> _lastKnownInventory = new Dictionary<string, int>();

        public override void OnUpdate()
        {
            // Re-enable logging after initial inventory load
            if (_suppressLogging && (DateTime.Now - _lastCheckTime).TotalSeconds > 1)
            {
                _suppressLogging = false;
                MelonLogger.Msg("[QuestMod] Inventory loaded. Quest tracking enabled.");
            }

            if (!_initialized && _modSettingsAvailable)
            {
                _initialized = true;
                CheckQuestResets();
            }

            // F2: Complete daily quests
            if (Input.GetKeyDown(KeyCode.F2) && _modSettingsAvailable)
            {
                var dailyQuests = _questState.ActiveQuests.Where(q => q.Type == "Daily").ToList();
                if (dailyQuests.Count > 0)
                {
                    MelonLogger.Msg($"[QuestMod] All daily quests completed!");
                    
                    int questNumber = 1;
                    foreach (var quest in dailyQuests)
                    {
                        quest.CurrentAmount = quest.RequiredAmount;
                        quest.Status = "Complete";
                        CheckAndCompleteQuest(quest, isDebugCommand: true, questNumber: questNumber);
                        questNumber++;
                    }
                }
            }

            // F3: Complete weekly quests
            if (Input.GetKeyDown(KeyCode.F3) && _modSettingsAvailable)
            {
                var weeklyQuests = _questState.ActiveQuests.Where(q => q.Type == "Weekly").ToList();
                if (weeklyQuests.Count > 0)
                {
                    MelonLogger.Msg($"[QuestMod] All weekly quests completed!");
                    
                    int questNumber = 1;
                    foreach (var quest in weeklyQuests)
                    {
                        quest.CurrentAmount = quest.RequiredAmount;
                        quest.Status = "Complete";
                        CheckAndCompleteQuest(quest, isDebugCommand: true, questNumber: questNumber);
                        questNumber++;
                    }
                }
            }

            // F4: Complete monthly quests
            if (Input.GetKeyDown(KeyCode.F4) && _modSettingsAvailable)
            {
                var monthlyQuests = _questState.ActiveQuests.Where(q => q.Type == "Monthly").ToList();
                if (monthlyQuests.Count > 0)
                {
                    MelonLogger.Msg($"[QuestMod] All monthly quests completed!");
                    
                    int questNumber = 1;
                    foreach (var quest in monthlyQuests)
                    {
                        quest.CurrentAmount = quest.RequiredAmount;
                        quest.Status = "Complete";
                        CheckAndCompleteQuest(quest, isDebugCommand: true, questNumber: questNumber);
                        questNumber++;
                    }
                }
            }

            // Check for stacked items every 2 seconds (for items that don't trigger callback)
            // TEMPORARILY DISABLED - causes inventory hang
            // if ((DateTime.Now - _lastUnitsCheckTime).TotalSeconds > 2 && _modSettingsAvailable)
            // {
            //     CheckForStackedItems();
            //     _lastUnitsCheckTime = DateTime.Now;
            // }

            // Check for quest resets every 60 seconds
            if ((DateTime.Now - _lastCheckTime).TotalSeconds > 60 && _modSettingsAvailable)
            {
                CheckQuestResets();
                _lastCheckTime = DateTime.Now;
            }
            
            // Save deferred changes (from callbacks)
            if (_needsSave)
            {
                SaveData();
                _needsSave = false;
            }
        }

        private void CheckForStackedItems()
        {
            try
            {
                // Create a copy of keys to avoid modification during iteration
                var keysToCheck = _lastTrackedUnits.Keys.ToList();

                foreach (var gearItem in keysToCheck)
                {
                    if (gearItem == null)
                    {
                        if (_lastTrackedUnits.ContainsKey(gearItem))
                            _lastTrackedUnits.Remove(gearItem);
                        continue;
                    }

                    try
                    {
                        if (!_lastTrackedUnits.ContainsKey(gearItem))
                            continue;

                        // Skip if this item was just processed by AddGear callback (within 0.5 seconds)
                        if (_lastCallbackTime.ContainsKey(gearItem))
                        {
                            if ((DateTime.Now - _lastCallbackTime[gearItem]).TotalSeconds < 0.5)
                                continue;  // Skip to avoid double-counting
                        }

                        int lastUnits = _lastTrackedUnits[gearItem];
                        
                        // Get current units from stackable item
                        int currentUnits = 1;
                        if (gearItem.m_StackableItem != null)
                        {
                            currentUnits = gearItem.m_StackableItem.m_Units;
                        }

                        // If units increased, fire callback for the difference
                        if (currentUnits > lastUnits)
                        {
                            int added = currentUnits - lastUnits;

                            // Fire ONE callback per unit added (quantity will be 1 each time)
                            // This ensures each item is counted once, not as total stack
                            for (int i = 0; i < added; i++)
                            {
                                OnInventoryItemAdded(gearItem, isBulkStack: true);
                            }

                            // Update tracked units
                            if (_lastTrackedUnits.ContainsKey(gearItem))
                                _lastTrackedUnits[gearItem] = currentUnits;
                        }
                        // Clean up old callback time tracking to prevent memory leak
                        var oldCallbacks = _lastCallbackTime.Where(kvp => (DateTime.Now - kvp.Value).TotalSeconds > 5).ToList();
                        foreach (var item in oldCallbacks)
                        {
                            _lastCallbackTime.Remove(item.Key);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void CheckQuestResets()
        {
            try
            {
                if (_lookup == null || _questState == null || _settings == null)
                    return;

                TimeOfDay tod = TOD;
                if (tod == null)
                    return;

                int day = tod.GetDayNumber();
                float hour = CurrentHour;

                foreach (var quest in _questState.ActiveQuests.ToList())
                {
                    // Check if quest objective is complete and period has ended - give reward
                    if (quest.CurrentAmount >= quest.RequiredAmount)
                    {
                        bool periodEnded = (day > quest.EndDay) || 
                                           (day == quest.EndDay && hour >= quest.EndHour);
                        
                        if (periodEnded)
                        {
                            // Period ended, complete the quest
                            MelonLogger.Msg($"[QuestMod] Period ended for {quest.Type} quest - giving reward");
                            GiveReward(quest);
                            
                            _questState.ActiveQuests.Remove(quest);
                            MelonLogger.Msg($"[QuestMod] Quest removed - {_questState.ActiveQuests.Count} remaining");
                            
                            // Generate replacement
                            int targetCount = quest.Type switch
                            {
                                "Daily" => _settings.EnableDailyQuests ? _settings.DailyQuestCount : 0,
                                "Weekly" => _settings.EnableWeeklyQuests ? _settings.WeeklyQuestCount : 0,
                                "Monthly" => _settings.EnableMonthlyQuests ? _settings.MonthlyQuestCount : 0,
                                _ => 0
                            };
                            
                            int currentCount = _questState.ActiveQuests.Count(q => q.Type == quest.Type);
                            for (int i = currentCount; i < targetCount; i++)
                            {
                                GenerateQuestOfType(quest.Type);
                            }
                            
                            SaveData();
                            continue;
                        }
                    }
                    
                    bool shouldReset = false;

                    if (quest.Type == "Daily" && day > quest.StartDay && hour >= 9f)
                    {
                        shouldReset = true;
                    }
                    else if (quest.Type == "Weekly" && (day - quest.StartDay) >= 7)
                    {
                        shouldReset = true;
                    }
                    else if (quest.Type == "Monthly" && (day - quest.StartDay) >= 30)
                    {
                        shouldReset = true;
                    }

                    if (shouldReset)
                    {
                        if (_settings.EnableDetailedLogging)
                        {
                            MelonLogger.Msg($"[QuestMod] ========== QUEST ENDING ==========");
                            MelonLogger.Msg($"[QuestMod] Type: {quest.Type}");
                            MelonLogger.Msg($"[QuestMod] Objective: {quest.CollectKey}");
                            MelonLogger.Msg($"[QuestMod] Final Progress: {quest.CurrentAmount}/{quest.RequiredAmount}");
                            
                            if (quest.CurrentAmount >= quest.RequiredAmount)
                            {
                                MelonLogger.Msg($"[QuestMod] Status: COMPLETED - Would give reward!");
                            }
                            else
                            {
                                MelonLogger.Msg($"[QuestMod] Status: NOT COMPLETED - Reward not given");
                            }
                            MelonLogger.Msg($"[QuestMod] ===================================");
                        }

                        _questState.ActiveQuests.Remove(quest);
                        GenerateQuestType(quest.Type);
                    }
                }

                SaveData();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error in CheckQuestResets: {ex.Message}");
            }
        }

        private void GenerateQuestType(string questType)
        {
            int count = questType switch
            {
                "Daily" => _settings.DailyQuestCount,
                "Weekly" => _settings.WeeklyQuestCount,
                "Monthly" => _settings.MonthlyQuestCount,
                _ => 0
            };

            for (int i = 0; i < count; i++)
            {
                GenerateQuestOfType(questType);
            }

            SaveData();
        }


        private void GenerateQuestOfType(string questType)
        {
            if (_lookup?.items_to_collect == null)
            {
                MelonLogger.Error($"[QuestMod] Cannot generate {questType} quest: items_to_collect is null!");
                return;
            }

            List<string> allowedCategories = _settings.GetAllowedCategories();
            var validItems = new List<KeyValuePair<string, List<GearEntry>>>();

            foreach (var kvp in _lookup.items_to_collect)
            {
                var variant = kvp.Value.FirstOrDefault(v => v.quest_type.Equals(questType, System.StringComparison.OrdinalIgnoreCase) && v.enabled && allowedCategories.Contains(v.category));
                if (variant != null)
                {
                    validItems.Add(kvp);
                }
            }

            if (validItems.Count == 0)
            {
                MelonLogger.Error($"[QuestMod] No valid {questType} collection items found! Allowed categories: {string.Join(", ", allowedCategories)}");
                return;
            }

            var collectItem = validItems[_random.Next(validItems.Count)];
            var collectVariant = collectItem.Value.First(v => v.quest_type.Equals(questType, System.StringComparison.OrdinalIgnoreCase) && v.enabled);

            var rewardItems = _lookup.items_as_reward
                .Where(kvp => kvp.Value.Any(v => v.quest_type.Equals(questType, System.StringComparison.OrdinalIgnoreCase) && v.enabled && allowedCategories.Contains(v.category)))
                .ToList();

            if (rewardItems.Count == 0)
            {
                MelonLogger.Error($"[QuestMod] No valid {questType} reward items found!");
                return;
            }

            var rewardItem = rewardItems[_random.Next(rewardItems.Count)];
            var rewardVariant = rewardItem.Value.First(v => v.quest_type.Equals(questType, System.StringComparison.OrdinalIgnoreCase) && v.enabled);

            // Apply difficulty multipliers
            int requiredAmount = _settings.ApplyRequiredMultiplier(collectVariant.required);
            int rewardAmount = _settings.ApplyRewardMultiplier(rewardVariant.reward_amount);

            int currentDay = GetCurrentDay();
            float currentHour = GetCurrentHour();
            
            // Calculate quest period end time
            // Quests always start at 9:00 AM and end at 8:50 AM (next period)
            const float QUEST_START_HOUR = 9.0f;      // 9:00 AM
            const float QUEST_END_HOUR = 8.833333f;   // 8:50 AM (8 + 50/60)
            
            int startDay = currentDay;
            float startHour = QUEST_START_HOUR;
            
            int endDay = currentDay;
            float endHour = QUEST_END_HOUR;
            
            if (questType.Equals("Daily", System.StringComparison.OrdinalIgnoreCase))
            {
                endDay = currentDay + 1;  // Ends tomorrow at 8:50 AM
            }
            else if (questType.Equals("Weekly", System.StringComparison.OrdinalIgnoreCase))
            {
                endDay = currentDay + 7;  // Ends in 7 days at 8:50 AM
            }
            else if (questType.Equals("Monthly", System.StringComparison.OrdinalIgnoreCase))
            {
                endDay = currentDay + 30;  // Ends in 30 days at 8:50 AM
            }

            var quest = new Quest
            {
                Type = questType,
                CollectKey = collectVariant.spawn_name,  // Use spawn_name, not dictionary key!
                RequiredAmount = requiredAmount,
                CurrentAmount = 0,
                RewardKey = rewardVariant.spawn_name,   // Use spawn_name, not dictionary key!
                RewardAmount = rewardAmount,
                StartDay = startDay,
                StartHour = startHour,
                EndDay = endDay,
                EndHour = endHour
            };

            _questState.ActiveQuests.Add(quest);

            if (_settings.EnableDetailedLogging)
            {
                MelonLogger.Msg($"[QuestMod] ========== QUEST CREATED ==========");
                MelonLogger.Msg($"[QuestMod] Type: {questType}");
                MelonLogger.Msg($"[QuestMod] Difficulty: {_settings.GetDifficultyDescription()}");
                MelonLogger.Msg($"[QuestMod] Objective: Collect {requiredAmount}x {collectItem.Key} ({collectVariant.spawn_name})");
                MelonLogger.Msg($"[QuestMod] New items needed: {requiredAmount}");
                MelonLogger.Msg($"[QuestMod] Category: {collectVariant.category}");
                MelonLogger.Msg($"[QuestMod] Progress: 0/{requiredAmount}");
                
                string rewardDisplay = _settings.HideReward ? "***" : $"{rewardAmount}x {rewardItem.Key} ({rewardVariant.spawn_name})";
                MelonLogger.Msg($"[QuestMod] Reward: {rewardDisplay}");
                
                MelonLogger.Msg($"[QuestMod] ===================================");
            }
        }

        private void CheckAndCompleteQuest(Quest quest, bool isDebugCommand = false, int questNumber = 0)
        {
            if (quest.CurrentAmount >= quest.RequiredAmount)
            {
                // Prevent multiple quests completing in same frame - 0.5 second delay
                // BUT: Skip this check for debug commands (F2/F3/F4)
                if (!isDebugCommand && (DateTime.Now - _lastQuestCompletionTime).TotalSeconds < 0.5)
                {
                    return;  // Skip, process next frame
                }
                
                _lastQuestCompletionTime = DateTime.Now;
                
                // Only show detailed logging for natural completions (not debug commands)
                if (!isDebugCommand)
                {
                    MelonLogger.Msg($"[QuestMod] ========== COMPLETING QUEST ==========");
                    MelonLogger.Msg($"[QuestMod] Type: {quest.Type}");
                    MelonLogger.Msg($"[QuestMod] Collected: {quest.CurrentAmount}/{quest.RequiredAmount}");
                }
                
                GiveReward(quest, questNumber);
                
                // Find and remove the quest
                var questToRemove = _questState.ActiveQuests.FirstOrDefault(q => 
                    q.Type == quest.Type && 
                    q.CollectKey == quest.CollectKey &&
                    q.RequiredAmount == quest.RequiredAmount
                );
                
                if (questToRemove != null)
                {
                    _questState.ActiveQuests.Remove(questToRemove);
                    MelonLogger.Msg($"[QuestMod] Quest removed from active list");
                    MelonLogger.Msg($"[QuestMod] Active quests remaining: {_questState.ActiveQuests.Count}");
                    
                    // Generate replacement quest
                    int targetCount = quest.Type switch
                    {
                        "Daily" => _settings.EnableDailyQuests ? _settings.DailyQuestCount : 0,
                        "Weekly" => _settings.EnableWeeklyQuests ? _settings.WeeklyQuestCount : 0,
                        "Monthly" => _settings.EnableMonthlyQuests ? _settings.MonthlyQuestCount : 0,
                        _ => 0
                    };
                    
                    int currentCount = _questState.ActiveQuests.Count(q => q.Type == quest.Type);
                    int neededCount = targetCount - currentCount;
                    
                    if (neededCount > 0)
                    {
                        MelonLogger.Msg($"[QuestMod] {quest.Type}: {currentCount} active, {targetCount} target, generating {neededCount} quest(s)...");
                        
                        for (int i = 0; i < neededCount; i++)
                        {
                            GenerateQuestOfType(quest.Type);
                        }
                        
                        MelonLogger.Msg($"[QuestMod] {quest.Type} quest(s) generated successfully");
                    }
                    
                    // Final save to ensure all changes are persisted
                    SaveData();
                }
                else
                {
                    MelonLogger.Error($"[QuestMod] Failed to find quest to remove!");
                }
                
                SaveData();
                MelonLogger.Msg($"[QuestMod] =====================================");
            }
        }

        private void GiveReward(Quest quest, int questNumber = 0)
        {
            try
            {
                _isGivingReward = true;  // Set guard FIRST
                
                var inventory = GameManager.GetInventoryComponent();
                if (inventory == null)
                {
                    MelonLogger.Error("[QuestMod] Inventory component is null!");
                    return;
                }

                int successCount = 0;
                for (int i = 0; i < quest.RewardAmount; i++)
                {
                    GearItem rewardItem = GearItem.InstantiateGearItem(quest.RewardKey);
                    if (rewardItem != null)
                    {
                        inventory.AddGear(rewardItem);
                        successCount++;
                    }
                    else
                    {
                        MelonLogger.Error($"[QuestMod] Failed to instantiate reward item: {quest.RewardKey}");
                    }
                }

                string rewardDisplay = _settings.HideReward ? "***" : $"{successCount} {quest.RewardKey}";
                
                if (questNumber > 0)
                {
                    MelonLogger.Msg($"[QuestMod] #{questNumber} Quest Completed - {quest.Type} Quest: Reward: {rewardDisplay}");
                }
                else
                {
                    MelonLogger.Msg($"[QuestMod] Quest Completed - {quest.Type} Quest: Reward: {rewardDisplay}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error giving reward: {ex.Message}");
                MelonLogger.Error($"[QuestMod] Stack trace: {ex.StackTrace}");
            }
            finally
            {
                _isGivingReward = false;  // Clear guard in finally block
            }
        }

        private int GetCurrentDay()
        {
            try
            {
                return TOD?.GetDayNumber() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private float GetCurrentHour()
        {
            try
            {
                return CurrentHour;
            }
            catch
            {
                return 0f;
            }
        }

        public void RegenerateQuestsForSettingsChange()
        {
            MelonLogger.Msg("[QuestMod] Clearing active quests...");
            _questState.ActiveQuests.Clear();

            MelonLogger.Msg($"[QuestMod] Lookup status: {(_lookup != null ? "LOADED" : "NULL - Cannot generate quests!")}");
            MelonLogger.Msg($"[QuestMod] Settings status: {(_settings != null ? "LOADED" : "NULL")}");
            MelonLogger.Msg($"[QuestMod] QuestState status: {(_questState != null ? "LOADED" : "NULL")}");

            if (_lookup == null)
            {
                MelonLogger.Error("[QuestMod] CRITICAL: GearLookup is null! Quests cannot be generated. Fix GearLookup.json JSON errors.");
                SaveData();
                return;
            }

            int totalQuests = 0;

            if (_settings.EnableDailyQuests)
            {
                MelonLogger.Msg($"[QuestMod] Generating {_settings.DailyQuestCount} Daily quests...");
                GenerateQuestType("Daily");
                totalQuests += _settings.DailyQuestCount;
            }
            else
            {
                MelonLogger.Msg("[QuestMod] Daily quests DISABLED");
            }

            if (_settings.EnableWeeklyQuests)
            {
                MelonLogger.Msg($"[QuestMod] Generating {_settings.WeeklyQuestCount} Weekly quests...");
                GenerateQuestType("Weekly");
                totalQuests += _settings.WeeklyQuestCount;
            }
            else
            {
                MelonLogger.Msg("[QuestMod] Weekly quests DISABLED");
            }

            if (_settings.EnableMonthlyQuests)
            {
                MelonLogger.Msg($"[QuestMod] Generating {_settings.MonthlyQuestCount} Monthly quests...");
                GenerateQuestType("Monthly");
                totalQuests += _settings.MonthlyQuestCount;
            }
            else
            {
                MelonLogger.Msg("[QuestMod] Monthly quests DISABLED");
            }

            MelonLogger.Msg($"[QuestMod] Total quests generated: {_questState.ActiveQuests.Count}/{totalQuests}");
            SaveData();
        }

        private void LoadLookup()
        {
            if (!File.Exists(GearLookupPath))
            {
                MelonLogger.Error($"[QuestMod] GearLookup.json not found at {GearLookupPath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(GearLookupPath);
                JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                _lookup = new GearLookup
                {
                    items_to_collect = new Dictionary<string, List<GearEntry>>(),
                    items_as_reward = new Dictionary<string, List<GearEntry>>()
                };

                // Parse items_to_collect (only enabled items)
                if (root.TryGetProperty("items_to_collect", out JsonElement collectSection))
                {
                    foreach (JsonProperty item in collectSection.EnumerateObject())
                    {
                        var variants = new List<GearEntry>();
                        foreach (JsonElement variant in item.Value.EnumerateArray())
                        {
                            var entry = new GearEntry
                            {
                                category = variant.GetProperty("category").GetString(),
                                spawn_name = variant.GetProperty("spawn_name").GetString(),
                                required = variant.GetProperty("required").GetInt32(),
                                quest_type = variant.GetProperty("quest_type").GetString(),
                                enabled = variant.GetProperty("enabled").GetBoolean()
                            };

                            if (entry.enabled)
                                variants.Add(entry);
                        }

                        if (variants.Count > 0)
                            _lookup.items_to_collect[item.Name] = variants;
                    }
                }

                // Parse items_as_reward (only enabled items)
                if (root.TryGetProperty("items_as_reward", out JsonElement rewardSection))
                {
                    foreach (JsonProperty item in rewardSection.EnumerateObject())
                    {
                        var variants = new List<GearEntry>();
                        foreach (JsonElement variant in item.Value.EnumerateArray())
                        {
                            var entry = new GearEntry
                            {
                                category = variant.GetProperty("category").GetString(),
                                spawn_name = variant.GetProperty("spawn_name").GetString(),
                                reward_amount = variant.GetProperty("reward_amount").GetInt32(),
                                quest_type = variant.GetProperty("quest_type").GetString(),
                                enabled = variant.GetProperty("enabled").GetBoolean()
                            };

                            if (entry.enabled)
                                variants.Add(entry);
                        }

                        if (variants.Count > 0)
                            _lookup.items_as_reward[item.Name] = variants;
                    }
                }

                MelonLogger.Msg($"[QuestMod] Loaded {_lookup.items_to_collect.Count} collect items and {_lookup.items_as_reward.Count} reward items");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error loading GearLookup: {ex.Message}");
            }
        }

        private void LoadData()
        {
            if (!File.Exists(QuestDataPath))
            {
                _questState = new QuestState();
                SaveData();
                return;
            }

            try
            {
                string json = File.ReadAllText(QuestDataPath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                _questState = JsonSerializer.Deserialize<QuestState>(json, options);
                
                // Recalculate Status for all quests based on CurrentAmount
                // This handles old JSON files that don't have Status field
                foreach (var quest in _questState.ActiveQuests)
                {
                    quest.Status = quest.CurrentAmount >= quest.RequiredAmount ? "Complete" : "Ongoing";
                }
                
                MelonLogger.Msg("[QuestMod] Quest data loaded from disk");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error loading quest data: {ex.Message}");
                _questState = new QuestState();
            }
        }

        private void SaveData()
        {
            // NOTE: ModSettings UI is read-only and doesn't auto-refresh when quest data changes.
            // The actual quest data in QuestData.json IS updated correctly and used by the game.
            // To see updated quest counts in ModSettings, close and re-open the settings panel.
            try
            {
                string dirPath = Path.GetDirectoryName(QuestDataPath);
                if (!Directory.Exists(dirPath))
                    Directory.CreateDirectory(dirPath);

                if (_settings.EnableDetailedLogging)
                {
                    MelonLogger.Msg($"[QuestMod] SaveData: Saving {_questState.ActiveQuests.Count} quests");
                    foreach (var q in _questState.ActiveQuests)
                    {
                        MelonLogger.Msg($"[QuestMod]   - {q.Type} Quest: {q.CollectKey} = {q.CurrentAmount}/{q.RequiredAmount} - {q.Status}");
                    }
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_questState, options);
                File.WriteAllText(QuestDataPath, json);
                
                MelonLogger.Msg($"[QuestMod] Updating QuestData.json");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error saving quest data: {ex.Message}");
            }
        }

        public static void OnInventoryItemAdded(GearItem gearItem, bool isBulkStack = false)
        {
            if (Instance == null || gearItem == null)
                return;

            // Skip if we're currently giving a reward (prevent re-entrancy hang)
            if (Instance._isGivingReward)
                return;

            if (Instance._questState?.ActiveQuests == null || Instance._questState.ActiveQuests.Count == 0)
                return;

            // Track this GearItem's units for stacking detection
            if (!Instance._lastTrackedUnits.ContainsKey(gearItem))
            {
                try
                {
                    int currentUnits = gearItem.m_StackableItem?.m_Units ?? 1;
                    Instance._lastTrackedUnits[gearItem] = currentUnits;
                }
                catch { }
            }

            string prefabName = gearItem.name.ToLower();

            // Suppress logging during initial inventory load
            if (Instance._suppressLogging)
                return;

            if (Instance._settings != null && Instance._settings.SuppressPickupLogging)
                return;

            if (Instance._settings.EnableDetailedLogging)
                MelonLogger.Msg($"[QuestMod] Item added to inventory: '{gearItem.name}'");

            string stackIndicator = isBulkStack ? "[STACKED] " : "";

            // Check all active quests
            bool matchFound = false;
            foreach (var quest in Instance._questState.ActiveQuests.ToList())
            {
                if (quest.CollectKey.Equals(gearItem.name, System.StringComparison.OrdinalIgnoreCase))
                {
                    // Get quantity - handle stackable items
                    // IMPORTANT: AddGear is called ONCE per pickup, so it's always +1
                    // Don't read m_StackableItem.m_Units (that's the total stack size, not what was added)
                    int quantity = 1;  // Always 1 per pickup event
                    
                    quest.CurrentAmount += quantity;
                    
                    // Update status when quest becomes complete
                    if (quest.CurrentAmount >= quest.RequiredAmount)
                    {
                        quest.Status = "Complete";
                    }
                    
                    matchFound = true;
                    
                    // Record when this item was last processed by AddGear callback
                    if (!Instance._lastCallbackTime.ContainsKey(gearItem))
                        Instance._lastCallbackTime[gearItem] = DateTime.Now;
                    else
                        Instance._lastCallbackTime[gearItem] = DateTime.Now;
                    
                    MelonLogger.Msg($"[QuestMod] Progress: {quest.Type} quest - {quest.CurrentAmount}/{quest.RequiredAmount} (added {quantity})");
                    
                    // DON'T call SaveData here - defer it to avoid callback recursion
                    // SaveData will be called by CheckQuestResets every 60 seconds
                    Instance._needsSave = true;
                    if (quest.CurrentAmount >= quest.RequiredAmount && Instance._settings.EnableDetailedLogging)
                    {
                        MelonLogger.Msg($"[QuestMod] {quest.Type} quest objective complete! Period must end before reward is given.");
                    }
                }
            }
        }
    }

    public static class InventoryPatches
    {
        public static void ApplyAll(HarmonyLib.Harmony harmony)
        {
            PatchInventoryAddItem(harmony);
        }

        private static void PatchInventoryAddItem(HarmonyLib.Harmony harmony)
        {
            var addGear = typeof(Inventory).GetMethod("AddGear");

            if (addGear != null)
            {
                harmony.Patch(addGear,
                    postfix: new HarmonyMethod(typeof(InventoryPatches), nameof(AddGear_Postfix)));
                MelonLogger.Msg($"[QuestMod] Patched Inventory.AddGear");
            }
            else
            {
                MelonLogger.Msg($"[QuestMod] WARNING: Inventory.AddGear not found!");
            }

            // Try to find AddItem with different signatures
            var addItem1 = typeof(Inventory).GetMethod("AddItem", new System.Type[] { typeof(GearItem) });
            var addItem2 = typeof(Inventory).GetMethod("AddItem", new System.Type[] { typeof(GearItem), typeof(bool) });
            
            if (addItem1 != null)
            {
                harmony.Patch(addItem1,
                    postfix: new HarmonyMethod(typeof(InventoryPatches), nameof(AddItem_Postfix)));
                MelonLogger.Msg($"[QuestMod] Patched Inventory.AddItem(GearItem)");
            }
            else if (addItem2 != null)
            {
                harmony.Patch(addItem2,
                    postfix: new HarmonyMethod(typeof(InventoryPatches), nameof(AddItem_Postfix)));
                MelonLogger.Msg($"[QuestMod] Patched Inventory.AddItem(GearItem, bool)");
            }
            else
            {
                MelonLogger.Msg($"[QuestMod] Note: Inventory.AddItem not found (using AddGear only)");
            }
        }

        public static void AddItem_Postfix(Inventory __instance, GearItem gearItem)
            => QuestMod.OnInventoryItemAdded(gearItem);

        public static void AddGear_Postfix(Inventory __instance, GearItem gi, bool enableNotificationFlag)
            => QuestMod.OnInventoryItemAdded(gi);
    }
}