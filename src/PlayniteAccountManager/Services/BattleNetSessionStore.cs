using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAccountManager.Services
{
    internal sealed class BattleNetSessionStore
    {
        private readonly Action<string> log;

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
                    log("Battle.net: nie znaleziono lokalnego stanu logowania do wyczyszczenia; kontynuuję uruchomienie launchera.");
                else
                    log("Battle.net: lokalny stan logowania został wyczyszczony.");

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
                string json = File.ReadAllText(configPath);
                string updated;
                if (!TryClearJsonArrayProperty(json, "SavedAccountNames", out updated))
                {
                    log("Battle.net: nie znaleziono pola SavedAccountNames w Battle.net.config.");
                    return false;
                }

                string tempPath = configPath + ".playnite-temp";
                File.WriteAllText(tempPath, updated);

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

        private static bool TryClearJsonArrayProperty(
            string json,
            string propertyName,
            out string updated)
        {
            updated = null;

            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
                return false;

            string quotedName = "\"" + propertyName + "\"";
            int searchStart = 0;

            while (searchStart < json.Length)
            {
                int propertyIndex = json.IndexOf(quotedName, searchStart, StringComparison.Ordinal);
                if (propertyIndex < 0)
                    return false;

                int colon = SkipWhitespace(json, propertyIndex + quotedName.Length);
                if (colon >= json.Length || json[colon] != ':')
                {
                    searchStart = propertyIndex + quotedName.Length;
                    continue;
                }

                int valueStart = SkipWhitespace(json, colon + 1);
                if (valueStart >= json.Length || json[valueStart] != '[')
                {
                    searchStart = propertyIndex + quotedName.Length;
                    continue;
                }

                int valueEnd = FindJsonValueEnd(json, valueStart);
                if (valueEnd <= valueStart || json[valueEnd] != ']')
                {
                    searchStart = propertyIndex + quotedName.Length;
                    continue;
                }

                updated = json.Substring(0, valueStart) + "[]" + json.Substring(valueEnd + 1);
                return true;
            }

            return false;
        }

        private static int SkipWhitespace(string value, int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;

            return index;
        }

        private static int FindJsonValueEnd(string json, int start)
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = start; i < json.Length; i++)
            {
                char ch = json[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '[')
                {
                    depth++;
                    continue;
                }

                if (ch == ']')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
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
