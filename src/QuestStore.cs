using System;
using System.Collections.Generic;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using ModData;

namespace MissionImpossible
{
    // Quest state of the active save game, stored inside the save with ModData
    // (Mods\ModData\<slot>.moddata, entry "MissionImpossible"). ModData keeps the data in memory and writes it
    // when the game saves, so this mod never writes a file itself.
    public class QuestStore
    {
        private readonly ModDataManager _modData = new ModDataManager("MissionImpossible", false);
        private bool _dirty;

        // Increases whenever the quest data changes or is replaced; lets the journal rebuild its texts only when needed
        public int Revision { get; private set; }

        public void MarkDirty()
        {
            _dirty = true;
            Revision++;
        }

        // Forget unsaved changes (back in the main menu)
        public void Reset()
        {
            _dirty = false;
            Revision++;
        }

        // Quest state of the active save; an empty state if the save has none yet
        public QuestState Load()
        {
            _dirty = false;
            Revision++;

            try
            {
                string json = _modData.Load();
                if (!string.IsNullOrEmpty(json))
                {
                    var state = JsonSerializer.Deserialize<QuestState>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (state != null)
                    {
                        state.ActiveQuests ??= new List<Quest>();
                        foreach (var quest in state.ActiveQuests)
                        {
                            quest.Status = quest.CurrentAmount >= quest.RequiredAmount ? "Complete" : "Active";
                        }
                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error reading quest data of the save: {ex.Message}");
            }

            return new QuestState();
        }

        // Hand the state to ModData; it is written to disk together with the game save
        public void Commit(QuestState state)
        {
            if (state == null || !_dirty)
                return;

            try
            {
                if (_modData.Save(JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })))
                    _dirty = false;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[QuestMod] Error storing quest data in the save: {ex.Message}");
            }
        }
    }

    // ModData writes its data to disk in a postfix of this method; the quest state is handed over right before
    [HarmonyPatch(typeof(SaveGameSlots), nameof(SaveGameSlots.WriteSlotToDisk), new Type[] { typeof(SlotData), typeof(SaveGameSlots.Timestamp) })]
    internal static class QuestSaveGamePatch
    {
        private static void Prefix()
        {
            QuestMod.Instance?.OnBeforeGameSave();
        }
    }
}
