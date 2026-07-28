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
        public string Status { get; set; } = "Active";  // Active or Complete
    }

    public class QuestState
    {
        public List<Quest> ActiveQuests { get; set; } = new List<Quest>();
    }

    public class QuestMod : MelonMod
    {
        public static QuestMod Instance { get; private set; }

        public QuestModSettings _settings;
        private GearLookup _lookup;
        private QuestState _questState;
        private System.Random _random = new System.Random();

        private bool _suppressLogging = false;  // Suppresses logs during initial inventory load
        private bool _needsInitialQuestGeneration = false;  // Set at init if save was fresh/empty; actual generation deferred to OnUpdate once TimeOfDay is valid
        private DateTime _lastCheckTime;
        private bool _isGivingReward = false;  // Guard against re-entrancy in reward giving
        public bool _modSettingsAvailable = false;

        private bool _needsSave = false;  // Flag to defer saves from callbacks
        private bool _saveInProgress = false;  // Prevents concurrent save operations (hang prevention)
        private object _dataLock = new object();  // Thread safety for console/save race conditions
        
        // Merge/drop tracking by item name, not GearItem ref (avoids stale-object crashes)
        private object _trackingLock = new object();
        private DateTime _lastStackCheckTime = DateTime.Now;
        private DateTime _lastValidationCheckTime = DateTime.Now;
        private Dictionary<string, int> _trackedItemUnitsByName = new Dictionary<string, int>();

        private TimeOfDay TOD => GameManager.GetTimeOfDayComponent();
        private float CurrentHour => TOD.GetHoursPlayedNotPaused() % 24f;

        private string GearLookupPath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Mods", "GearLookup.json");

        private string QuestDataPath =>
            Path.Combine(MelonLoader.Utils.MelonEnvironment.ModsDirectory, "QuestModData.json");

        // Initialize the mod - load settings, quests, and set up inventory monitoring
        public override void OnInitializeMelon()
        {
            Instance = this;
            MelonLogger.Msg("[QuestMod] *** Initializing Mission Impossible ***");

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
            
            // Initialize GUI system to display quests in Journal/Collections
            new QuestModGUI();

            if (_questState == null)
            {
                _questState = new QuestState();
            }

            // Defer quest gen to OnUpdate - TOD is null this early, would zero out EndDay
            _needsInitialQuestGeneration = (_questState.ActiveQuests.Count == 0);

            SaveData();
            MelonLogger.Msg($"[QuestMod] Initialized - {_questState.ActiveQuests.Count} quests active - Difficulty: {_settings.GetDifficultyDescription()}");
        }

        // Check if ModSettings.dll is installed (required dependency)
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

        // Save quest data before game shutdown
        public override void OnApplicationQuit()
        {
            try
            {
                MelonLogger.Msg("[QuestMod] Shutting down - saving data...");

                if (_questState != null && _lookup != null)
                {
                    SaveDataSync();
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

        // Handle scene loading - suppress initial inventory logging spam
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"[QuestMod] Scene loaded: {sceneName} (index: {buildIndex})");

            _suppressLogging = true;
            _lastCheckTime = DateTime.Now;
        }

        // Handle scene unloading - save quest data before transition
        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"[QuestMod] Scene unloaded: {sceneName}");

            try
            {
                if (_questState != null)
                {
                    // Sync save - scene unload is already a loading break, avoids teardown race
                    SaveDataSync();
                    MelonLogger.Msg("[QuestMod] Data saved before scene unload.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error saving data on scene unload: {ex.Message}");
            }
        }

        // Main loop: quest resets, stack detection, deferred saves (all non-blocking)
        public override void OnUpdate()
        {
            // End suppress window, then seed tracking baseline once inventory has settled
            if (_suppressLogging && (DateTime.Now - _lastCheckTime).TotalSeconds > 5)
            {
                _suppressLogging = false;
                SeedInventoryTracking();
            }

            // Deferred quest gen for a fresh save, once TimeOfDay is actually available
            if (_needsInitialQuestGeneration && _modSettingsAvailable && TOD != null)
            {
                _needsInitialQuestGeneration = false;  // Set false before the risky call, so a
                                                        // failure here can't cause a retry loop
                try
                {
                    RegenerateQuestsForSettingsChange();
                    SaveData();
                    MelonLogger.Msg($"[QuestMod] Generated initial quests now that TimeOfDay is available - {_questState.ActiveQuests.Count} quests active");
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[QuestMod] Error generating initial quests: {ex.Message}");
                }
            }

            // F2: Complete all daily quests (debug command)
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
                        CheckAndCompleteQuest(quest, isDebugCommand: true, questNumber: questNumber);
                        questNumber++;
                    }
                }
            }

            // F3: Complete all weekly quests (debug command)
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
                        CheckAndCompleteQuest(quest, isDebugCommand: true, questNumber: questNumber);
                        questNumber++;
                    }
                }
            }

            // F4: Complete all monthly quests (debug command)
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
            
            // Poll for stack merges - AddGear/native event/TryStackingItem don't fire for those
            if ((DateTime.Now - _lastStackCheckTime).TotalMilliseconds > 500 && _modSettingsAvailable)
            {
                CheckForStackedItems();
                _lastStackCheckTime = DateTime.Now;
            }

            // Periodic configuration validation (every 30 seconds) Ensures at least one quest type remains enabled
            if ((DateTime.Now - _lastValidationCheckTime).TotalSeconds > 30 && _modSettingsAvailable)
            {
                _settings.ValidateConfiguration();
                _lastValidationCheckTime = DateTime.Now;
            }
            
            // Deferred save - prevents blocking the game thread with file I/O
            if (_needsSave && !_saveInProgress)
            {
                SaveData();
                _needsSave = false;
            }
        }


        // Live-queries total units for a name - never holds a GearItem ref (can go stale/crash)
        private int GetTotalUnitsInInventory(Inventory inventory, string itemName)
        {
            var matches = new Il2CppSystem.Collections.Generic.List<GearItem>();
            inventory.GetItems(itemName, matches);

            int total = 0;
            foreach (var item in matches)
            {
                if (item != null && item.m_StackableItem != null)
                {
                    total += item.m_StackableItem.m_Units;
                }
            }
            return total;
        }

        // Polling fallback for stack merges/drops, keyed by name (see GetTotalUnitsInInventory)
        private void CheckForStackedItems()
        {
            try
            {
                // Same suppress guard as AddGear path, else scene-load reload miscounts as pickups
                if (_suppressLogging)
                    return;

                if (_questState?.ActiveQuests == null)
                    return;

                var inventory = GameManager.GetInventoryComponent();
                if (inventory == null)
                    return;

                bool verbose = _settings != null && _settings.EnablePickupLogging;
                var collectKeys = _questState.ActiveQuests.Select(q => q.CollectKey).Distinct().ToList();

                lock (_trackingLock)
                {
                    foreach (var key in collectKeys)
                    {
                        int currentTotal;
                        try
                        {
                            currentTotal = GetTotalUnitsInInventory(inventory, key);
                        }
                        catch
                        {
                            continue;  // Skip this key this cycle, retry next poll
                        }

                        if (!_trackedItemUnitsByName.TryGetValue(key, out int lastKnown))
                        {
                            // No baseline yet - record only, don't count as a pickup
                            _trackedItemUnitsByName[key] = currentTotal;
                            continue;
                        }

                        if (currentTotal > lastKnown)
                        {
                            if (verbose)
                            {
                                MelonLogger.Msg($"[QuestMod] Stack increase detected: {key} {lastKnown} -> {currentTotal}");
                            }

                            int unitsAdded = currentTotal - lastKnown;
                            for (int i = 0; i < unitsAdded; i++)
                            {
                                ProcessQuestItemPickup(key, verbose);
                            }
                        }
                        else if (currentTotal < lastKnown && verbose)
                        {
                            // Drop: don't count, but update baseline or a repickup won't register
                            MelonLogger.Msg($"[QuestMod] Stack decrease detected: {key} {lastKnown} -> {currentTotal}");
                        }

                        _trackedItemUnitsByName[key] = currentTotal;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QuestMod] Error checking for stacked items: {ex.Message}");
            }
        }

        // Seeds a starting unit baseline for quest items already in inventory
        private void SeedInventoryTracking()
        {
            try
            {
                var inventory = GameManager.GetInventoryComponent();
                if (inventory == null || _questState == null)
                    return;

                var collectKeys = _questState.ActiveQuests.Select(q => q.CollectKey).Distinct().ToList();

                lock (_trackingLock)
                {
                    foreach (var key in collectKeys)
                    {
                        _trackedItemUnitsByName[key] = GetTotalUnitsInInventory(inventory, key);
                    }
                }

                // MelonLogger.Msg($"[QuestMod] Seeded inventory tracking across {collectKeys.Count} quest item type(s)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QuestMod] Error seeding inventory tracking: {ex.Message}");
            }
        }

        // Check if quest periods have ended and regenerate quests as needed Also handles reward distribution when quest periods complete
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
                    // If quest objective is complete, handle period end
                    if (quest.CurrentAmount >= quest.RequiredAmount && quest.Status == "Complete")
                    {
                        bool periodEnded = (day > quest.EndDay) || 
                                           (day == quest.EndDay && hour >= quest.EndHour);
                        
                        if (periodEnded)
                        {
                            // Reward already given when quest was marked Complete - just remove and regenerate
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
                            int neededCount = targetCount - currentCount;
                            if (neededCount > 0)
                            {
                                GenerateQuestsAndNotify(quest.Type, neededCount, showCreationLog: true);
                            }
                            
                            SaveData();
                        }
                        
                        // Quest is complete - don't process further resets for this quest
                        continue;
                    }
                    
                    bool shouldReset = false;

                    // Check if quest period has ENDED (use EndDay/EndHour)
                    bool questPeriodEnded = (day > quest.EndDay) || 
                                            (day == quest.EndDay && hour >= quest.EndHour);
                    
                    if (questPeriodEnded)
                    {
                        shouldReset = true;
                    }

                    if (shouldReset)
                    {
                        _questState.ActiveQuests.Remove(quest);
                        
                        // Only generate replacements if we're below the target count for this quest type
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
                            GenerateQuestsAndNotify(quest.Type, neededCount, showCreationLog: true);
                        }
                    }
                }

                SaveData();
            }
            catch
            {
                // Silently fail - CheckQuestResets runs every 60 seconds so occasional errors are normal
            }
        }

        private void GenerateQuestType(string questType, bool showCreationLog = false)
        {
            int count = questType switch
            {
                "Daily" => _settings.DailyQuestCount,
                "Weekly" => _settings.WeeklyQuestCount,
                "Monthly" => _settings.MonthlyQuestCount,
                _ => 0
            };

            GenerateQuestsAndNotify(questType, count, showCreationLog);

            SaveData();
        }

        // Generate one random quest of a type, applying difficulty multipliers
        private bool GenerateQuestOfType(string questType, bool showCreationLog = false)
        {
            if (_lookup?.items_to_collect == null)
            {
                MelonLogger.Error($"[QuestMod] Cannot generate {questType} quest: items_to_collect is null!");
                return false;
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
                return false;
            }

            var collectItem = validItems[_random.Next(validItems.Count)];
            var collectVariant = collectItem.Value.First(v => v.quest_type.Equals(questType, System.StringComparison.OrdinalIgnoreCase) && v.enabled);

            var rewardItems = _lookup.items_as_reward
                .Where(kvp => kvp.Value.Any(v => v.quest_type.Equals(questType, System.StringComparison.OrdinalIgnoreCase) && v.enabled && allowedCategories.Contains(v.category)))
                .ToList();

            if (rewardItems.Count == 0)
            {
                MelonLogger.Error($"[QuestMod] No valid {questType} reward items found!");
                return false;
            }

            var rewardItem = rewardItems[_random.Next(rewardItems.Count)];
            var rewardVariant = rewardItem.Value.First(v => v.quest_type.Equals(questType, System.StringComparison.OrdinalIgnoreCase) && v.enabled);

            // Apply difficulty multipliers
            int requiredAmount = _settings.ApplyRequiredMultiplier(collectVariant.required);
            int rewardAmount = _settings.ApplyRewardMultiplier(rewardVariant.reward_amount);

            int currentDay = GetCurrentDay();
            float currentHour = GetCurrentHour();
            
            // Calculate quest period end time (always from 9:00 AM to 8:50 AM next period)
            const float QUEST_START_HOUR = 9.0f;
            const float QUEST_END_HOUR = 8.833333f;  // 8:50 AM
            
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

            return true;
        }

        // Generate quest(s) of a type and show one combined HUD notification
        private void GenerateQuestsAndNotify(string questType, int count, bool showCreationLog)
        {
            int successCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (GenerateQuestOfType(questType, showCreationLog: showCreationLog))
                    successCount++;
            }

            if (showCreationLog && successCount > 0)
            {
                HUDMessage.AddMessage($"{successCount} new {questType} Quests created", true);
            }
        }

        // Complete a quest if threshold reached; 0.5s cooldown prevents completion spam
        public void CheckAndCompleteQuest(Quest quest, bool isDebugCommand = false, int questNumber = 0)
        {
            // For debug commands: process regardless of status. For normal gameplay: skip if already Complete
            bool shouldProcess = isDebugCommand || (quest.CurrentAmount >= quest.RequiredAmount && quest.Status != "Complete");
            
            if (shouldProcess)
            {
                // Get target count for this quest type
                int targetCount = quest.Type switch
                {
                    "Daily" => _settings.EnableDailyQuests ? _settings.DailyQuestCount : 0,
                    "Weekly" => _settings.EnableWeeklyQuests ? _settings.WeeklyQuestCount : 0,
                    "Monthly" => _settings.EnableMonthlyQuests ? _settings.MonthlyQuestCount : 0,
                    _ => 0
                };

                // Calculate progress: how many completed vs target
                int completedCount = _questState.ActiveQuests.Count(q => q.Type == quest.Type && q.Status == "Complete") + 1;

                // Display objective completion notification with progress
                try
                {
                    HUDMessage.AddMessage($"{completedCount}/{targetCount} {quest.Type} Quest completed!", true);
                }
                catch (Exception displayEx)
                {
                    MelonLogger.Msg($"[QuestMod] Note: Could not display completion notification (non-critical): {displayEx.Message}");
                }
                
                // Mark quest as completed (keep in list, don't remove)
                quest.Status = "Complete";
                
                // IMMEDIATELY GIVE REWARD when quest is completed (not waiting for period to end)
                GiveReward(quest, questNumber);
                
                // For debug commands: also generate replacement quests
                if (isDebugCommand)
                {
                    // Remove completed quest and generate new one
                    var questToRemove = _questState.ActiveQuests.FirstOrDefault(q => 
                        q.Type == quest.Type && 
                        q.CollectKey == quest.CollectKey &&
                        q.RequiredAmount == quest.RequiredAmount &&
                        q.Status == "Complete"
                    );
                    
                    if (questToRemove != null)
                    {
                        _questState.ActiveQuests.Remove(questToRemove);
                        
                        // Generate replacement quest
                        GenerateQuestsAndNotify(quest.Type, 1, showCreationLog: false);
                    }
                }
                
                // Save progress to file
                SaveData();
            }
        }

        // Give the player their quest reward items HANG PREVENTION: Uses locks but no file I/O, non-blocking
        private void GiveReward(Quest quest, int questNumber = 0)
        {
            try
            {
                _isGivingReward = true;  // Set guard to prevent re-entrancy
                
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
                    
                    // Display reward notification using HUDMessage (works everywhere, indoor/outdoor)
                    try
                    {
                        if (successCount > 0)
                        {
                            HUDMessage.AddMessage($"Quest Reward: {successCount}x {quest.RewardKey}", true);
                            MelonLogger.Msg($"[QuestMod] Reward notification displayed: {successCount}x {quest.RewardKey}");
                        }
                    }
                    catch (Exception displayEx)
                    {
                        MelonLogger.Msg($"[QuestMod] Note: Could not display reward notification (non-critical): {displayEx.Message}");
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
                _isGivingReward = false;  // Clear guard in finally block (always executes)
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

        // Clear and regenerate all quests based on current settings
        public void RegenerateQuestsForSettingsChange(bool showCreationLogs = false)
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
                GenerateQuestType("Daily", showCreationLog: showCreationLogs);
                totalQuests += _settings.DailyQuestCount;
            }
            else
            {
                MelonLogger.Msg("[QuestMod] Daily quests DISABLED");
            }

            if (_settings.EnableWeeklyQuests)
            {
                MelonLogger.Msg($"[QuestMod] Generating {_settings.WeeklyQuestCount} Weekly quests...");
                GenerateQuestType("Weekly", showCreationLog: showCreationLogs);
                totalQuests += _settings.WeeklyQuestCount;
            }
            else
            {
                MelonLogger.Msg("[QuestMod] Weekly quests DISABLED");
            }

            if (_settings.EnableMonthlyQuests)
            {
                MelonLogger.Msg($"[QuestMod] Generating {_settings.MonthlyQuestCount} Monthly quests...");
                GenerateQuestType("Monthly", showCreationLog: showCreationLogs);
                totalQuests += _settings.MonthlyQuestCount;
            }
            else
            {
                MelonLogger.Msg("[QuestMod] Monthly quests DISABLED");
            }

            MelonLogger.Msg($"[QuestMod] Total quests generated: {_questState.ActiveQuests.Count}/{totalQuests}");
            SaveData();
        }

        // Load GearLookup.json containing all available quest items and their properties
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

        // Load quest data from disk - persists player progress between sessions
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
                
                // Recalculate Status for all quests based on CurrentAmount This handles old JSON files that don't have Status field
                foreach (var quest in _questState.ActiveQuests)
                {
                    quest.Status = quest.CurrentAmount >= quest.RequiredAmount ? "Complete" : "Active";
                }
                
                MelonLogger.Msg("[QuestMod] Quest data loaded from disk");
                
                // Display current missions on game load
                if (_questState.ActiveQuests.Count > 0)
                {
                    //MelonLogger.Msg($"[QuestMod] Current Missions ({_questState.ActiveQuests.Count} active):");
                    foreach (var quest in _questState.ActiveQuests)
                    {
                        //MelonLogger.Msg($"[QuestMod]   - {quest.Type} Quest: {quest.CollectKey} = {quest.CurrentAmount}/{quest.RequiredAmount} - {quest.Status}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error loading quest data: {ex.Message}");
                _questState = new QuestState();
            }
        }

        // Async save for normal gameplay - do not use during shutdown (see SaveDataSync)
        private void SaveData()
        {
            if (_saveInProgress)
                return;

            _saveInProgress = true;
            System.Threading.ThreadPool.QueueUserWorkItem(SaveDataThreadCallback);
        }

        private void SaveDataThreadCallback(object state)
        {
            WriteQuestDataToDisk();
        }

        // Sync save for shutdown only - avoids a background thread racing process teardown
        private void SaveDataSync()
        {
            if (_saveInProgress)
                return;

            _saveInProgress = true;
            WriteQuestDataToDisk();
        }

        private void WriteQuestDataToDisk()
        {
            try
            {
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

        // Tracks quest progress on item pickup; locked for console/save safety
        public static void OnInventoryItemAdded(GearItem gearItem, bool isBulkStack = false)
        {
            if (Instance == null || gearItem == null)
                return;

            // Cheap guards first, before any logging (this can fire in a big burst)
            if (Instance._isGivingReward || Instance._suppressLogging)
                return;

            if (Instance._questState?.ActiveQuests == null || Instance._questState.ActiveQuests.Count == 0)
                return;

            bool verbose = Instance._settings != null && Instance._settings.EnablePickupLogging;

            // Update stacking baseline for direct AddGear adds only, not polled units
            if (!isBulkStack)
            {
                try
                {
                    if (gearItem.m_StackableItem != null)
                    {
                        lock (Instance._trackingLock)
                        {
                            Instance._trackedItemUnitsByName[gearItem.name] = gearItem.m_StackableItem.m_Units;
                        }
                    }
                }
                catch { }  // Silently ignore tracking errors
            }

            Instance.ProcessQuestItemPickup(gearItem.name, verbose);
        }

        // Core quest-progress logic by item name, shared by AddGear and polling paths
        private void ProcessQuestItemPickup(string itemName, bool verbose)
        {
            lock (_dataLock)
            {
                try
                {
                    bool foundMatch = false;

                    foreach (var quest in _questState.ActiveQuests.ToList())
                    {
                        if (!quest.CollectKey.Equals(itemName, System.StringComparison.OrdinalIgnoreCase))
                            continue;

                        foundMatch = true;
                        quest.CurrentAmount += 1;

                        if (verbose)
                        {
                            MelonLogger.Msg($"[QuestMod] Quest Item added to inventory: '{itemName}'");
                            MelonLogger.Msg($"[QuestMod] Progress: {quest.Type} Quest: {quest.CollectKey} - {quest.CurrentAmount}/{quest.RequiredAmount} (+1)");
                        }

                        // Only complete once, on first threshold crossing
                        if (quest.CurrentAmount >= quest.RequiredAmount && quest.Status != "Complete")
                        {
                            try
                            {
                                CheckAndCompleteQuest(quest, isDebugCommand: false);
                            }
                            catch (Exception ex)
                            {
                                MelonLogger.Error($"[QuestMod] Error in CheckAndCompleteQuest: {ex.Message}");
                            }

                            // Only claim success if the quest actually completed - CheckAndCompleteQuest
                            // can decline to complete (e.g. a guard/cooldown), so don't assume it worked
                            if (verbose && quest.Status == "Complete")
                            {
                                MelonLogger.Msg($"[QuestMod] {quest.Type} Quest objective complete! Reward given.");
                            }
                        }

                        _needsSave = true;
                    }

                    if (!foundMatch && verbose)
                    {
                        MelonLogger.Msg($"[QuestMod] Item added to inventory: '{itemName}'");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[QuestMod] Error in ProcessQuestItemPickup: {ex.Message}");
                }
            }
        }

        // Quest progress summary for a type, used by QuestModGUI
        public (int completed, int total, bool enabled) GetQuestSummary(string type)
        {
            if (_questState == null || _settings == null)
                return (0, 0, false);

            var quests = _questState.ActiveQuests.Where(q => q.Type == type).ToList();
            int total = quests.Count;
            int completed = quests.Count(q => q.Status == "Complete");

            bool enabled = type switch
            {
                "Daily" => _settings.EnableDailyQuests,
                "Weekly" => _settings.EnableWeeklyQuests,
                "Monthly" => _settings.EnableMonthlyQuests,
                _ => false
            };

            return (completed, total, enabled);
        }

        // Active quests of a type, used by QuestModGUI's detail panel
        public List<Quest> GetActiveQuestsOfType(string type)
        {
            if (_questState == null)
                return new List<Quest>();

            return _questState.ActiveQuests.Where(q => q.Type == type).ToList();
        }

        // Get the ShowReward setting from mod settings. Used by QuestModGUI to determine if rewards should be visible or hidden as *****.
        public bool GetShowRewardSetting()
        {
            return _settings?.ShowReward ?? false;
        }
    }

    // Detects new item stacks; merges are caught separately by CheckForStackedItems polling
    public static class InventoryPatches
    {
        public static void ApplyAll(HarmonyLib.Harmony harmony)
        {
            var addGear = typeof(Inventory).GetMethod("AddGear");

            if (addGear != null)
            {
                harmony.Patch(addGear,
                    postfix: new HarmonyMethod(typeof(InventoryPatches), nameof(AddGear_Postfix)));
                //MelonLogger.Msg("[QuestMod] Patched Inventory.AddGear");
            }
            else
            {
                MelonLogger.Msg("[QuestMod] WARNING: Inventory.AddGear not found!");
            }
        }

        public static void AddGear_Postfix(Inventory __instance, GearItem gi, bool enableNotificationFlag)
        {
            try
            {
                if (gi == null)
                    return;

                QuestMod.OnInventoryItemAdded(gi);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Exception in AddGear_Postfix: {ex.Message}");
            }
        }
    }
}