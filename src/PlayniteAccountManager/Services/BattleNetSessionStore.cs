using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace PlayniteAccountManager.Services
{
    internal sealed class BattleNetSessionStore
    {
        private readonly Action<string> log;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public BattleNetSessionStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool ClearLiveState(out string error)
        {
            error = null;

            try
            {
                bool cachedDataRemoved = RemoveCachedData();
                bool savedAccountsCleared = ClearSavedAccountNames();

                RemoveCacheDirectory(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Battle.net",
                        "Cache"));

                RemoveCacheDirectory(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Battle.net",
                        "BrowserCache"));

                RemoveCacheDirectory(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Battle.net",
                        "BrowserCaches"));

                RemoveCacheDirectory(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Battle.net",
                        "Cache"));

                RemoveCacheDirectory(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Blizzard Entertainment",
                        "Battle.net",
                        "Cache"));

                if (!cachedDataRemoved && !savedAccountsCleared)
                {
                    log("Battle.net: nie znaleziono lokalnego stanu logowania do wyczyszczenia; kontynuuję uruchomienie launchera.");
                }
                else
                {
                    log("Battle.net: lokalny stan logowania został wyczyszczony.");
                }

                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "Brak uprawnień do wyczyszczenia danych sesji Battle.net: " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się wyczyścić sesji Battle.net: " + ex.Message;
                return false;
            }
        }

        private bool RemoveCachedData()
        {
            bool removed = false;

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Battle.net");

            string[] files =
            {
                Path.Combine(root, "CachedData.db"),
                Path.Combine(root, "CachedData.db-journal"),
                Path.Combine(root, "CachedData.db-shm"),
                Path.Combine(root, "CachedData.db-wal")
            };

            foreach (string file in files)
                removed |= TryDeleteFile(file);

            return removed;
        }

        private bool ClearSavedAccountNames()
        {
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Battle.net",
                "Battle.net.config");

            if (!File.Exists(configPath))
                return false;

            try
            {
                Dictionary<string, object> root;

                using (var reader = new StreamReader(configPath))
                {
                    object parsed = Json.DeserializeObject(reader.ReadToEnd());
                    root = parsed as Dictionary<string, object>;
                }

                if (root == null)
                {
                    log("Battle.net: nie udało się odczytać Battle.net.config jako JSON.");
                    return false;
                }

                Dictionary<string, object> client;
                if (root.ContainsKey("Client"))
                    client = root["Client"] as Dictionary<string, object>;
                else
                    client = null;

                if (client == null)
                {
                    client = new Dictionary<string, object>();
                    root["Client"] = client;
                }

                client["SavedAccountNames"] = new object[0];

                string json = Json.Serialize(root);
                string tempPath = configPath + ".playnite-temp";

                using (var writer = new StreamWriter(tempPath, false))
                {
                    writer.Write(json);
                }

                if (File.Exists(configPath))
                    File.SetAttributes(configPath, FileAttributes.Normal);

                File.Replace(tempPath, configPath, null);
                log("Battle.net: wyczyszczono listę zapamiętanych kont w Battle.net.config.");
                return true;
            }
            catch (Exception ex)
            {
                log("Battle.net: nie udało się wyczyścić SavedAccountNames: " + ex.Message);
                return false;
            }
        }

        private void RemoveCacheDirectory(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            try
            {
                ClearReadOnlyAttributes(directory);
                Directory.Delete(directory, true);
                log("Battle.net: usunięto lokalny cache: " + directory);
            }
            catch (Exception ex)
            {
                log("Battle.net: nie udało się usunąć cache " + directory + ": " + ex.Message);
            }
        }

        private bool TryDeleteFile(string file)
        {
            try
            {
                if (!File.Exists(file))
                    return false;

                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                log("Battle.net: usunięto plik stanu sesji: " + file);
                return true;
            }
            catch (Exception ex)
            {
                log("Battle.net: nie udało się usunąć pliku " + file + ": " + ex.Message);
                return false;
            }
        }

        private static void ClearReadOnlyAttributes(string directory)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(child, FileAttributes.Normal); } catch { }
                }

                try { File.SetAttributes(directory, FileAttributes.Normal); } catch { }
            }
            catch
            {
            }
        }
    }
}
