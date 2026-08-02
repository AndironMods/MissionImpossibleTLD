using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using MelonLoader;
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
        // When the quest period ends
        public int EndDay { get; set; }
        // When the quest period ends
        public float EndHour { get; set; }
        // Active or Complete
        public string Status { get; set; } = "Active";
    }

    public class QuestState
    {
        public List<Quest> ActiveQuests { get; set; } = new List<Quest>();
        // Set once a "_DLC01" scene is observed loading - Unity can't load a scene that doesn't
        // exist, so this is a reliable, zero-guesswork signal that DLC content is installed.
        // DLC content is force-enabled from that point on; there is no player-facing toggle.
        public bool DlcAutoDetected { get; set; } = false;
    }

    public class QuestMod : MelonMod
    {
        public static QuestMod Instance { get; private set; }

        public QuestModSettings _settings;
        private GearLookup _lookup;
        // Maps spawn_name (e.g. "GEAR_CookedBannock") -> the friendly JSON key name (e.g. "Bannock"),
        // built once when GearLookup.json loads. Used for quest objective display text.
        private Dictionary<string, string> _spawnNameToDisplayName = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        private QuestState _questState;
        private System.Random _random = new System.Random();

        // Suppresses logs during initial inventory load
        private bool _suppressLogging = false;
        // Set whenever quest generation is needed but TimeOfDay isn't ready yet; actual generation deferred to OnUpdate
        private bool _needsInitialQuestGeneration = false;
        // Set after RegenerateQuestsForSettingsChange; actual reseed deferred to OnUpdate
        private bool _needsReseedAfterRegen = false;
        private string _currentSceneName = "";
        // TimeOfDay can be non-null at main menu but report placeholder values; requires gameplay scene check
        private bool IsInGameplayScene => !string.IsNullOrEmpty(_currentSceneName) &&
            !_currentSceneName.Contains("MainMenu", StringComparison.OrdinalIgnoreCase) &&
            !_currentSceneName.Equals("Empty", StringComparison.OrdinalIgnoreCase);
        private DateTime _lastCheckTime;
        public bool _modSettingsAvailable = false;

        // Flag to defer saves from callbacks
        private bool _needsSave = false;
        // Prevents concurrent save operations (hang prevention)
        private bool _saveInProgress = false;
        // Thread safety for console/save race conditions
        private object _dataLock = new object();
        
        // Quest item counts tracked by item name, not GearItem ref (avoids stale-object crashes).
        // This interval scan is the ONLY inventory-progress mechanism (no AddGear event patch).
        private object _trackingLock = new object();
        private DateTime _lastInventoryScanTime = DateTime.Now;
        private DateTime _lastValidationCheckTime = DateTime.Now;
        private Dictionary<string, int> _trackedItemUnitsByName = new Dictionary<string, int>();

        private TimeOfDay TOD => GameManager.GetTimeOfDayComponent();
        private float CurrentHour => TOD.GetHour() + (TOD.GetMinutes() / 60f);

        // Quest cycles run 9:00 AM to 8:50 AM; new quests wait until 9 AM to appear
        private const float QUEST_START_HOUR = 9.0f;
        // 8:50 AM
        private const float QUEST_END_HOUR = 8.833333f;
        private const int QUEST_CHECK_INTERVAL_SECONDS = 60;
        // Sole inventory-progress mechanism: how often we compare current item counts against
        // the last known counts. No Harmony patch on AddGear - everything goes through this.
        private const int INVENTORY_SCAN_INTERVAL_MS = 1000;

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
            MelonLogger.Msg($"[QuestMod] Scene loaded: {sceneName}");
            _currentSceneName = sceneName;
            _suppressLogging = true;
            _lastCheckTime = DateTime.Now;

            CheckForDlcAutoEnable(sceneName);
        }

        // A "_DLC01" scene can only load if the DLC is installed - Unity can't load a scene
        // that doesn't exist on disk. This fires once per install (persisted via _questState).
        // DLC content is force-enabled from this point on - there is no player-facing toggle.
        private void CheckForDlcAutoEnable(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || _questState == null)
                return;

            if (_questState.DlcAutoDetected)
                return;

            if (sceneName.IndexOf("_DLC01", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            _questState.DlcAutoDetected = true;
            _needsSave = true;

            MelonLogger.Msg($"[QuestMod] DLC content detected (scene: {sceneName}) - DLC items now included in quest generation.");

            // Only regenerate if quests already exist this session - if initial generation
            // hasn't run yet, it will naturally include DLC items now that this is set.
            if (!_needsInitialQuestGeneration)
            {
                RegenerateQuestsForSettingsChange(showCreationLogs: true);
            }
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

            // Deferred quest generation - fresh save, or a settings change that came in before
            // we were actually in a gameplay scene. TOD != null alone is NOT enough - confirmed
            // via testing that TimeOfDay can be non-null at the main menu with placeholder data.
            if (_needsInitialQuestGeneration && _modSettingsAvailable && TOD != null && IsInGameplayScene && !_suppressLogging)
            {
                // Set false before the risky call so a failure here can't cause a retry loop
                _needsInitialQuestGeneration = false;
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

            // Deferred reseed after RegenerateQuestsForSettingsChange - see comment there for why this isn't called directly from that method
            if (_needsReseedAfterRegen)
            {
                _needsReseedAfterRegen = false;
                try
                {
                    SeedInventoryTracking();
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[QuestMod] Error reseeding inventory tracking: {ex.Message}");
                }
            }

            // F2: Complete all daily quests (debug command - only active when logging is enabled)
            if (Input.GetKeyDown(KeyCode.F2) && _modSettingsAvailable && _settings.EnablePickupLogging)
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

            // F3: Complete all weekly quests (debug command - only active when logging is enabled)
            if (Input.GetKeyDown(KeyCode.F3) && _modSettingsAvailable && _settings.EnablePickupLogging)
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

            // F4: Complete all monthly quests (debug command - only active when logging is enabled)
            if (Input.GetKeyDown(KeyCode.F4) && _modSettingsAvailable && _settings.EnablePickupLogging)
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

            // Check for quest resets every QUEST_CHECK_INTERVAL_SECONDS
            if ((DateTime.Now - _lastCheckTime).TotalSeconds > QUEST_CHECK_INTERVAL_SECONDS && _modSettingsAvailable)
            {
                CheckQuestResets();
                _lastCheckTime = DateTime.Now;
            }
            
            // Scan inventory for quest item count changes (adds, stack merges, everything).
            // This is the only place quest progress is detected - no event patch.
            if ((DateTime.Now - _lastInventoryScanTime).TotalMilliseconds > INVENTORY_SCAN_INTERVAL_MS && _modSettingsAvailable)
            {
                ScanInventoryForQuestProgress();
                _lastInventoryScanTime = DateTime.Now;
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


        // Live-queries total units for a name - never holds a GearItem ref (can go stale/crash).
        // Stackable items (e.g. sticks, cloth) contribute their stack's unit count; non-stackable
        // items (tools, clothing, firearms, etc.) have no m_StackableItem, so each match = 1 unit.
        private int GetTotalUnitsInInventory(Inventory inventory, string itemName)
        {
            var matches = new Il2CppSystem.Collections.Generic.List<GearItem>();
            inventory.GetItems(itemName, matches);

            int total = 0;
            foreach (var item in matches)
            {
                if (item == null)
                    continue;

                total += (item.m_StackableItem != null) ? item.m_StackableItem.m_Units : 1;
            }
            return total;
        }

        // Sole quest-progress detection mechanism. Compares current total units of each quest
        // item (by name, across all stacks) against the last known count. Handles fresh pickups,
        // stack merges, and split stacks identically - it's just a number going up or down.
        private void ScanInventoryForQuestProgress()
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
                                MelonLogger.Msg($"[QuestMod] Item count increased: '{GetDisplayName(key)}' {lastKnown} -> {currentTotal}");
                            }

                            int unitsAdded = currentTotal - lastKnown;
                            for (int i = 0; i < unitsAdded; i++)
                            {
                                ProcessQuestItemPickup(key, verbose);
                            }
                        }
                        else if (currentTotal < lastKnown && verbose)
                        {
                            // Drop/consume/craft-away: not counted toward progress, but baseline
                            // still updates below or a later re-pickup wouldn't register.
                            MelonLogger.Msg($"[QuestMod] Item count decreased: '{GetDisplayName(key)}' {lastKnown} -> {currentTotal} (not counted)");
                        }

                        // Re-read the true current total rather than reusing the value captured
                        // at the top of this iteration: if a quest just completed above and its
                        // RewardKey is this same item name (self-reward, or a duplicate CollectKey
                        // across quests), GiveReward already synced the baseline to the correct
                        // post-reward total - overwriting with the stale pre-reward value here
                        // would make the reward units look like a fresh pickup next scan.
                        try
                        {
                            _trackedItemUnitsByName[key] = GetTotalUnitsInInventory(inventory, key);
                        }
                        catch
                        {
                            _trackedItemUnitsByName[key] = currentTotal;
                        }
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
                bool anyQuestReplaced = false;

                foreach (var quest in _questState.ActiveQuests.ToList())
                {
                    // If quest objective is complete, handle period end
                    if (quest.CurrentAmount >= quest.RequiredAmount && quest.Status == "Complete")
                    {
                        bool periodEnded = (day > quest.EndDay) || 
                                           (day == quest.EndDay && hour >= quest.EndHour);


                        
                        if (periodEnded && hour >= QUEST_START_HOUR)
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
                                anyQuestReplaced = true;
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


                    
                    if (questPeriodEnded && hour >= QUEST_START_HOUR)
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
                            anyQuestReplaced = true;
                        }
                    }
                }

                if (anyQuestReplaced)
                {
                    // Re-seed tracking baselines for the new active quest set - without this,
                    // a replacement quest for an item that was tracked earlier (then removed)
                    // would inherit a stale baseline instead of the player's true current
                    // amount, wrongly counting already-owned items as fresh pickups.
                    SeedInventoryTracking();
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
            if (_questState != null && _questState.DlcAutoDetected)
            {
                allowedCategories.Add("DLC");
            }
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

            // Quest window is always anchored to the fixed 9am-to-8:50am cycle, not a rolling
            // window from creation time. StartHour is nominal (the "cycle" begins at 9am);
            // EndDay/EndHour are what actually determine when the quest expires.
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
                    // Remove this exact quest object by reference (Quest is a class with no
                    // Equals override, so List.Remove does reference equality). Matching by
                    // Type/CollectKey/RequiredAmount/Status instead would risk grabbing a
                    // different quest that happens to share those values - e.g. one already
                    // sitting as Complete from an earlier natural completion this session -
                    // leaving THIS quest stuck displaying as Complete with no replacement.
                    _questState.ActiveQuests.Remove(quest);

                    // Generate replacement quest
                    GenerateQuestsAndNotify(quest.Type, 1, showCreationLog: false);
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
                            MelonLogger.Error($"[QuestMod] Failed to instantiate reward item: {quest.RewardKey} ({GetDisplayName(quest.RewardKey)})");
                        }
                    }

                    // Sync the tracking baseline for the reward item to its new true count.
                    // Without this, the next scan tick would see the reward units as a fresh
                    // "pickup" and credit them toward ANY active quest matching RewardKey
                    // (including a different quest that just happens to want the same item) -
                    // rewards should never count as collection progress.
                    if (successCount > 0)
                    {
                        try
                        {
                            lock (_trackingLock)
                            {
                                _trackedItemUnitsByName[quest.RewardKey] = GetTotalUnitsInInventory(inventory, quest.RewardKey);
                            }
                        }
                        catch (Exception trackEx)
                        {
                            MelonLogger.Error($"[QuestMod] Error syncing reward tracking baseline: {trackEx.Message}");
                        }
                    }

                    string rewardDisplay = $"{successCount} {quest.RewardKey}";
                    
                    // Display reward notification using HUDMessage (works everywhere, indoor/outdoor)
                    try
                    {
                        if (successCount > 0)
                        {
                            HUDMessage.AddMessage($"Quest Reward: {successCount}x {GetDisplayName(quest.RewardKey)}", true);
                            MelonLogger.Msg($"[QuestMod] Reward notification displayed: {successCount}x {GetDisplayName(quest.RewardKey)}");
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
            // If TimeOfDay isn't available yet, or we're not actually in a gameplay scene
            // (e.g. settings changed from the main menu before loading a save), defer instead
            // of generating with placeholder/main-menu day-1 data - confirmed via testing that
            // TOD can be non-null at the main menu but still report day=1,hour=0 instead of
            // the real save's data, only becoming accurate once actually loaded into a save.
            if (TOD == null || !IsInGameplayScene)
            {
                _needsInitialQuestGeneration = true;
                MelonLogger.Msg("[QuestMod] Not in a gameplay scene yet - deferring quest regeneration until we are");
                return;
            }

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

            // Defer the re-seed to OnUpdate instead of calling it directly here - this method
            // can be invoked from OnConfirm (a settings-menu UI callback), and every other
            // inventory/GameManager query in this mod deliberately only happens from the
            // normal OnUpdate loop, never from a UI callback context.
            _needsReseedAfterRegen = true;

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

                            // Record regardless of enabled - the name mapping should stay valid
                            // even for variants currently turned off in settings/JSON.
                            if (!string.IsNullOrEmpty(entry.spawn_name))
                                _spawnNameToDisplayName[entry.spawn_name] = item.Name;

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

                            // Record regardless of enabled - the name mapping should stay valid
                            // even for variants currently turned off in settings/JSON.
                            if (!string.IsNullOrEmpty(entry.spawn_name))
                                _spawnNameToDisplayName[entry.spawn_name] = item.Name;

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

        // Core quest-progress logic by item name, called once per unit gained, from the scanner
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
                            MelonLogger.Msg($"[QuestMod] Quest Item added to inventory: '{GetDisplayName(itemName)}'");
                            MelonLogger.Msg($"[QuestMod] Progress: {quest.Type} Quest: {GetDisplayName(quest.CollectKey)} - {quest.CurrentAmount}/{quest.RequiredAmount} (+1)");
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
                        MelonLogger.Msg($"[QuestMod] Item added to inventory: '{GetDisplayName(itemName)}'");
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

        // Friendly display name for a spawn_name (e.g. "GEAR_CookedBannock" -> "Bannock"),
        // taken from the JSON key in GearLookup.json. Falls back to stripping "GEAR_" if the
        // spawn_name isn't in the map (e.g. lookup not loaded yet, or JSON edited externally).
        public string GetDisplayName(string spawnName)
        {
            if (string.IsNullOrEmpty(spawnName))
                return spawnName;

            if (_spawnNameToDisplayName.TryGetValue(spawnName, out string displayName))
                return displayName;

            return spawnName.StartsWith("GEAR_") ? spawnName.Substring(5) : spawnName;
        }

        // Get the ShowReward setting from mod settings. Used by QuestModGUI to determine if rewards should be visible or hidden as *****.
        public bool GetShowRewardSetting()
        {
            return _settings?.ShowReward ?? false;
        }
    }

}