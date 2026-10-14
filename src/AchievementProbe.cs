using System;
using System.IO;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using Il2CppSteamworks;
using MelonLoader;

namespace MissionImpossible
{
    // Test-only probe: dumps every Achievement enum entry and logs unlocks, to find out what the game exposes.
    internal static class AchievementProbe
    {
        private static bool _dumpedMenu;
        private static bool _dumpedGameplay;

        private static string DumpPath =>
            Path.Combine(Directory.GetCurrentDirectory(), "Mods", "AchievementDump.csv");

        public static void OnSceneLoaded(string sceneName)
        {
            bool isMenu = string.Equals(sceneName, "MainMenu", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(sceneName, "Boot", StringComparison.OrdinalIgnoreCase);

            if (isMenu)
            {
                if (_dumpedMenu) return;
                _dumpedMenu = true;
            }
            else
            {
                if (_dumpedGameplay) return;
                _dumpedGameplay = true;
            }

            Dump(sceneName);
        }

        public static string GroupOf(string name)
        {
            if (name.StartsWith("EP", StringComparison.Ordinal) && name.Length > 3 && char.IsDigit(name[2]) && name[3] == '_')
                return "wintermute";
            if (name.StartsWith("DLC", StringComparison.Ordinal))
                return "dlc";
            if (name.StartsWith("SM_", StringComparison.Ordinal))
                return "sm";
            return "base";
        }

        private static void Dump(string sceneName)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("id;enum_name;group;steam_api_name_valid;steam_unlocked;platform_unlocked");

                var names = Enum.GetNames(typeof(Achievement));
                int count = 0;
                foreach (string name in names)
                {
                    Achievement ach = (Achievement)Enum.Parse(typeof(Achievement), name);

                    bool steamValid = false;
                    bool steamUnlocked = false;
                    try { steamValid = SteamUserStats.GetAchievement(name, out steamUnlocked); }
                    catch (Exception ex) { MelonLogger.Warning($"[AchievementProbe] Steam lookup failed for '{name}': {ex.Message}"); }

                    bool platformUnlocked = false;
                    try { platformUnlocked = AchievementPlatformManager.IsUnlocked(ach); }
                    catch (Exception ex) { MelonLogger.Warning($"[AchievementProbe] Platform lookup failed for '{name}': {ex.Message}"); }

                    sb.AppendLine($"{(int)ach};{name};{GroupOf(name)};{steamValid};{steamUnlocked};{platformUnlocked}");
                    count++;
                }

                File.WriteAllText(DumpPath, sb.ToString(), new UTF8Encoding(false));
                MelonLogger.Msg($"[AchievementProbe] Dumped {count} achievements (scene '{sceneName}') -> {DumpPath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[AchievementProbe] Dump failed: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(AchievementPlatformManager), nameof(AchievementPlatformManager.Unlock))]
    internal static class AchievementUnlockProbePatch
    {
        private static void Prefix(Achievement achievement)
        {
            try
            {
                string name = achievement.ToString();
                MelonLogger.Msg($"[AchievementProbe] Unlock called: {name} (id {(int)achievement}, group {AchievementProbe.GroupOf(name)}, alreadyUnlocked {AchievementPlatformManager.IsUnlocked(achievement)})");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[AchievementProbe] Unlock log failed: {ex.Message}");
            }
        }
    }
}
