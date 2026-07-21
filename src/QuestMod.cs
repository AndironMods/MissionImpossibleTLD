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

        private bool _needsSave = false;  // Flag to defer saves from callbacks
        private bool _saveInProgress = false;  // HANG FIX: Prevent blocking saves
        private object _dataLock = new object();  // CRASH FIX: Thread safety for console
        
        // NEW: Track GearItems and their m_Units to detect stacking
        private Dictionary<GearItem, int> _trackedUnitCounts = new Dictionary<GearItem, int>();
        private object _trackingLock = new object();
        private DateTime _lastStackCheckTime = DateTime.Now;

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
            // Re-enable logging after initial inventory load (wait 5 seconds for full load)
            if (_suppressLogging && (DateTime.Now - _lastCheckTime).TotalSeconds > 5)
            {
                _suppressLogging = false;
            }

            if (!_initialized && _modSettingsAvailable)
            {
                _initialized = true;
            }

            // F2: Complete daily quests
            if (Input.GetKeyDown(KeyCode.F2) && _modSettingsAvailable)
            {
                var dailyQuests = _questState.ActiveQuests.Where(q => q.Type == "Daily").ToList();
                if (dailyQuests.Count > 0)
                {
                    MelonLogger.Msg($"[QuestMod] All Daily Quests completed!");
                    
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
                    MelonLogger.Msg($"[QuestMod] All Weekly Quests completed!");
                    
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
                    MelonLogger.Msg($"[QuestMod] All Monthly Quests completed!");
                    
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

            // Check for quest resets every 60 seconds
            if ((DateTime.Now - _lastCheckTime).TotalSeconds > 60 && _modSettingsAvailable)
            {
                CheckQuestResets();
                _lastCheckTime = DateTime.Now;
            }
            
            // NEW: Check for stacked items every 0.5 seconds (detect m_Units increases)
            if ((DateTime.Now - _lastStackCheckTime).TotalMilliseconds > 500 && _modSettingsAvailable)
            {
                CheckForStackedItems();
                _lastStackCheckTime = DateTime.Now;
            }
            
            // HANG FIX: Save deferred changes but don't block
            if (_needsSave && !_saveInProgress)
            {
                SaveData();
                _needsSave = false;
            }
        }

        private void CheckForStackedItems()
        {
            try
            {
                lock (_trackingLock)
                {
                    var itemsToCheck = _trackedUnitCounts.ToList();
                    foreach (var kvp in itemsToCheck)
                    {
                        GearItem item = kvp.Key;
                        int lastKnownUnits = kvp.Value;
                        
                        // Check if item still exists and has more units
                        if (item != null && item.m_StackableItem != null)
                        {
                            int currentUnits = item.m_StackableItem.m_Units;
                            if (currentUnits > lastKnownUnits)
                            {
                                // Units increased! Fire callbacks for each new unit
                                int unitsAdded = currentUnits - lastKnownUnits;
                                
                                for (int i = 0; i < unitsAdded; i++)
                                {
                                    // Fire quest callback for this stacked item
                                    OnInventoryItemAdded(item, isBulkStack: true);
                                }
                                
                                // Update tracked count
                                _trackedUnitCounts[item] = currentUnits;
                            }
                        }
                        else if (item == null || item.m_StackableItem == null)
                        {
                            // Item no longer exists, remove from tracking
                            _trackedUnitCounts.Remove(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QuestMod] Error checking for stacked items: {ex.Message}");
            }
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

                // CRASH FIX: Lock prevents console from accessing data while checking resets
                lock (_dataLock)
                {
                    foreach (var quest in _questState.ActiveQuests.ToList())
                    {
                        // Check if quest objective is complete and period has ended - give reward
                        if (quest.CurrentAmount >= quest.RequiredAmount)
                        {
                            bool periodEnded = (day > quest.EndDay) || 
                                               (day == quest.EndDay && hour >= quest.EndHour);
                            
                            if (periodEnded)
                            {
                                GiveReward(quest);
                                
                                _questState.ActiveQuests.Remove(quest);
                                
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

                        // Check if quest period has ENDED
                        bool questPeriodEnded = (day > quest.EndDay) || 
                                                (day == quest.EndDay && hour >= quest.EndHour);
                        
                        if (questPeriodEnded)
                        {
                            shouldReset = true;
                        }

                        if (shouldReset)
                        {
                            _questState.ActiveQuests.Remove(quest);
                            
                            // Only generate replacements if we're below the target count
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
                        }
                    }

                    SaveData();
                }
            }
            catch
            {
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
            
            const float QUEST_START_HOUR = 9.0f;
            const float QUEST_END_HOUR = 8.833333f;
            
            int startDay = currentDay;
            float startHour = QUEST_START_HOUR;
            
            int endDay = currentDay;
            float endHour = QUEST_END_HOUR;
            
            if (questType.Equals("Daily", System.StringComparison.OrdinalIgnoreCase))
            {
                endDay = currentDay + 1;
            }
            else if (questType.Equals("Weekly", System.StringComparison.OrdinalIgnoreCase))
            {
                endDay = currentDay + 7;
            }
            else if (questType.Equals("Monthly", System.StringComparison.OrdinalIgnoreCase))
            {
                endDay = currentDay + 30;
            }

            var quest = new Quest
            {
                Type = questType,
                CollectKey = collectVariant.spawn_name,
                RequiredAmount = requiredAmount,
                CurrentAmount = 0,
                RewardKey = rewardVariant.spawn_name,
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
                MelonLogger.Msg($"[QuestMod] Category: {collectVariant.category}");
                MelonLogger.Msg($"[QuestMod] Progress: 0/{requiredAmount}");
                
                string rewardDisplay = !_settings.ShowReward ? "***" : $"{rewardAmount}x {rewardItem.Key} ({rewardVariant.spawn_name})";
                MelonLogger.Msg($"[QuestMod] Reward: {rewardDisplay}");
                
                MelonLogger.Msg($"[QuestMod] ===================================");
            }
        }

        private void CheckAndCompleteQuest(Quest quest, bool isDebugCommand = false, int questNumber = 0)
        {
            if (quest.CurrentAmount >= quest.RequiredAmount)
            {
                if (!isDebugCommand && (DateTime.Now - _lastQuestCompletionTime).TotalSeconds < 0.5)
                {
                    return;
                }
                
                _lastQuestCompletionTime = DateTime.Now;
                
                if (!isDebugCommand)
                {
                    MelonLogger.Msg($"[QuestMod] ========== COMPLETING QUEST ==========");
                    MelonLogger.Msg($"[QuestMod] Type: {quest.Type}");
                    MelonLogger.Msg($"[QuestMod] Collected: {quest.CurrentAmount}/{quest.RequiredAmount}");
                }
                
                GiveReward(quest, questNumber);
                
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
                _isGivingReward = true;
                
                // CRASH FIX: Lock prevents console from accessing data while giving reward
                lock (_dataLock)
                {
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

                    string rewardDisplay = $"{successCount} {quest.RewardKey}";
                    
                    if (_settings.EnableDetailedLogging)
                    {
                        MelonLogger.Msg($"[QuestMod] ========== QUEST COMPLETE ==========");
                        MelonLogger.Msg($"[QuestMod] Type: {quest.Type}");
                        MelonLogger.Msg($"[QuestMod] Difficulty: {_settings.GetDifficultyDescription()}");
                        
                        string itemDisplayName = quest.CollectKey;
                        if (_lookup != null && _lookup.items_to_collect != null)
                        {
                            foreach (var category in _lookup.items_to_collect)
                            {
                                var item = category.Value.FirstOrDefault(x => x.spawn_name == quest.CollectKey);
                                if (item != null)
                                {
                                    itemDisplayName = category.Key;
                                    MelonLogger.Msg($"[QuestMod] Objective: Collect {quest.RequiredAmount}x {itemDisplayName} ({quest.CollectKey})");
                                    MelonLogger.Msg($"[QuestMod] Category: {item.category}");
                                    break;
                                }
                            }
                        }
                        
                        if (itemDisplayName == quest.CollectKey)
                        {
                            MelonLogger.Msg($"[QuestMod] Objective: Collect {quest.RequiredAmount}x {quest.CollectKey}");
                        }
                        
                        MelonLogger.Msg($"[QuestMod] Progress: {quest.CurrentAmount}/{quest.RequiredAmount}");
                        MelonLogger.Msg($"[QuestMod] Reward: {rewardDisplay}");
                        MelonLogger.Msg($"[QuestMod] ===================================");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error giving reward: {ex.Message}");
                MelonLogger.Error($"[QuestMod] Stack trace: {ex.StackTrace}");
            }
            finally
            {
                _isGivingReward = false;
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
                
                foreach (var quest in _questState.ActiveQuests)
                {
                    quest.Status = quest.CurrentAmount >= quest.RequiredAmount ? "Complete" : "Ongoing";
                }
                
                MelonLogger.Msg("[QuestMod] Quest data loaded from disk");
                
                if (_questState.ActiveQuests.Count > 0)
                {
                    MelonLogger.Msg($"[QuestMod] Current Missions ({_questState.ActiveQuests.Count} active):");
                    foreach (var quest in _questState.ActiveQuests)
                    {
                        MelonLogger.Msg($"[QuestMod]   - {quest.Type} Quest: {quest.CollectKey} = {quest.CurrentAmount}/{quest.RequiredAmount} - {quest.Status}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error loading quest data: {ex.Message}");
                _questState = new QuestState();
            }
        }

        // HANG FIX: Async save on background thread
        private void SaveData()
        {
            if (_saveInProgress)
                return;

            _saveInProgress = true;
            
            System.Threading.ThreadPool.QueueUserWorkItem(SaveDataAsync);
        }

        private void SaveDataAsync(object state)
        {
            try
            {
                // CRASH FIX: Lock prevents console from accessing data while saving
                lock (_dataLock)
                {
                    string dirPath = Path.GetDirectoryName(QuestDataPath);
                    if (!Directory.Exists(dirPath))
                        Directory.CreateDirectory(dirPath);

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(_questState, options);
                    File.WriteAllText(QuestDataPath, json);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error saving quest data: {ex.Message}");
            }
            finally
            {
                _saveInProgress = false;
            }
        }

        // HANG FIX: Simplified inventory callback
        public static void OnInventoryItemAdded(GearItem gearItem, bool isBulkStack = false)
        {
            if (Instance == null || gearItem == null)
                return;

            if (Instance._isGivingReward)
                return;

            if (Instance._questState?.ActiveQuests == null || Instance._questState.ActiveQuests.Count == 0)
                return;

            if (Instance._suppressLogging)
                return;

            if (Instance._settings != null && !Instance._settings.SuppressPickupLogging)
                MelonLogger.Msg($"[QuestMod] Item added to inventory: '{gearItem.name}' {(isBulkStack ? "[STACKED]" : "")}");

            // Track this item's m_Units for future stacking detection
            if (!isBulkStack)
            {
                lock (Instance._trackingLock)
                {
                    try
                    {
                        if (gearItem.m_StackableItem != null)
                        {
                            Instance._trackedUnitCounts[gearItem] = gearItem.m_StackableItem.m_Units;
                        }
                    }
                    catch { }
                }
            }

            // CRASH FIX: Lock to prevent console/save race condition
            lock (Instance._dataLock)
            {
                // Get the actual quantity - each call is for 1 unit
                // (If it's a stacked callback, it was already split into individual units by CheckForStackedItems)
                int quantityToAdd = 1;

                // SIMPLE: Just check if item matches quest and increment counter
                foreach (var quest in Instance._questState.ActiveQuests.ToList())
                {
                    if (quest.CollectKey.Equals(gearItem.name, System.StringComparison.OrdinalIgnoreCase))
                    {
                        quest.CurrentAmount += quantityToAdd;
                        
                        if (quest.CurrentAmount >= quest.RequiredAmount)
                        {
                            quest.Status = "Complete";
                        }
                        
                        if (!Instance._settings.SuppressPickupLogging)
                        {
                            MelonLogger.Msg($"[QuestMod] Progress: {quest.Type} Quest: {quest.CollectKey} - {quest.CurrentAmount}/{quest.RequiredAmount} (+{quantityToAdd})");
                        }
                        
                        Instance._needsSave = true;
                        
                        if (quest.CurrentAmount >= quest.RequiredAmount && Instance._settings.EnableDetailedLogging)
                        {
                            MelonLogger.Msg($"[QuestMod] {quest.Type} Quest objective complete!");
                            MelonLogger.Msg($"[QuestMod] {quest.Type} Period must end before reward is given.");
                        }
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