using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// The part of the Epic Games Launcher state that keeps an account signed in
    /// ("Remember me"): the [RememberMe] and [Offline] sections of
    /// GameUserSettings.ini and the AccountId identifier in the registry.
    /// </summary>
    internal sealed class EpicSessionSnapshot
    {
        public string AccountId;
        public string RememberMeEnable;
        public string RememberMeData;
        public string OfflineData;
        public string IniFolder;
        public DateTime SavedUtc;

        public bool IsComplete =>
            !string.IsNullOrEmpty(RememberMeData) && IsAccountId(AccountId);

        public static bool IsAccountId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32)
                return false;

            foreach (char c in value)
            {
                if (!Uri.IsHexDigit(c))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Stores one Epic session snapshot per plugin account, encrypted with DPAPI
    /// (current Windows user + plugin-specific entropy) in files readable only by
    /// the current user. A snapshot contains a refresh token, so it is treated
    /// like a password: never logged, never written unencrypted.
    /// </summary>
    internal sealed class EpicSessionVault
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("PlayniteAccountManager/2A6DE1EF-2EF4-4D60-A0FD-4AFBD8F5B72C/epic-session/v1");

        private readonly string directory;
        private readonly string activeMarkerPath;
        private readonly object sync = new object();

        public EpicSessionVault(string pluginUserDataPath)
        {
            directory = Path.Combine(pluginUserDataPath, "epic-sessions");
            activeMarkerPath = Path.Combine(directory, "active-account.txt");
        }

        public void Save(Guid accountId, EpicSessionSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsComplete)
                throw new ArgumentException("Incomplete Epic session snapshot.", nameof(snapshot));

            var values = new Dictionary<string, string>
            {
                ["AccountId"] = snapshot.AccountId,
                ["RememberMe.Enable"] = snapshot.RememberMeEnable ?? "True",
                ["RememberMe.Data"] = snapshot.RememberMeData,
                ["Offline.Data"] = snapshot.OfflineData ?? string.Empty,
                ["IniFolder"] = snapshot.IniFolder ?? string.Empty,
                ["SavedUtc"] = DateTime.UtcNow.ToString("o")
            };

            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in values)
                text.Append(pair.Key).Append('\t').Append(Sanitize(pair.Value)).Append('\n');

            byte[] plain = Encoding.UTF8.GetBytes(text.ToString());
            try
            {
                byte[] protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                lock (sync)
                {
                    Directory.CreateDirectory(directory);
                    string path = GetPath(accountId);
                    string temp = path + ".tmp";
                    File.WriteAllBytes(temp, protectedBytes);
                    SecureCredentialStore.RestrictToCurrentUser(temp);
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                    SecureCredentialStore.RestrictToCurrentUser(path);
                }
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        public bool TryLoad(Guid accountId, out EpicSessionSnapshot snapshot)
        {
            snapshot = null;
            byte[] plain = null;

            try
            {
                string path = GetPath(accountId);
                byte[] protectedBytes;
                lock (sync)
                {
                    if (!File.Exists(path))
                        return false;

                    protectedBytes = File.ReadAllBytes(path);
                }

                plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in Encoding.UTF8.GetString(plain).Split('\n'))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0)
                        values[line.Substring(0, tab)] = line.Substring(tab + 1);
                }

                DateTime saved;
                snapshot = new EpicSessionSnapshot
                {
                    AccountId = Get(values, "AccountId"),
                    RememberMeEnable = Get(values, "RememberMe.Enable"),
                    RememberMeData = Get(values, "RememberMe.Data"),
                    OfflineData = Get(values, "Offline.Data"),
                    IniFolder = Get(values, "IniFolder"),
                    SavedUtc = DateTime.TryParse(Get(values, "SavedUtc"), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out saved) ? saved : DateTime.MinValue
                };

                if (!snapshot.IsComplete)
                {
                    snapshot = null;
                    return false;
                }

                return true;
            }
            catch
            {
                snapshot = null;
                return false;
            }
            finally
            {
                if (plain != null)
                    Array.Clear(plain, 0, plain.Length);
            }
        }

        /// <summary>Epic AccountId bound to the plugin account, or null.</summary>
        public string GetBoundAccountId(Guid accountId)
        {
            EpicSessionSnapshot snapshot;
            return TryLoad(accountId, out snapshot) ? snapshot.AccountId : null;
        }

        public void Delete(Guid accountId)
        {
            lock (sync)
            {
                string path = GetPath(accountId);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        /// <summary>The plugin account whose session is currently live in the launcher.</summary>
        public Guid GetActiveAccount()
        {
            try
            {
                Guid id;
                lock (sync)
                {
                    if (File.Exists(activeMarkerPath) &&
                        Guid.TryParse(File.ReadAllText(activeMarkerPath).Trim(), out id))
                        return id;
                }
            }
            catch
            {
            }

            return Guid.Empty;
        }

        public void SetActiveAccount(Guid accountId)
        {
            lock (sync)
            {
                if (accountId == Guid.Empty)
                {
                    if (File.Exists(activeMarkerPath))
                        File.Delete(activeMarkerPath);
                    return;
                }

                Directory.CreateDirectory(directory);
                File.WriteAllText(activeMarkerPath, accountId.ToString("D"));
            }
        }

        private string GetPath(Guid accountId)
        {
            return Path.Combine(directory, accountId.ToString("N") + ".dat");
        }

        private static string Get(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : null;
        }

        private static string Sanitize(string value)
        {
            return (value ?? string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
    }
}
