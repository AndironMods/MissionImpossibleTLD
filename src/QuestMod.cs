using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MelonLoader;
using Il2Cpp;
using Il2CppTLD.Gear;
using UnityEngine;

[assembly: MelonInfo(typeof(MissionImpossible.QuestMod), "Mission Impossible", "1.5.0", "Andiron")]
[assembly: MelonGame("Hinterland", "TheLongDark")]
[assembly: MelonAdditionalDependencies("ModSettings", "ModData")]

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
    }

    public partial class QuestMod : MelonMod
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
        // True once ModSettings and ModData were found and the mod is fully set up. Everything that touches types
        // from those mods sits behind this flag (and in NoInlining methods): without them .NET fails to compile
        // such a method at all, and a failing OnUpdate would throw an error every single frame.
        private bool _modReady = false;
        public bool IsReady => _modReady;

        // Quest data lives in the save game (ModData); loaded when a save is entered, dropped in the main menu
        // Created in InitializeMod, after the required mods were found (the type needs ModData.dll)
        private QuestStore _store;
        private string _loadedSaveName;
        // Set when settings change while no save is loaded (main menu): the save loaded next gets fresh quests
        private bool _regenerateOnNextLoad = false;
        private float _nextSaveLoadCheck;
        private float _saveLoadWaitSince = -1f;
        private bool _saveLoadWaitLogged;
        public int QuestDataRevision => _store?.Revision ?? 0;
        // Set once a "_DLC01" scene is seen loading - Unity can't load a scene that doesn't exist, so this is a
        // reliable signal that DLC content is installed. Detected again every game start (the main menu is a DLC scene).
        private bool _dlcDetected = false;
        // Thread safety for console access to quest data
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

        // Checks that the required mods are installed before anything of them is touched
        public override void OnInitializeMelon()
        {
            Instance = this;

            string missing = FindMissingDependencies();
            if (missing != null)
            {
                MelonLogger.Error($"[QuestMod] Mission Impossible is disabled: {missing}");
                return;
            }

            InitializeMod();
        }

        // Message about the missing required mods (with download links), or null if all are there
        private static string FindMissingDependencies()
        {
            var names = new List<string>();
            var links = new List<string>();

            if (!IsAssemblyAvailable("ModSettings"))
            {
                names.Add("ModSettings.dll");
                links.Add("ModSettings: https://github.com/DigitalzombieTLD/ModSettings/");
            }

            if (!IsAssemblyAvailable("ModData"))
            {
                names.Add("ModData.dll");
                links.Add("ModData: https://github.com/dommrogers/ModData/releases");
            }

            if (names.Count == 0)
                return null;

            return $"{string.Join(" and ", names)} {(names.Count == 1 ? "is" : "are")} required but not found in the Mods folder. " +
                   $"Download: {string.Join("  ", links)}";
        }

        private static bool IsAssemblyAvailable(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            try
            {
                System.Reflection.Assembly.Load(new System.Reflection.AssemblyName(name));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Load settings and lookup data, set up the GUI. Only called when ModSettings and ModData are present.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InitializeMod()
        {
            try
            {
                _store = new QuestStore();
                _settings = new QuestModSettings();
                _settings.InitializeSettings();
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Mission Impossible is disabled: error registering settings: {ex.Message}");
                return;
            }

            _modSettingsAvailable = true;

            LoadLookup();

            // Initialize GUI system to display quests in Journal/Collections
            new QuestModGUI();

            _modReady = true;

            // Quest data is loaded per save game once a save is entered (see EnsureSaveLoaded)
            MelonLogger.Msg($"[QuestMod] Initialized - Difficulty: {_settings.GetDifficultyDescription()}");
        }

        public override void OnApplicationQuit()
        {
            Instance = null;

            if (_modReady)
                MelonLogger.Msg("[QuestMod] Shutdown complete.");
        }

        // Handle scene loading - suppress initial inventory logging spam
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (!_modReady)
                return;

            MelonLogger.Msg($"[QuestMod] Scene loaded: {sceneName}");
            _currentSceneName = sceneName;
            _suppressLogging = true;
            _lastCheckTime = DateTime.Now;

            CheckForDlcAutoEnable(sceneName);

            // The quest data belongs to the save game: drop it in the main menu, it is loaded again with the next save
            if (sceneName.Contains("MainMenu", StringComparison.OrdinalIgnoreCase))
                UnloadSaveData();
        }

        private void UnloadSaveData()
        {
            _questState = null;
            _loadedSaveName = null;
            _store.Reset();
            lock (_trackingLock)
            {
                _trackedItemUnitsByName.Clear();
            }
            _rewardQueue.Clear();
            _messagesAfterRewards.Clear();
            _needsInitialQuestGeneration = false;
            _saveLoadWaitSince = -1f;
            _saveLoadWaitLogged = false;
        }

        // Loads the quest data of the active save the first time a gameplay scene is entered
        private void EnsureSaveLoaded()
        {
            if (_questState != null || !IsInGameplayScene)
                return;

            // The name is only needed until the data is loaded; asking once per second is plenty
            if (Time.unscaledTime < _nextSaveLoadCheck)
                return;
            _nextSaveLoadCheck = Time.unscaledTime + 1f;

            string saveName = SaveGameSystem.GetCurrentSaveName();
            if (string.IsNullOrEmpty(saveName))
            {
                if (_saveLoadWaitSince < 0f)
                    _saveLoadWaitSince = Time.unscaledTime;

                if (!_saveLoadWaitLogged && Time.unscaledTime - _saveLoadWaitSince > 10f)
                {
                    _saveLoadWaitLogged = true;
                    MelonLogger.Warning("[QuestMod] The game reports no save game name - quests are loaded as soon as it does.");
                }
                return;
            }

            _saveLoadWaitSince = -1f;
            _saveLoadWaitLogged = false;
            _loadedSaveName = saveName;
            _questState = _store.Load();

            // A new save has no quests yet; a settings change made in the main menu also regenerates the quests of this save
            _needsInitialQuestGeneration = _regenerateOnNextLoad || _questState.ActiveQuests.Count == 0;
            _regenerateOnNextLoad = false;

            MelonLogger.Msg($"[QuestMod] Loaded quest data of save '{saveName}' - {_questState.ActiveQuests.Count} quests active");
        }

        // Called right before the game writes its save, so ModData stores the latest quest state with it
        internal void OnBeforeGameSave()
        {
            if (!_modReady)
                return;

            _store.Commit(_questState);
        }

        // A "_DLC01" scene can only load if the DLC is installed - Unity can't load a scene
        // that doesn't exist on disk. DLC content is force-enabled from this point on - there
        // is no player-facing toggle.
        private void CheckForDlcAutoEnable(string sceneName)
        {
            if (_dlcDetected || string.IsNullOrEmpty(sceneName))
                return;

            if (sceneName.IndexOf("_DLC01", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            _dlcDetected = true;

            MelonLogger.Msg($"[QuestMod] DLC content detected (scene: {sceneName}) - DLC items now included in quest generation.");

            // Only regenerate if quests of a loaded save exist - otherwise generation will include DLC items anyway.
            if (_questState != null && !_needsInitialQuestGeneration)
            {
                RegenerateQuestsForSettingsChange(showCreationLogs: true);
            }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (!_modReady)
                return;

            MelonLogger.Msg($"[QuestMod] Scene unloaded: {sceneName}");
        }

        // Main loop: quest resets, stack detection (all non-blocking)
        public override void OnUpdate()
        {
            if (_modReady)
                UpdateMod();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateMod()
        {
            EnsureSaveLoaded();
            ProcessRewardQueue();

            // End suppress window, then seed tracking baseline once inventory has settled
            if (_suppressLogging && (DateTime.Now - _lastCheckTime).TotalSeconds > 5)
            {
                _suppressLogging = false;
                SeedInventoryTracking();
            }

            // Deferred quest generation - fresh save, or a settings change that came in before
            // we were actually in a gameplay scene. TOD != null alone is NOT enough - confirmed
            // via testing that TimeOfDay can be non-null at the main menu with placeholder data.
            if (_needsInitialQuestGeneration && _modSettingsAvailable && _questState != null && TOD != null && IsInGameplayScene && !_suppressLogging)
            {
                // Set false before the risky call so a failure here can't cause a retry loop
                _needsInitialQuestGeneration = false;
                try
                {
                    RegenerateQuestsForSettingsChange();
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

            // F2/F3/F4: Complete all daily/weekly/monthly quests (debug commands - only active when logging is enabled)
            if (_modSettingsAvailable && _settings.EnablePickupLogging)
            {
                if (Input.GetKeyDown(KeyCode.F2)) CompleteAllQuestsDebug("Daily");
                if (Input.GetKeyDown(KeyCode.F3)) CompleteAllQuestsDebug("Weekly");
                if (Input.GetKeyDown(KeyCode.F4)) CompleteAllQuestsDebug("Monthly");
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

                        // Reward items are queued and sync their own baseline when they are handed out
                        // (ProcessRewardQueue), so the total read at the top of this iteration is still correct here.
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
                            
                            _store.MarkDirty();
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

                _store.MarkDirty();
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

            _store.MarkDirty();
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
            if (_dlcDetected)
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
                HUDMessage.AddMessage($"New {questType.ToLowerInvariant()} quests created!", true);
            }
        }

        // Debug: complete every active quest of a type at once, then show one summary and one "new quests" message
        private void CompleteAllQuestsDebug(string questType)
        {
            if (_questState == null)
                return;

            var quests = _questState.ActiveQuests.Where(q => q.Type == questType).ToList();
            if (quests.Count == 0)
                return;

            MelonLogger.Msg($"[QuestMod] All {questType} Quests completed!");
            try { HUDMessage.AddMessage($"All {questType.ToLowerInvariant()} quests completed!", true); }
            catch (Exception ex) { MelonLogger.Msg($"[QuestMod] Note: Could not display completion notification (non-critical): {ex.Message}"); }

            int questNumber = 1;
            foreach (var quest in quests)
            {
                quest.CurrentAmount = quest.RequiredAmount;
                CheckAndCompleteQuest(quest, isDebugCommand: true, questNumber: questNumber);
                questNumber++;
            }

            if (_questState.ActiveQuests.Any(q => q.Type == questType && !quests.Contains(q)))
            {
                // Shown after the rewards, which are handed out over the next frames
                _messagesAfterRewards.Enqueue($"New {questType.ToLowerInvariant()} quests created!");
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

                // Debug commands show one "All ... completed" message for the whole batch instead
                if (!isDebugCommand)
                {
                    try
                    {
                        string completionMessage = targetCount > 0 && completedCount >= targetCount
                            ? $"All {quest.Type.ToLowerInvariant()} quests completed!"
                            : $"{completedCount}/{targetCount} {quest.Type} Quests completed!";
                        HUDMessage.AddMessage(completionMessage, true);
                    }
                    catch (Exception displayEx)
                    {
                        MelonLogger.Msg($"[QuestMod] Note: Could not display completion notification (non-critical): {displayEx.Message}");
                    }
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
                _store.MarkDirty();
            }
        }

        private void GiveReward(Quest quest, int questNumber = 0)
        {
            GrantRewardItems(quest.RewardKey, quest.RewardAmount, "Quest Reward");
        }

        private class PendingReward
        {
            public string Key;
            public int Remaining;
            public int Given;
            public string NotificationPrefix;
        }

        // Rewards are handed out a few items per frame so a large amount can't freeze the game
        private const int REWARD_ITEMS_PER_FRAME = 5;
        private readonly Queue<PendingReward> _rewardQueue = new Queue<PendingReward>();
        // HUD messages that belong after the rewards have been handed out
        private readonly Queue<string> _messagesAfterRewards = new Queue<string>();

        // Queues reward items; ProcessRewardQueue hands them out from OnUpdate
        private void GrantRewardItems(string rewardKey, int rewardAmount, string notificationPrefix)
        {
            if (string.IsNullOrEmpty(rewardKey) || rewardAmount <= 0)
                return;

            _rewardQueue.Enqueue(new PendingReward { Key = rewardKey, Remaining = rewardAmount, NotificationPrefix = notificationPrefix });
        }

        private static void ShowHudMessage(string message)
        {
            try
            {
                HUDMessage.AddMessage(message, true);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[QuestMod] Note: Could not display notification (non-critical): {ex.Message}");
            }
        }

        // HANG PREVENTION: at most REWARD_ITEMS_PER_FRAME items per frame, no file I/O
        private void ProcessRewardQueue()
        {
            if (_rewardQueue.Count == 0 && _messagesAfterRewards.Count == 0)
                return;

            if (!IsInGameplayScene)
                return;

            try
            {
                if (_rewardQueue.Count > 0)
                {
                    var inventory = GameManager.GetInventoryComponent();
                    if (inventory == null)
                        return;

                    lock (_dataLock)
                    {
                        int budget = REWARD_ITEMS_PER_FRAME;
                        while (budget > 0 && _rewardQueue.Count > 0)
                        {
                            PendingReward reward = _rewardQueue.Peek();
                            int batch = Math.Min(budget, reward.Remaining);
                            int added = 0;

                            for (int i = 0; i < batch; i++)
                            {
                                GearItem rewardItem = GearItem.InstantiateGearItem(reward.Key);
                                if (rewardItem != null)
                                {
                                    inventory.AddGear(rewardItem);
                                    added++;
                                }
                            }

                            reward.Remaining -= batch;
                            reward.Given += added;
                            budget -= batch;

                            if (added < batch)
                                MelonLogger.Error($"[QuestMod] Failed to instantiate reward item: {reward.Key} ({GetDisplayName(reward.Key)})");

                            // Sync the tracking baseline for the reward item to its new true count.
                            // Without this, the next scan tick would see the reward units as a fresh
                            // "pickup" and credit them toward ANY active quest matching the reward item.
                            if (added > 0)
                            {
                                try
                                {
                                    lock (_trackingLock)
                                    {
                                        _trackedItemUnitsByName[reward.Key] = GetTotalUnitsInInventory(inventory, reward.Key);
                                    }
                                }
                                catch (Exception trackEx)
                                {
                                    MelonLogger.Error($"[QuestMod] Error syncing reward tracking baseline: {trackEx.Message}");
                                }
                            }

                            if (reward.Remaining <= 0)
                            {
                                _rewardQueue.Dequeue();

                                if (reward.Given > 0)
                                {
                                    ShowHudMessage($"{reward.NotificationPrefix}: {reward.Given}x {GetDisplayName(reward.Key)}");
                                    MelonLogger.Msg($"[QuestMod] Reward notification displayed: {reward.Given}x {GetDisplayName(reward.Key)}");
                                }
                            }
                        }
                    }
                }

                if (_rewardQueue.Count == 0)
                {
                    while (_messagesAfterRewards.Count > 0)
                        ShowHudMessage(_messagesAfterRewards.Dequeue());
                }
            }
            catch (Exception ex)
            {
                // Drop the reward that failed, otherwise it would fail again every frame
                if (_rewardQueue.Count > 0)
                    _rewardQueue.Dequeue();

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
            if (_questState == null)
            {
                _regenerateOnNextLoad = true;
                MelonLogger.Msg("[QuestMod] No save loaded - quests are regenerated as soon as a save is loaded");
                return;
            }

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
                _store.MarkDirty();
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

            _store.MarkDirty();
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

                ParseAchievementRewards(root);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error loading GearLookup: {ex.Message}");
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

                        // Already complete: further units don't count, the counter stays at the required amount
                        if (quest.Status == "Complete")
                            continue;

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
                                MelonLogger.Msg($"[QuestMod] {quest.Type} Quest objective complete! Reward queued.");
                            }
                        }

                        _store.MarkDirty();
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

        // Caches the resolved GearItemData (not a live GearItem/GameObject) per spawn_name, so
        // the one-time instantiate+destroy cost is paid at most once per unique item per session.
        private Dictionary<string, GearItemData> _spawnNameToGearItemData = new Dictionary<string, GearItemData>(System.StringComparer.OrdinalIgnoreCase);

        // Resolves and caches the GearItemData for a spawn_name by briefly instantiating the
        // prefab and reading its data reference back off, then destroying the instance. A null
        // cache entry (failed resolution) is also stored, so a bad spawn_name only costs once.
        private GearItemData GetGearItemDataFor(string spawnName)
        {
            if (_spawnNameToGearItemData.TryGetValue(spawnName, out GearItemData cached))
                return cached;

            GearItemData data = null;
            try
            {
                GearItem probe = GearItem.InstantiateGearItem(spawnName);
                if (probe != null)
                {
                    data = probe.GearItemData;
                    UnityEngine.Object.Destroy(probe.gameObject);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QuestMod] Failed to resolve GearItemData for '{spawnName}': {ex.Message}");
            }

            _spawnNameToGearItemData[spawnName] = data;
            return data;
        }

        private readonly HashSet<string> _nameLookupWarned = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // Real, current-language display name for a spawn_name (e.g. "GEAR_CookedBannock" -> the game's own
        // localized name for that item). Resolved with the game's own lookup (GearItem.GetGearDisplayName), which
        // creates no object. Falls back to briefly instantiating the item, then to the JSON-key name, then to
        // stripping "GEAR_" (e.g. an unrecognized spawn_name, or called before the asset system is ready).
        public string GetDisplayName(string spawnName)
        {
            if (string.IsNullOrEmpty(spawnName))
                return spawnName;

            try
            {
                string fast = GearItem.GetGearDisplayName(spawnName);
                if (!string.IsNullOrEmpty(fast) && !fast.Equals(spawnName, StringComparison.OrdinalIgnoreCase)
                    && !fast.StartsWith("GAMEPLAY_", StringComparison.Ordinal) && !fast.StartsWith("GEAR_", StringComparison.Ordinal))
                {
                    return fast;
                }
            }
            catch (Exception ex)
            {
                if (_nameLookupWarned.Add(spawnName))
                    MelonLogger.Warning($"[QuestMod] GetGearDisplayName failed for '{spawnName}': {ex.Message}");
            }

            try
            {
                GearItemData data = GetGearItemDataFor(spawnName);
                if (data != null)
                {
                    string localized = data.DisplayName;
                    if (!string.IsNullOrEmpty(localized))
                        return localized;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[QuestMod] Error reading localized DisplayName for '{spawnName}': {ex.Message}");
            }

            if (_spawnNameToDisplayName.TryGetValue(spawnName, out string jsonName))
                return jsonName;

            return spawnName.StartsWith("GEAR_") ? spawnName.Substring(5) : spawnName;
        }

        // Get the ShowReward setting from mod settings. Used by QuestModGUI to determine if rewards should be visible or hidden as *****.
        public bool GetShowRewardSetting()
        {
            return _settings?.ShowReward ?? false;
        }
    }

}