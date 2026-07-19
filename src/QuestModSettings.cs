using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Text.Json;
using ModSettings;
using MelonLoader;

namespace MissionImpossible
{
    public enum DifficultyLevel { Easy = 0, Normal = 1, Hard = 2 }

    public class QuestModSettings : ModSettingsBase
    {
        // ==================== DIFFICULTY SETTINGS ====================
        [Name("Difficulty Level")]
        [Description("Easy (0.5x required), Normal (1.0x), Hard (2.0x required)")]
        public DifficultyLevel DifficultyLevel = DifficultyLevel.Normal;
        
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
        
        [Name("Enable Detailed Logging")]
        [Description("Log detailed quest information")]
        public bool EnableDetailedLogging = true;
        
        [Name("Suppress Pickup Logging")]
        [Description("Don't log item pickup events")]
        public bool SuppressPickupLogging = true;

        private string _settingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TheLongDark", "Mods", "MissionImpossible", "QuestModSettings.json"
        );

        // ==================== INITIALIZATION ====================
        public void InitializeSettings()
        {
            AddToModSettings("Mission Impossible");
            LoadSettingsFromDisk();
            
            // Always save settings (creates file if it doesn't exist)
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

                    // Load all settings from JSON
                    if (root.TryGetProperty("DifficultyLevel", out var difficultyValue))
                        if (Enum.TryParse<DifficultyLevel>(difficultyValue.GetString(), out var diff))
                            DifficultyLevel = diff;

                    if (root.TryGetProperty("DailyQuestCount", out var dailyValue))
                        DailyQuestCount = dailyValue.GetInt32();

                    if (root.TryGetProperty("EnableDailyQuests", out var enableDailyValue))
                        EnableDailyQuests = enableDailyValue.GetBoolean();

                    if (root.TryGetProperty("WeeklyQuestCount", out var weeklyValue))
                        WeeklyQuestCount = weeklyValue.GetInt32();

                    if (root.TryGetProperty("EnableWeeklyQuests", out var enableWeeklyValue))
                        EnableWeeklyQuests = enableWeeklyValue.GetBoolean();

                    if (root.TryGetProperty("MonthlyQuestCount", out var monthlyValue))
                        MonthlyQuestCount = monthlyValue.GetInt32();

                    if (root.TryGetProperty("EnableMonthlyQuests", out var enableMonthlyValue))
                        EnableMonthlyQuests = enableMonthlyValue.GetBoolean();

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

                    if (root.TryGetProperty("ShowReward", out var showRewardValue))
                        ShowReward = showRewardValue.GetBoolean();
                    else if (root.TryGetProperty("HideReward", out var hideRewardValue))
                        // Backwards compatibility: invert HideReward to ShowReward
                        ShowReward = !hideRewardValue.GetBoolean();

                    if (root.TryGetProperty("EnableDetailedLogging", out var loggingValue))
                        EnableDetailedLogging = loggingValue.GetBoolean();

                    if (root.TryGetProperty("SuppressPickupLogging", out var suppressValue))
                        SuppressPickupLogging = suppressValue.GetBoolean();
                }

                MelonLogger.Msg($"[QuestModSettings] Settings loaded from: {_settingsPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModSettings] Error loading settings: {ex.Message}");
            }
        }

        // ==================== HELPER METHODS ====================
        public (float RequiredMultiplier, float RewardMultiplier) GetDifficultyMultipliers()
        {
            return DifficultyLevel switch
            {
                DifficultyLevel.Easy => (0.5f, 1.0f),    // Half requirements, same reward
                DifficultyLevel.Normal => (1.0f, 1.0f),  // No change
                DifficultyLevel.Hard => (2.0f, 1.0f),    // Double requirements, same reward
                _ => (1.0f, 1.0f)
            };
        }

        public string GetDifficultyDescription()
        {
            return DifficultyLevel switch
            {
                DifficultyLevel.Easy => "Pilgrim (Easy - 0.5x required)",
                DifficultyLevel.Normal => "Voyager/Stalker (Normal - 1.0x)",
                DifficultyLevel.Hard => "Interloper/Misery (Hard - 2.0x required)",
                _ => "Unknown"
            };
        }

        public List<string> GetAllowedCategories()
        {
            List<string> allowed = new();
            if (AllowClothing) allowed.Add("Clothing");
            if (AllowFood) allowed.Add("Food");
            if (AllowTools) allowed.Add("Tools");
            if (AllowAmmunition) allowed.Add("Ammunition");
            if (AllowResources) allowed.Add("Resources");
            return allowed;
        }

        public int ApplyRequiredMultiplier(int amount)
        {
            var (multiplier, _) = GetDifficultyMultipliers();
            return (int)Math.Ceiling(amount * multiplier);
        }

        public int ApplyRewardMultiplier(int amount)
        {
            var (_, multiplier) = GetDifficultyMultipliers();
            return (int)Math.Ceiling(amount * multiplier);
        }

        private bool _questSettingsChanged = false;

        // ==================== SETTINGS CALLBACKS ====================
        protected override void OnConfirm()
        {
            MelonLogger.Msg("[QuestModSettings] ========== SETTINGS CHANGE DETECTED ==========");
            MelonLogger.Msg($"[QuestModSettings] Daily Quests: {DailyQuestCount} (Enabled: {EnableDailyQuests})");
            MelonLogger.Msg($"[QuestModSettings] Weekly Quests: {WeeklyQuestCount} (Enabled: {EnableWeeklyQuests})");
            MelonLogger.Msg($"[QuestModSettings] Monthly Quests: {MonthlyQuestCount} (Enabled: {EnableMonthlyQuests})");
            MelonLogger.Msg($"[QuestModSettings] Difficulty: {DifficultyLevel}");
            
            // Save settings to disk
            SaveSettingsToDisk();
            
            // Only regenerate if quest-related settings actually changed
            if (_questSettingsChanged && QuestMod.Instance != null)
            {
                MelonLogger.Msg("[QuestModSettings] Quest settings changed - Regenerating quests...");
                QuestMod.Instance.RegenerateQuestsForSettingsChange();
                _questSettingsChanged = false;  // Reset flag
            }
            else if (!_questSettingsChanged)
            {
                MelonLogger.Msg("[QuestModSettings] Only logging settings changed - No quest regeneration needed");
            }
            
            MelonLogger.Msg("[QuestModSettings] Settings confirmed and applied");
            MelonLogger.Msg("[QuestModSettings] =============================================");
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

        // ==================== SETTINGS PERSISTENCE ====================
        private void SaveSettingsToDisk()
        {
            try
            {
                string dirPath = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(dirPath))
                    Directory.CreateDirectory(dirPath);

                // Manually serialize all public fields to JSON
                var settings = new Dictionary<string, object>
                {
                    { "DifficultyLevel", DifficultyLevel.ToString() },
                    { "DailyQuestCount", DailyQuestCount },
                    { "EnableDailyQuests", EnableDailyQuests },
                    { "WeeklyQuestCount", WeeklyQuestCount },
                    { "EnableWeeklyQuests", EnableWeeklyQuests },
                    { "MonthlyQuestCount", MonthlyQuestCount },
                    { "EnableMonthlyQuests", EnableMonthlyQuests },
                    { "AllowClothing", AllowClothing },
                    { "AllowFood", AllowFood },
                    { "AllowTools", AllowTools },
                    { "AllowAmmunition", AllowAmmunition },
                    { "AllowResources", AllowResources },
                    { "ShowReward", ShowReward },
                    { "EnableDetailedLogging", EnableDetailedLogging },
                    { "SuppressPickupLogging", SuppressPickupLogging }
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_settingsPath, json);
                
                MelonLogger.Msg($"[QuestModSettings] Settings saved to: {_settingsPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestModSettings] Error saving settings: {ex.Message}");
            }
        }
    }
}