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
        public string quest_type { get; set; }
        public bool enabled { get; set; }
    }

    public partial class QuestMod
    {
        private readonly Dictionary<string, AchievementRewardEntry> _achievementRewards =
            new Dictionary<string, AchievementRewardEntry>(StringComparer.OrdinalIgnoreCase);

        // Rewards are queued from the unlock hook and handed out in OnUpdate, never inside the game's unlock call.
        private readonly Queue<(string spawnName, int amount, string achievementName)> _pendingAchievementRewards =
            new Queue<(string, int, string)>();

        private string AchievementLookupPath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Mods", "AchievementLookup.json");

        private void LoadAchievementLookup()
        {
            _achievementRewards.Clear();

            if (!File.Exists(AchievementLookupPath))
            {
                MelonLogger.Warning($"[QuestMod] AchievementLookup.json not found at {AchievementLookupPath} - achievement rewards disabled.");
                return;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(AchievementLookupPath));

                if (doc.RootElement.TryGetProperty("achievements", out JsonElement section))
                {
                    foreach (JsonProperty item in section.EnumerateObject())
                    {
                        _achievementRewards[item.Name] = new AchievementRewardEntry
                        {
                            quest_type = item.Value.GetProperty("quest_type").GetString(),
                            enabled = item.Value.GetProperty("enabled").GetBoolean()
                        };
                    }
                }

                MelonLogger.Msg($"[QuestMod] Loaded {_achievementRewards.Count} achievement reward entries");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error loading AchievementLookup: {ex.Message}");
            }
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
                if (!_achievementRewards.TryGetValue(name, out AchievementRewardEntry entry) || !entry.enabled)
                    return;

                if (_lookup?.items_as_reward == null)
                    return;

                if (!IsQuestType(entry.quest_type))
                {
                    MelonLogger.Warning($"[QuestMod] Achievement '{name}' has unknown quest_type '{entry.quest_type}' (use daily, weekly or monthly) - no reward.");
                    return;
                }

                List<string> allowedCategories = _settings.GetAllowedCategories();
                if (_questState != null && _questState.DlcAutoDetected)
                    allowedCategories.Add("DLC");

                var pool = _lookup.items_as_reward
                    .Select(kvp => kvp.Value.FirstOrDefault(v => v.enabled
                        && v.quest_type.Equals(entry.quest_type, StringComparison.OrdinalIgnoreCase)
                        && allowedCategories.Contains(v.category)))
                    .Where(v => v != null)
                    .ToList();

                if (pool.Count == 0)
                {
                    MelonLogger.Warning($"[QuestMod] No {entry.quest_type} reward items available for achievement '{name}'.");
                    return;
                }

                GearEntry reward = pool[_random.Next(pool.Count)];
                int amount = _settings.ApplyRewardMultiplier(reward.reward_amount);

                _pendingAchievementRewards.Enqueue((reward.spawn_name, amount, name));
                MelonLogger.Msg($"[QuestMod] Achievement unlocked: {name} ({entry.quest_type}) - reward queued: {amount}x {reward.spawn_name}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error handling achievement unlock: {ex.Message}");
            }
        }

        private void ProcessPendingAchievementRewards()
        {
            if (_pendingAchievementRewards.Count == 0 || !IsInGameplayScene)
                return;

            if (GameManager.GetInventoryComponent() == null)
                return;

            var reward = _pendingAchievementRewards.Dequeue();
            GrantRewardItems(reward.spawnName, reward.amount, "Achievement Reward");
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
