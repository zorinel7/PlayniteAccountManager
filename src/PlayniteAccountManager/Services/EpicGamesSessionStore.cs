using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace PlayniteAccountManager.Services
{
    internal sealed class EpicGamesSessionStore
    {
        private const string AccountIdSubKey =
            @"Software\Epic Games\Unreal Engine\Identifiers";

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
                error = Loc.Get("ErrClearSessionAccess", "Epic Games", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = Loc.Get("ErrClearSession", "Epic Games", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Reads the live "Remember me" session of the launcher. Returns null when
        /// the launcher has no remembered session. Values are never logged.
        /// </summary>
        public EpicSessionSnapshot CaptureLiveSession()
        {
            foreach (string folder in new[] { "WindowsEditor", "Windows" })
            {
                string path = Path.Combine(savedRoot, "Config", folder, "GameUserSettings.ini");
                if (!File.Exists(path))
                    continue;

                Dictionary<string, Dictionary<string, string>> ini = ReadIni(path);
                string data = GetIniValue(ini, "RememberMe", "Data");
                if (string.IsNullOrEmpty(data))
                    continue;

                return new EpicSessionSnapshot
                {
                    AccountId = ReadCurrentAccountId(),
                    RememberMeEnable = GetIniValue(ini, "RememberMe", "Enable") ?? "True",
                    RememberMeData = data,
                    OfflineData = GetIniValue(ini, "Offline", "Data"),
                    IniFolder = folder
                };
            }

            return null;
        }

        /// <summary>
        /// Writes a saved session back into the launcher configuration. Call after
        /// <see cref="ClearLiveState"/> with the launcher closed.
        /// </summary>
        public bool RestoreSession(EpicSessionSnapshot snapshot, out string error)
        {
            error = null;

            try
            {
                string folder = string.IsNullOrEmpty(snapshot.IniFolder) ? "WindowsEditor" : snapshot.IniFolder;
                string path = Path.Combine(savedRoot, "Config", folder, "GameUserSettings.ini");
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["RememberMe"] = new Dictionary<string, string>
                    {
                        ["Enable"] = string.IsNullOrEmpty(snapshot.RememberMeEnable) ? "True" : snapshot.RememberMeEnable,
                        ["Data"] = snapshot.RememberMeData
                    }
                };

                if (!string.IsNullOrEmpty(snapshot.OfflineData))
                    sections["Offline"] = new Dictionary<string, string> { ["Data"] = snapshot.OfflineData };

                ReplaceIniSections(path, sections);

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AccountIdSubKey))
                    key.SetValue(AccountIdValue, snapshot.AccountId, RegistryValueKind.String);

                log("Epic Games: przywrócono zapisaną sesję konta.");
                return true;
            }
            catch (Exception ex)
            {
                error = Loc.Get("ErrEpicRestore", ex.Message);
                return false;
            }
        }

        private static string ReadCurrentAccountId()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AccountIdSubKey, false))
                return key?.GetValue(AccountIdValue) as string;
        }

        private static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> current = null;

            foreach (string raw in ReadAllLinesShared(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    string name = line.Substring(1, line.Length - 2);
                    if (!result.TryGetValue(name, out current))
                    {
                        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        result[name] = current;
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (current != null && eq > 0)
                    current[line.Substring(0, eq)] = line.Substring(eq + 1);
            }

            return result;
        }

        private static string GetIniValue(Dictionary<string, Dictionary<string, string>> ini, string section, string key)
        {
            Dictionary<string, string> values;
            string value;
            return ini.TryGetValue(section, out values) && values.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>Replaces the given sections and keeps every other line of the file.</summary>
        private static void ReplaceIniSections(string path, Dictionary<string, Dictionary<string, string>> sections)
        {
            var output = new List<string>();
            bool skipping = false;

            if (File.Exists(path))
            {
                foreach (string raw in ReadAllLinesShared(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                        skipping = sections.ContainsKey(line.Substring(1, line.Length - 2));

                    if (!skipping)
                        output.Add(raw);
                }
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> section in sections)
            {
                output.Add("[" + section.Key + "]");
                foreach (KeyValuePair<string, string> value in section.Value)
                    output.Add(value.Key + "=" + value.Value);
                output.Add(string.Empty);
            }

            File.WriteAllLines(path, output, new UTF8Encoding(false));
        }

        private static IEnumerable<string> ReadAllLinesShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                var lines = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null)
                    lines.Add(line);
                return lines;
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
