using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Stores the small local login-state block used by Epic Games Launcher.
    /// Epic account switching is deliberately file/registry based instead of
    /// using the launcher's logout UI.
    /// </summary>
    internal sealed class EpicGamesSessionStore
    {
        private const string AccountIdSubKey =
            @"Software\Epic Games\Unreal Engine\Identifiers";

        private const string AccountIdValue = "AccountId";

        private readonly string rootPath;
        private readonly Action<string> log;

        private readonly string liveSettingsPath;
        private readonly string[] cacheDirectories;

        public EpicGamesSessionStore(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });

            rootPath = Path.Combine(pluginUserDataPath, "EpicGamesSessions");
            Directory.CreateDirectory(rootPath);

            liveSettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EpicGamesLauncher", "Saved", "Config", "WindowsEditor",
                "GameUserSettings.ini");

            string savedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EpicGamesLauncher", "Saved");

            cacheDirectories = new[]
            {
                Path.Combine(savedRoot, "webcache"),
                Path.Combine(savedRoot, "webcache_4147"),
                Path.Combine(savedRoot, "webcache_4430")
            };
        }

        public bool HasSnapshot(Guid accountId)
        {
            string root = GetAccountRoot(accountId);
            return File.Exists(Path.Combine(root, "GameUserSettings.ini")) &&
                   File.Exists(Path.Combine(root, "AccountId.txt"));
        }

        public string GetCurrentAccountId()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AccountIdSubKey, false))
                {
                    return key == null
                        ? string.Empty
                        : (key.GetValue(AccountIdValue) as string ?? string.Empty).Trim();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        public string GetSnapshotAccountId(Guid accountId)
        {
            try
            {
                string path = Path.Combine(GetAccountRoot(accountId), "AccountId.txt");
                return File.Exists(path)
                    ? File.ReadAllText(path).Trim()
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public bool Restore(Guid accountId, out string error)
        {
            error = null;

            string accountRoot = GetAccountRoot(accountId);
            string sourceIni = Path.Combine(accountRoot, "GameUserSettings.ini");
            string sourceId = Path.Combine(accountRoot, "AccountId.txt");

            if (!File.Exists(sourceIni) || !File.Exists(sourceId))
            {
                error = "Brak zapisanej sesji Epic Games dla tego konta.";
                return false;
            }

            try
            {
                string expectedId = File.ReadAllText(sourceId).Trim();
                if (string.IsNullOrWhiteSpace(expectedId))
                {
                    error = "Zapisana sesja Epic Games nie zawiera AccountId.";
                    return false;
                }

                ClearLiveState(false);

                Directory.CreateDirectory(Path.GetDirectoryName(liveSettingsPath));
                File.Copy(sourceIni, liveSettingsPath, true);
                SetCurrentAccountId(expectedId);

                log("Epic Games: przywrócono zapisany stan sesji konta.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się przywrócić sesji Epic Games: " + ex.Message;
                return false;
            }
        }

        public bool SaveCurrent(Guid accountId, out string error)
        {
            error = null;

            string currentId = GetCurrentAccountId();
            if (string.IsNullOrWhiteSpace(currentId))
            {
                error = "Epic Games nie udostępnił bieżącego AccountId. Nie zapisuję sesji.";
                return false;
            }

            try
            {
                if (!File.Exists(liveSettingsPath))
                {
                    error = "Nie znaleziono GameUserSettings.ini Epic Games.";
                    return false;
                }

                string accountRoot = GetAccountRoot(accountId);
                Directory.CreateDirectory(accountRoot);

                File.Copy(
                    liveSettingsPath,
                    Path.Combine(accountRoot, "GameUserSettings.ini"),
                    true);

                File.WriteAllText(
                    Path.Combine(accountRoot, "AccountId.txt"),
                    currentId);

                log("Epic Games: zapisano snapshot sesji. AccountId=" + currentId + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się zapisać sesji Epic Games: " + ex.Message;
                return false;
            }
        }

        public bool ClearLiveState(bool clearCache)
        {
            try
            {
                if (File.Exists(liveSettingsPath))
                    File.Delete(liveSettingsPath);

                DeleteCurrentAccountId();

                if (clearCache)
                {
                    foreach (string directory in cacheDirectories)
                        DeleteDirectory(directory);
                }

                return true;
            }
            catch (Exception ex)
            {
                log("Epic Games: nie udało się wyczyścić stanu logowania: " + ex.Message);
                return false;
            }
        }

        public bool ClearCache()
        {
            bool ok = true;

            foreach (string directory in cacheDirectories)
            {
                try
                {
                    DeleteDirectory(directory);
                }
                catch
                {
                    ok = false;
                }
            }

            return ok;
        }

        public void Forget(Guid accountId)
        {
            string root = GetAccountRoot(accountId);

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (Exception ex)
            {
                log("Epic Games: nie udało się usunąć snapshotu: " + ex.Message);
            }
        }

        private string GetAccountRoot(Guid accountId)
        {
            return Path.Combine(rootPath, accountId.ToString("D"));
        }

        private static void SetCurrentAccountId(string accountId)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AccountIdSubKey))
                key.SetValue(AccountIdValue, accountId, RegistryValueKind.String);
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
