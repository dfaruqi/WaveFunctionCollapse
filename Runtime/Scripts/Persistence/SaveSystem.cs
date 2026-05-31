using UnityEngine;

namespace MagusStudios.WaveFunctionCollapse
{
    public static class SaveSystem
    {
        public delegate void GameSavedHandler(int saveSlot);

        public static event GameSavedHandler OnGameSaved;

        public delegate void GameLoadedHandler(int saveSlot);

        public static event GameLoadedHandler OnGameLoaded;

        public static void LoadGame(int saveSlot)
        {
            OnGameLoaded?.Invoke(saveSlot);
        }

        public static void SaveGame(int saveSlot)
        {
            OnGameSaved?.Invoke(saveSlot);
        }
    }
}