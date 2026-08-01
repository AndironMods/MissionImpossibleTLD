using System;
using System.Collections.Generic;
using System.Reflection;
using ModSettings;
using MelonLoader;

namespace MissionImpossible
{
    // Custom attribute to override how enum values are displayed in the settings menu
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
        [DisplayName("Easy")]
        Easy = 0,
        
        [DisplayName("Normal")]
        Normal = 1,
        
        [DisplayName("Hard")]
        Hard = 2
    }

    public class QuestModSettings : ModSettings.JsonModSettings
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
	[Description("Enable/Disable daily quests")]
        public bool EnableDailyQuests = true;
        
        // ==================== WEEKLY QUEST SETTINGS ====================
        [Name("Weekly Quest Count")]
        [Description("Number of active weekly quests")]
        [Slider(1f, 5f, 5)]
        public int WeeklyQuestCount = 2;
        
        [Name("Enable Weekly Quests")]
	[Description("Enable/Disable weekly quests")]
        public bool EnableWeeklyQuests = true;
        
        // ==================== MONTHLY QUEST SETTINGS ====================
        [Name("Monthly Quest Count")]
        [Description("Number of active monthly quests")]
        [Slider(1f, 5f, 5)]
        public int MonthlyQuestCount = 3;
        
        [Name("Enable Monthly Quests")]
	[Description("Enable/Disable montly quests")]
        public bool EnableMonthlyQuests = true;

        // ==================== CATEGORY FILTERS ====================
        [Name("Allow Clothing")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowClothing = true;
        
        [Name("Allow Food/Drink")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowFood = true;
        
        [Name("Allow Tools")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowTools = true;
        
        [Name("Allow FirstAid")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowFirstAid = true;
        
        [Name("Allow Material")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowResources = true;

        [Name("Allow DLC Content")]
	[Description("Enable/Disable will regenerate Quests")]
        public bool AllowDLC = false;

        // ==================== DISPLAY & LOGGING SETTINGS ====================
        [Name("Show Reward")]
        [Description("Show reward in GUI (hide if disabled)")]
        public bool ShowReward = false;
        
        [Name("Enable Logging")]
        [Description("Log quest related events")]
        public bool EnablePickupLogging = false;

        // ==================== INITIALIZATION ====================
        public void InitializeSettings()
        {
            AddToModSettings("Mission Impossible");
            ValidateConfiguration();
        }

        // Validate that configuration is in a valid state. At least one quest type must be enabled.
        public void ValidateConfiguration()
        {
            bool anyQuestEnabled = EnableDailyQuests || EnableWeeklyQuests || EnableMonthlyQuests;

            if (!anyQuestEnabled)
            {
                MelonLogger.Warning("[QuestModSettings] CONFIGURATION ERROR: All quest types are disabled!");
                MelonLogger.Warning("[QuestModSettings] At least one quest type must be enabled (Daily, Weekly, or Monthly).");
                MelonLogger.Warning("[QuestModSettings] Enabling Daily Quests as default...");
                
                // Auto-fix: enable Daily Quests
                EnableDailyQuests = true;
            }
        }

        // ==================== SETTINGS PERSISTENCE ====================

        // Required amount scaled by difficulty (0.5x/1.0x/2.0x)
        public int ApplyRequiredMultiplier(int baseRequired)
        {
            return DifficultyLevel switch
            {
                DifficultyLevel.Easy => Math.Max(1, (int)(baseRequired * 0.5f)),
                DifficultyLevel.Normal => baseRequired,
                DifficultyLevel.Hard => baseRequired * 2,
                _ => baseRequired
            };
        }

        // Calculate the reward amount based on difficulty setting
        public int ApplyRewardMultiplier(int baseReward)
        {
            return DifficultyLevel switch
            {
                DifficultyLevel.Easy => baseReward,
                DifficultyLevel.Normal => baseReward,
                DifficultyLevel.Hard => Math.Max(1, (int)(baseReward * 1.5f)),
                _ => baseReward
            };
        }

        // Get a human-readable description of the current difficulty level
        public string GetDifficultyDescription()
        {
            return DifficultyLevel switch
            {
                DifficultyLevel.Easy => "Easy (0.5x required)",
                DifficultyLevel.Normal => "Normal (1.0x)",
                DifficultyLevel.Hard => "Hard (2.0x required)",
                _ => "Unknown"
            };
        }

        // Get the list of allowed item categories based on current settings
        public List<string> GetAllowedCategories()
        {
            var allowedCategories = new List<string>();
            
            if (AllowClothing) allowedCategories.Add("Clothing");
            if (AllowFood) allowedCategories.Add("Food");
            if (AllowTools) allowedCategories.Add("Tools");
            if (AllowFirstAid) allowedCategories.Add("FirstAid");
            if (AllowResources) allowedCategories.Add("Resources");
            if (AllowDLC) allowedCategories.Add("DLC");
            
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
            
            // base.OnConfirm() calls Save() - required or settings never reach disk
            base.OnConfirm();
            
            MelonLogger.Msg("[QuestModSettings] Settings confirmed and applied");
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
                    nameof(AllowFirstAid),
                    nameof(AllowResources),
                    nameof(AllowDLC)
                };
                
                if (System.Array.Exists(questSettingNames, element => element == field.Name))
                {
                    _questSettingsChanged = true;
                }
            }
        }
    }
}