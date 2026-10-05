using System;
using System.IO;
using Microsoft.Win32;

namespace PlayniteAccountManager.Services
{
    internal sealed class EpicGamesSessionStore
    {
        private const string AccountIdSubKey =
            @"SoftwareEpic GamesUnreal EngineIdentifiers";

        private const string AccountIdValue = "AccountId";

        private readonly Action<string> log;
        private readonly string settingsPath;
        private readonly string savedRoot;

        public EpicGamesSessionStore(Action<string> log)
        {
            this.log = log ?? (_ => { });

            savedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EpicGamesLauncher", "Saved");

            settingsPath = Path.Combine(
                savedRoot, "Config", "WindowsEditor", "GameUserSettings.ini");
        }

        public bool ClearLiveState(out string error)
        {
            error = null;

            try
            {
                if (File.Exists(settingsPath))
                    File.Delete(settingsPath);

                DeleteCurrentAccountId();

                DeleteDirectory(Path.Combine(savedRoot, "webcache"));
                DeleteDirectory(Path.Combine(savedRoot, "webcache_4147"));
                DeleteDirectory(Path.Combine(savedRoot, "webcache_4430"));

                log("Epic Games: active login state was cleared.");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "Brak uprawnień do wyczyszczenia sesji Epic Games: " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się wyczyścić sesji Epic Games: " + ex.Message;
                return false;
            }
        }

        private static void DeleteCurrentAccountId()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AccountIdSubKey, true))
            {
                if (key != null)
                    key.DeleteValue(AccountIdValue, false);
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
