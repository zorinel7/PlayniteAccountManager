using System;
using System.IO;
using Microsoft.Win32;

namespace PlayniteAccountManager.Services
{
    internal sealed class GOGGalaxySessionStore
    {
        private readonly Action<string> log;

        public GOGGalaxySessionStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool ClearLiveState(out string error)
        {
            error = null;

            try
            {
                RemoveRefreshToken();
                RemoveLockFiles();
                RemoveTokenFiles();

                log("GOG Galaxy: wyczyszczono lokalny stan uwierzytelnienia bez usuwania bazy gier.");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "Brak uprawnień do wyczyszczenia danych sesji GOG Galaxy: " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się wyczyścić sesji GOG Galaxy: " + ex.Message;
                return false;
            }
        }

        private void RemoveRefreshToken()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\GOG.com\Galaxy", true))
            {
                if (key == null)
                    return;

                try
                {
                    if (key.GetValue("refreshToken") != null)
                    {
                        key.DeleteValue("refreshToken", false);
                        log("GOG Galaxy: usunięto refreshToken z HKCU.");
                    }
                }
                catch
                {
                }
            }
        }

        private void RemoveLockFiles()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GOG.com",
                "Galaxy",
                "lock-files");

            if (!Directory.Exists(directory))
                return;

            foreach (string file in Directory.GetFiles(directory, "*.lock", SearchOption.TopDirectoryOnly))
            {
                TryDelete(file);
            }
        }

        private void RemoveTokenFiles()
        {
            string[] roots =
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "GOG.com",
                    "Galaxy"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GOG.com",
                    "Galaxy")
            };

            foreach (string root in roots)
            {
                TryDelete(Path.Combine(root, "tokens.json"));
            }
        }

        private void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    log("GOG Galaxy: usunięto plik sesji: " + file);
                }
            }
            catch (Exception ex)
            {
                log("GOG Galaxy: nie udało się usunąć pliku sesji " + file + ": " + ex.Message);
            }
        }
    }
}
