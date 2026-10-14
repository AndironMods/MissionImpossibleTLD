using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace MissionImpossible
{
    public class AchievementRewardEntry
    {
        public List<string> enabledQuestTypes { get; } = new List<string>();
    }

    public partial class QuestMod
    {
        private readonly Dictionary<string, AchievementRewardEntry> _achievementRewards =
            new Dictionary<string, AchievementRewardEntry>(StringComparer.OrdinalIgnoreCase);

        // Reads the "achievements" section of GearLookup.json.
        private void ParseAchievementRewards(JsonElement root)
        {
            _achievementRewards.Clear();

            if (!root.TryGetProperty("achievements", out JsonElement section))
            {
                MelonLogger.Warning("[QuestMod] No \"achievements\" section in GearLookup.json - achievement rewards disabled.");
                return;
            }

            foreach (JsonProperty item in section.EnumerateObject())
            {
                var entry = new AchievementRewardEntry();
                foreach (JsonElement variant in item.Value.EnumerateArray())
                {
                    if (!variant.GetProperty("enabled").GetBoolean())
                        continue;

                    string questType = variant.GetProperty("quest_type").GetString();
                    if (IsQuestType(questType))
                        entry.enabledQuestTypes.Add(questType);
                    else
                        MelonLogger.Warning($"[QuestMod] Achievement '{item.Name}' has unknown quest_type '{questType}' (use daily, weekly or monthly) - ignored.");
                }

                _achievementRewards[item.Name] = entry;
            }

            MelonLogger.Msg($"[QuestMod] Loaded {_achievementRewards.Count} achievement reward entries");
        }

        private static bool IsQuestType(string value)
        {
            return string.Equals(value, "daily", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "weekly", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "monthly", StringComparison.OrdinalIgnoreCase);
        }

        internal void OnAchievementUnlocked(Achievement achievement)
        {
            try
            {
                if (_settings == null || !_settings.EnableAchievementRewards)
                    return;

                string name = achievement.ToString();
                if (!_achievementRewards.TryGetValue(name, out AchievementRewardEntry entry) || entry.enabledQuestTypes.Count == 0)
                    return;

                if (_lookup?.items_as_reward == null)
                    return;

                string questType = entry.enabledQuestTypes[_random.Next(entry.enabledQuestTypes.Count)];

                List<string> allowedCategories = _settings.GetAllowedCategories();
                if (_dlcDetected)
                    allowedCategories.Add("DLC");

                var pool = _lookup.items_as_reward
                    .Select(kvp => kvp.Value.FirstOrDefault(v => v.enabled
                        && v.quest_type.Equals(questType, StringComparison.OrdinalIgnoreCase)
                        && allowedCategories.Contains(v.category)))
                    .Where(v => v != null)
                    .ToList();

                if (pool.Count == 0)
                {
                    MelonLogger.Warning($"[QuestMod] No {questType} reward items available for achievement '{name}'.");
                    return;
                }

                GearEntry reward = pool[_random.Next(pool.Count)];
                int amount = _settings.ApplyRewardMultiplier(reward.reward_amount);

                // Only queued here; handed out from OnUpdate, never inside the game's unlock call
                GrantRewardItems(reward.spawn_name, amount, "Achievement Reward");
                MelonLogger.Msg($"[QuestMod] Achievement unlocked: {name} ({questType}) - reward queued: {amount}x {reward.spawn_name}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error handling achievement unlock: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(AchievementPlatformManager), nameof(AchievementPlatformManager.Unlock))]
    internal static class AchievementUnlockPatch
    {
        private static void Prefix(Achievement achievement, out bool __state)
        {
            __state = true;
            try { __state = AchievementPlatformManager.IsUnlocked(achievement); }
            catch { }
        }

        private static void Postfix(Achievement achievement, bool __state)
        {
            try
            {
                if (!__state && AchievementPlatformManager.IsUnlocked(achievement))
                    QuestMod.Instance?.OnAchievementUnlocked(achievement);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Achievement unlock hook failed: {ex.Message}");
            }
        }
    }
}
