using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using Il2CppSteamworks;
using MelonLoader;
using UnityEngine;

namespace MissionImpossible
{
    // Test-only probe: dumps every Achievement enum entry and records unlocks, to find out what the game exposes.
    // Everything is also appended to Mods/AchievementProbeLog.txt, which survives game restarts (Latest.log does not).
    internal static class AchievementProbe
    {
        private static bool _dumpedMenu;
        private static bool _dumpedGameplay;
        private static string _scene = "?";
        private static float _nextPoll;
        private static Dictionary<string, bool> _lastSteamState;

        private static string ModsDir => Path.Combine(Directory.GetCurrentDirectory(), "Mods");
        private static string DumpPath => Path.Combine(ModsDir, "AchievementDump.csv");
        private static string LogPath => Path.Combine(ModsDir, "AchievementProbeLog.txt");

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [scene {_scene}] {message}";
            MelonLogger.Msg($"[AchievementProbe] {message}");
            try { File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false)); }
            catch { }
        }

        public static void OnSceneLoaded(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)
                || string.Equals(sceneName, "Empty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneName, "Boot", StringComparison.OrdinalIgnoreCase))
                return;

            _scene = sceneName;
            bool isMenu = sceneName.StartsWith("MainMenu", StringComparison.OrdinalIgnoreCase);

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

            Dump(sceneName, DumpPath);
        }

        // Called from OnUpdate; detects any change of the Steam unlock state, regardless of which code path caused it.
        public static void Tick()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 2f;

            try
            {
                var current = ReadSteamState();
                if (current == null) return;

                if (_lastSteamState == null)
                {
                    _lastSteamState = current;
                    Log($"Baseline captured: {CountUnlocked(current)}/{current.Count} unlocked on Steam.");
                    return;
                }

                foreach (var kv in current)
                {
                    _lastSteamState.TryGetValue(kv.Key, out bool before);
                    if (before != kv.Value)
                    {
                        var ach = (Achievement)Enum.Parse(typeof(Achievement), kv.Key);
                        Log($"*** STEAM STATE CHANGE: {kv.Key} (id {(int)ach}, group {GroupOf(kv.Key)}) {before} -> {kv.Value}");
                        Dump("state-change:" + kv.Key, Path.Combine(ModsDir, $"AchievementDump_{DateTime.Now:yyyyMMdd_HHmmss}_{kv.Key}.csv"));
                    }
                }

                _lastSteamState = current;
            }
            catch (Exception ex)
            {
                Log($"Tick failed: {ex.Message}");
            }
        }

        private static Dictionary<string, bool> ReadSteamState()
        {
            var result = new Dictionary<string, bool>();
            foreach (string name in Enum.GetNames(typeof(Achievement)))
            {
                bool unlocked;
                try
                {
                    if (!SteamUserStats.GetAchievement(name, out unlocked))
                        return null;
                }
                catch
                {
                    return null;
                }
                result[name] = unlocked;
            }
            return result;
        }

        private static int CountUnlocked(Dictionary<string, bool> state)
        {
            int n = 0;
            foreach (var kv in state) if (kv.Value) n++;
            return n;
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

        public static string SteamStateOf(Achievement achievement)
        {
            try
            {
                bool unlocked;
                bool valid = SteamUserStats.GetAchievement(achievement.ToString(), out unlocked);
                return valid ? unlocked.ToString() : "n/a";
            }
            catch (Exception ex) { return "error(" + ex.Message + ")"; }
        }

        public static string PlatformStateOf(Achievement achievement)
        {
            try { return AchievementPlatformManager.IsUnlocked(achievement).ToString(); }
            catch (Exception ex) { return "error(" + ex.Message + ")"; }
        }

        public static string Describe(Achievement achievement)
        {
            string name = achievement.ToString();
            return $"{name} (id {(int)achievement}, group {GroupOf(name)}) steamUnlocked={SteamStateOf(achievement)} platformUnlocked={PlatformStateOf(achievement)}";
        }

        private static void Dump(string reason, string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("id;enum_name;group;steam_api_name_valid;steam_unlocked;platform_unlocked");

                int count = 0;
                foreach (string name in Enum.GetNames(typeof(Achievement)))
                {
                    Achievement ach = (Achievement)Enum.Parse(typeof(Achievement), name);

                    bool steamValid = false;
                    bool steamUnlocked = false;
                    try { steamValid = SteamUserStats.GetAchievement(name, out steamUnlocked); }
                    catch { }

                    bool platformUnlocked = false;
                    try { platformUnlocked = AchievementPlatformManager.IsUnlocked(ach); }
                    catch { }

                    sb.AppendLine($"{(int)ach};{name};{GroupOf(name)};{steamValid};{steamUnlocked};{platformUnlocked}");
                    count++;
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Log($"Dumped {count} achievements ({reason}) -> {path}");
            }
            catch (Exception ex)
            {
                Log($"Dump failed: {ex}");
            }
        }

        public static string SafeStackTrace()
        {
            try { return new System.Diagnostics.StackTrace(true).ToString(); }
            catch (Exception ex) { return "stack trace unavailable: " + ex.Message; }
        }
    }

    [HarmonyPatch(typeof(AchievementPlatformManager), nameof(AchievementPlatformManager.Unlock))]
    internal static class AchievementManagerUnlockProbePatch
    {
        private static void Prefix(Achievement achievement)
        {
            try
            {
                AchievementProbe.Log($">>> AchievementPlatformManager.Unlock BEFORE: {AchievementProbe.Describe(achievement)}");
                AchievementProbe.Log("    Stack: " + AchievementProbe.SafeStackTrace().Replace(Environment.NewLine, " | "));
            }
            catch (Exception ex) { AchievementProbe.Log($"Manager.Unlock prefix failed: {ex.Message}"); }
        }

        private static void Postfix(Achievement achievement)
        {
            try { AchievementProbe.Log($"<<< AchievementPlatformManager.Unlock AFTER: {AchievementProbe.Describe(achievement)}"); }
            catch (Exception ex) { AchievementProbe.Log($"Manager.Unlock postfix failed: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(SteamAchievementPlatform), nameof(SteamAchievementPlatform.Unlock))]
    internal static class SteamPlatformUnlockProbePatch
    {
        private static void Prefix(Achievement achievement)
        {
            try { AchievementProbe.Log($">>> SteamAchievementPlatform.Unlock BEFORE: {AchievementProbe.Describe(achievement)}"); }
            catch (Exception ex) { AchievementProbe.Log($"Steam.Unlock prefix failed: {ex.Message}"); }
        }

        private static void Postfix(Achievement achievement)
        {
            try { AchievementProbe.Log($"<<< SteamAchievementPlatform.Unlock AFTER: {AchievementProbe.Describe(achievement)}"); }
            catch (Exception ex) { AchievementProbe.Log($"Steam.Unlock postfix failed: {ex.Message}"); }
        }
    }
}
