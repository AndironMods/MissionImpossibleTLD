using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Text.Json;
using ModSettings;
using MelonLoader;

namespace MissionImpossible
{
    /// <summary>
    /// Custom attribute to override how enum values are displayed in the settings menu
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class DisplayNameAttribute : Attribute
    {
        public string DisplayName { get; set; }

        public DisplayNameAttribute(string displayName)
        {
            DisplayName = displayName;
        }
    }

    public enum DifficultyLevel
    {
        [DisplayName("Pilgrim")]
        Pilgrim = 0,
        
        [DisplayName("Stalker and Voyager")]
        Stalker_Voyager = 1,
        
        [DisplayName("Interloper and Misery")]
        Interloper_Misery = 2
    }

    public class QuestModSettings : ModSettingsBase
    {
        // ==================== DIFFICULTY SETTINGS ====================
        [Name("Difficulty Level")]
        [Description("Pilgrim (0.5x required), Stalker and Voyager (1.0x), Interloper and Misery (2.0x required)")]
        public DifficultyLevel DifficultyLevel = DifficultyLevel.Stalker_Voyager;
        
        // ==================== DAILY QUEST SETTINGS ====================
        [Name("Daily Quest Count")]
        [Description("Number of active daily quests")]
        [Slider(1f, 5f, 5)]
        public int DailyQuestCount = 1;
        
        [Name("Enable Daily Quests")]
        public bool EnableDailyQuests = true;
        
        // ==================== WEEKLY QUEST SETTINGS ====================
        [Name("Weekly Quest Count")]
        [Description("Number of active weekly quests")]
        [Slider(1f, 5f, 5)]
        public int WeeklyQuestCount = 2;
        
        [Name("Enable Weekly Quests")]
        public bool EnableWeeklyQuests = true;
        
        // ==================== MONTHLY QUEST SETTINGS ====================
        [Name("Monthly Quest Count")]
        [Description("Number of active monthly quests")]
        [Slider(1f, 5f, 5)]
        public int MonthlyQuestCount = 3;
        
        [Name("Enable Monthly Quests")]
        public bool EnableMonthlyQuests = true;

        // ==================== CATEGORY FILTERS ====================
        [Name("Allow Clothing")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowClothing = true;
        
        [Name("Allow Food")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowFood = true;
        
        [Name("Allow Tools")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowTools = true;
        
        [Name("Allow Ammunition")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowAmmunition = true;
        
        [Name("Allow Resources")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowResources = true;

        // ==================== DISPLAY & LOGGING SETTINGS ====================
        [Name("Show Reward")]
        [Description("Show reward amounts in console log (hide *** if disabled)")]
        public bool ShowReward = false;
        
        [Name("Enable Pickup Logging")]
        [Description("Log item pickup events")]
        public bool EnablePickupLogging = false;

        private string _settingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TheLongDark", "Mods", "MissionImpossible", "QuestModSettings.json"
        );

        // ==================== UTILITY METHODS ====================
        /// <summary>
        /// Get the display name of an enum value using the DisplayNameAttribute
        /// </summary>
        public static string GetEnumDisplayName(Enum value)
        {
            var fieldInfo = value.GetType().GetField(value.ToString());
            if (fieldInfo == null)
                return value.ToString();

            var attribute = fieldInfo.GetCustomAttribute<DisplayNameAttribute>();
            return attribute?.DisplayName ?? value.ToString();
        }

        // ==================== INITIALIZATION ====================
        public void InitializeSettings()
        {
            AddToModSettings("Mission Impossible");
            LoadSettingsFromDisk();
            SaveSettingsToDisk();
        }

        // ==================== SETTINGS PERSISTENCE ====================
        private void LoadSettingsFromDisk()
        {
            if (!File.Exists(_settingsPath))
            {
                MelonLogger.Msg($"[QuestModSettings] No saved settings found at: {_settingsPath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(_settingsPath);
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    // Load difficulty level
                    if (root.TryGetProperty("DifficultyLevel", out var difficultyValue))
                        if (Enum.TryParse<DifficultyLevel>(difficultyValue.GetString(), out var diff))
                            DifficultyLevel = diff;

                    // Load daily quest settings
                    if (root.TryGetProperty("DailyQuestCount", out var dailyValue))
                        DailyQuestCount = dailyValue.GetInt32();

                    if (root.TryGetProperty("EnableDailyQuests", out var enableDailyValue))
                        EnableDailyQuests = enableDailyValue.GetBoolean();

                    // Load weekly quest settings
                    if (root.TryGetProperty("WeeklyQuestCount", out var weeklyValue))
                        WeeklyQuestCount = weeklyValue.GetInt32();

                    if (root.TryGetProperty("EnableWeeklyQuests", out var enableWeeklyValue))
                        EnableWeeklyQuests = enableWeeklyValue.GetBoolean();

                    // Load monthly quest settings
                    if (root.TryGetProperty("MonthlyQuestCount", out var monthlyValue))
                        MonthlyQuestCount = monthlyValue.GetInt32();

                    if (root.TryGetProperty("EnableMonthlyQuests", out var enableMonthlyValue))
                        EnableMonthlyQuests = enableMonthlyValue.GetBoolean();

                    // Load category filters
                    if (root.TryGetProperty("AllowClothing", out var clothingValue))
                        AllowClothing = clothingValue.GetBoolean();

                    if (root.TryGetProperty("AllowFood", out var foodValue))
                        AllowFood = foodValue.GetBoolean();

                    if (root.TryGetProperty("AllowTools", out var toolsValue))
                        AllowTools = toolsValue.GetBoolean();

                    if (root.TryGetProperty("AllowAmmunition", out var ammValue))
                        AllowAmmunition = ammValue.GetBoolean();

                    if (root.TryGetProperty("AllowResources", out var resValue))
                        AllowResources = resValue.GetBoolean();

                    // Load display/logging settings
                    if (root.TryGetProperty("ShowReward", out var showRewardValue))
                        ShowReward = showRewardValue.GetBoolean();

                    if (root.TryGetProperty("EnablePickupLogging", out var enablePickupValue))
                        EnablePickupLogging = enablePickupValue.GetBoolean();
                }

                MelonLogger.Msg($"[QuestModSettings] Settings loaded from: {_settingsPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModSettings] Error loading settings: {ex.Message}");
            }
        }

        private void SaveSettingsToDisk()
        {
            try
            {
                var settings = new Dictionary<string, object>
                {
                    { nameof(DifficultyLevel), DifficultyLevel.ToString() },
                    { nameof(DailyQuestCount), DailyQuestCount },
                    { nameof(EnableDailyQuests), EnableDailyQuests },
                    { nameof(WeeklyQuestCount), WeeklyQuestCount },
                    { nameof(EnableWeeklyQuests), EnableWeeklyQuests },
                    { nameof(MonthlyQuestCount), MonthlyQuestCount },
                    { nameof(EnableMonthlyQuests), EnableMonthlyQuests },
                    { nameof(AllowClothing), AllowClothing },
                    { nameof(AllowFood), AllowFood },
                    { nameof(AllowTools), AllowTools },
                    { nameof(AllowAmmunition), AllowAmmunition },
                    { nameof(AllowResources), AllowResources },
                    { nameof(ShowReward), ShowReward },
                    { nameof(EnablePickupLogging), EnablePickupLogging }
                };

                string dirPath = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dirPath))
                    Directory.CreateDirectory(dirPath);

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error saving settings to disk: {ex.Message}");
            }
        }

        // ==================== DIFFICULTY CALCULATIONS ====================
        /// <summary>
        /// Calculate the required amount based on difficulty setting (0.5x, 1.0x, 2.0x)
        /// </summary>
        public int ApplyRequiredMultiplier(int baseRequired)
        {
            return DifficultyLevel switch
            {
                DifficultyLevel.Pilgrim => Math.Max(1, (int)(baseRequired * 0.5f)),
                DifficultyLevel.Stalker_Voyager => baseRequired,
                DifficultyLevel.Interloper_Misery => baseRequired * 2,
                _ => baseRequired
            };
        }

        /// <summary>
        /// Calculate the reward amount based on difficulty setting
        /// </summary>
        public int ApplyRewardMultiplier(int baseReward)
        {
            return DifficultyLevel switch
            {
                DifficultyLevel.Pilgrim => baseReward,
                DifficultyLevel.Stalker_Voyager => baseReward,
                DifficultyLevel.Interloper_Misery => Math.Max(1, (int)(baseReward * 1.5f)),
                _ => baseReward
            };
        }

        /// <summary>
        /// Get a human-readable description of the current difficulty level
        /// </summary>
        public string GetDifficultyDescription()
        {
            return DifficultyLevel switch
            {
                DifficultyLevel.Pilgrim => "Pilgrim (0.5x required)",
                DifficultyLevel.Stalker_Voyager => "Stalker and Voyager (1.0x)",
                DifficultyLevel.Interloper_Misery => "Interloper and Misery (2.0x required)",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Get the list of allowed item categories based on current settings
        /// </summary>
        public List<string> GetAllowedCategories()
        {
            var allowedCategories = new List<string>();
            
            if (AllowClothing) allowedCategories.Add("Clothing");
            if (AllowFood) allowedCategories.Add("Food");
            if (AllowTools) allowedCategories.Add("Tools");
            if (AllowAmmunition) allowedCategories.Add("Ammunition");
            if (AllowResources) allowedCategories.Add("Resources");
            
            return allowedCategories;
        }

        private bool _questSettingsChanged = false;

        // ==================== SETTINGS CALLBACKS ====================
        protected override void OnConfirm()
        {
            MelonLogger.Msg("[QuestModSettings] ========== SETTINGS CHANGE DETECTED ==========");
            MelonLogger.Msg($"[QuestModSettings] Daily Quests: {DailyQuestCount} (Enabled: {EnableDailyQuests})");
            MelonLogger.Msg($"[QuestModSettings] Weekly Quests: {WeeklyQuestCount} (Enabled: {EnableWeeklyQuests})");
            MelonLogger.Msg($"[QuestModSettings] Monthly Quests: {MonthlyQuestCount} (Enabled: {EnableMonthlyQuests})");
            MelonLogger.Msg($"[QuestModSettings] Difficulty: {GetDifficultyDescription()}");
            
            // Save settings to disk
            SaveSettingsToDisk();

	    //Load settings from disk
	    LoadSettingsFromDisk();
            
            // Only regenerate if quest-related settings actually changed
            if (_questSettingsChanged && QuestMod.Instance != null)
            {
                MelonLogger.Msg("[QuestModSettings] Quest settings changed - Regenerating quests...");
                QuestMod.Instance.RegenerateQuestsForSettingsChange(showCreationLogs: true);
                _questSettingsChanged = false;  // Reset flag
            }
            else if (!_questSettingsChanged)
            {
                MelonLogger.Msg("[QuestModSettings] Only logging settings changed - No quest regeneration needed");
            }
            
            MelonLogger.Msg("[QuestModSettings] Settings confirmed and applied");
            //MelonLogger.Msg("[QuestModSettings] =============================================");
        }

        protected override void OnChange(FieldInfo field, object oldValue, object newValue)
        {
            if (field != null)
            {
                MelonLogger.Msg($"[QuestModSettings] Setting changed: {field.Name} = {newValue}");
                
                // Track if any quest-related setting changed
                string[] questSettingNames = new[]
                {
                    nameof(DifficultyLevel),
                    nameof(DailyQuestCount),
                    nameof(EnableDailyQuests),
                    nameof(WeeklyQuestCount),
                    nameof(EnableWeeklyQuests),
                    nameof(MonthlyQuestCount),
                    nameof(EnableMonthlyQuests),
                    nameof(AllowClothing),
                    nameof(AllowFood),
                    nameof(AllowTools),
                    nameof(AllowAmmunition),
                    nameof(AllowResources)
                };
                
                if (System.Array.Exists(questSettingNames, element => element == field.Name))
                {
                    _questSettingsChanged = true;
                }
            }
        }
    }
}