using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

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
                bool savedAccountStateCleared = ClearSavedAccountState();
                bool registryStateCleared = ClearRegistryAuthenticationState();

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

                if (!cachedDataRemoved && !savedAccountStateCleared && !registryStateCleared)
                    log("Battle.net: nie znaleziono lokalnego stanu logowania do wyczyszczenia; kontynuuję uruchomienie launchera.");
                else
                    log("Battle.net: lokalny stan logowania i zapamiętanego konta został wyczyszczony.");

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

        private bool ClearSavedAccountState()
        {
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Battle.net",
                "Battle.net.config");

            if (!File.Exists(configPath))
            {
                log("Battle.net: nie znaleziono Battle.net.config do wyczyszczenia zapamiętanego konta.");
                return false;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                string updated = json;
                bool changed = false;

                string result;
                if (TryReplaceJsonValue(updated, "SavedAccountNames", "string", out result))
                {
                    updated = result;
                    changed = true;
                    log("Battle.net: SavedAccountNames ustawiono na pustą wartość.");
                }

                if (TryReplaceJsonValue(updated, "RememberAccountName", "false", out result))
                {
                    updated = result;
                    changed = true;
                    log("Battle.net: RememberAccountName ustawiono na false.");
                }

                if (TryReplaceJsonValue(updated, "AutoLogin", "false", out result))
                {
                    updated = result;
                    changed = true;
                    log("Battle.net: AutoLogin ustawiono na false.");
                }

                if (TryReplaceJsonValue(updated, "AutoLoginCN", "false", out result))
                {
                    updated = result;
                    changed = true;
                    log("Battle.net: AutoLoginCN ustawiono na false.");
                }

                if (!changed)
                {
                    log("Battle.net: nie znaleziono pól sesji/zapamiętanego konta w Battle.net.config.");
                    return false;
                }

                string tempPath = configPath + ".playnite-temp";
                File.WriteAllText(tempPath, updated);

                File.SetAttributes(configPath, FileAttributes.Normal);

                if (File.Exists(configPath))
                    File.Replace(tempPath, configPath, null);
                else
                    File.Move(tempPath, configPath);

                log("Battle.net: zapisano wyczyszczony Battle.net.config.");
                return true;
            }
            catch (Exception ex)
            {
                log("Battle.net: nie udało się wyczyścić Battle.net.config: " + ex.Message);
                return false;
            }
        }

        private bool ClearRegistryAuthenticationState()
        {
            bool changed = false;

            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(
                        RegistryHive.CurrentUser, view))
                    using (RegistryKey battleNet = baseKey.OpenSubKey(
                        @"SOFTWARE\Blizzard Entertainment\Battle.net", true))
                    {
                        if (battleNet == null)
                            continue;

                        changed |= DeleteSubKeyTree(battleNet, "Identity");
                        changed |= DeleteSubKeyTree(battleNet, "Authenticator");

                        using (RegistryKey launchOptions = battleNet.OpenSubKey("Launch Options", true))
                        {
                            if (launchOptions != null)
                            {
                                foreach (string childName in launchOptions.GetSubKeyNames())
                                {
                                    using (RegistryKey child = launchOptions.OpenSubKey(childName, true))
                                    {
                                        if (child == null)
                                            continue;

                                        changed |= DeleteValue(child, "WEB_TOKEN");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log("Battle.net: pominięto część czyszczenia rejestru: " + ex.Message);
                }
            }

            if (changed)
                log("Battle.net: wyczyszczono registry state powiązany z uwierzytelnieniem.");

            return changed;
        }

        private static bool DeleteSubKeyTree(RegistryKey parent, string name)
        {
            try
            {
                if (parent.OpenSubKey(name) == null)
                    return false;

                parent.DeleteSubKeyTree(name, false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool DeleteValue(RegistryKey key, string name)
        {
            try
            {
                if (key.GetValue(name) == null)
                    return false;

                key.DeleteValue(name, false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReplaceJsonValue(
            string json,
            string propertyName,
            string replacementMode,
            out string updated)
        {
            updated = null;

            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
                return false;

            string quotedName = """ + propertyName + """;
            int searchStart = 0;

            while (searchStart < json.Length)
            {
                int propertyIndex = json.IndexOf(
                    quotedName,
                    searchStart,
                    StringComparison.Ordinal);

                if (propertyIndex < 0)
                    return false;

                int colon = SkipWhitespace(
                    json,
                    propertyIndex + quotedName.Length);

                if (colon >= json.Length || json[colon] != ':')
                {
                    searchStart = propertyIndex + quotedName.Length;
                    continue;
                }

                int valueStart = SkipWhitespace(json, colon + 1);

                if (valueStart >= json.Length)
                    return false;

                char valueChar = json[valueStart];

                int valueEnd;
                if (valueChar == '"')
                {
                    valueEnd = FindJsonStringEnd(json, valueStart);
                    if (valueEnd < 0)
                        return false;

                    string replacement = replacementMode == "string"
                        ? "\"\""
                        : PreserveScalarType(json.Substring(valueStart, valueEnd - valueStart + 1), replacementMode);

                    updated =
                        json.Substring(0, valueStart) +
                        replacement +
                        json.Substring(valueEnd + 1);

                    return true;
                }

                if (valueChar == '[')
                {
                    valueEnd = FindJsonValueEnd(json, valueStart);
                    if (valueEnd < 0)
                        return false;

                    string replacement = replacementMode == "string"
                        ? """"
                        : "[]";

                    updated =
                        json.Substring(0, valueStart) +
                        replacement +
                        json.Substring(valueEnd + 1);

                    return true;
                }

                valueEnd = FindJsonScalarEnd(json, valueStart);
                if (valueEnd < 0)
                    return false;

                updated =
                    json.Substring(0, valueStart) +
                    replacementMode +
                    json.Substring(valueEnd);

                return true;
            }

            return false;
        }

        private static string PreserveScalarType(
            string original,
            string replacement)
        {
            if (original.Length >= 2 &&
                original[0] == '"' &&
                original[original.Length - 1] == '"')
            {
                return """ + replacement + """;
            }

            return replacement;
        }

        private static int SkipWhitespace(string value, int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
                index++;

            return index;
        }

        private static int FindJsonStringEnd(string json, int start)
        {
            bool escaped = false;

            for (int i = start + 1; i < json.Length; i++)
            {
                char ch = json[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                    return i;
            }

            return -1;
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

        private static int FindJsonScalarEnd(string json, int start)
        {
            int i = start;

            while (i < json.Length &&
                   json[i] != ',' &&
                   json[i] != '}' &&
                   !char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            return i;
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
