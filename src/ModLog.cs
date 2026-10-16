using System.Collections.Generic;
using System;
using MelonLoader;

namespace MissionImpossible
{
    // Single place for all log output. There is one switch ("Enable Logging" in the mod settings):
    // events are only written when it is on, warnings and errors are always written.
    internal static class ModLog
    {
        private const long RepeatMilliseconds = 10000;
        private const int MaxTrackedMessages = 200;

        // Warnings and errors can come from other threads (e.g. while the game saves), so no Unity API and a lock here
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, long> _lastWritten = new Dictionary<string, long>();

        public static bool Enabled => QuestMod.LoggingEnabled;

        public static void Info(string message)
        {
            if (Enabled)
                MelonLogger.Msg(message);
        }

        public static void Warning(string message)
        {
            if (ShouldWrite(message))
                MelonLogger.Warning(message);
        }

        public static void Error(string message)
        {
            if (ShouldWrite(message))
                MelonLogger.Error(message);
        }

        // Code that runs every frame or every second must not flood the log with the same problem
        private static bool ShouldWrite(string message)
        {
            long now = Environment.TickCount64;

            lock (_lock)
            {
                if (_lastWritten.TryGetValue(message, out long last) && now - last < RepeatMilliseconds)
                    return false;

                if (_lastWritten.Count >= MaxTrackedMessages)
                    _lastWritten.Clear();

                _lastWritten[message] = now;
                return true;
            }
        }
    }
}
