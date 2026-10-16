using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PlayniteAccountManager.Services
{
    internal sealed class SecureCredentialStore
    {
        // Additional DPAPI entropy bound to this plugin. Other software running as
        // the same Windows user can no longer decrypt the blobs with a plain
        // ProtectedData.Unprotect(data, null, CurrentUser) call.
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("PlayniteAccountManager/2A6DE1EF-2EF4-4D60-A0FD-4AFBD8F5B72C/credentials/v2");

        private const string V2Prefix = "v2:";

        private readonly string filePath;
        private readonly object sync = new object();

        public SecureCredentialStore(string directory)
        {
            Directory.CreateDirectory(directory);
            filePath = Path.Combine(directory, "credentials.dat");
        }

        public void Set(Guid accountId, string password)
        {
            lock (sync)
            {
                var values = LoadRaw();
                if (string.IsNullOrEmpty(password))
                {
                    values.Remove(accountId);
                }
                else
                {
                    values[accountId] = ProtectV2(password);
                }
                SaveRaw(values);
            }
        }

        private static string ProtectV2(string password)
        {
            byte[] plain = Encoding.UTF8.GetBytes(password);
            try
            {
                var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                return V2Prefix + Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        public string Get(Guid accountId)
        {
            lock (sync)
            {
                var values = LoadRaw();
                string encoded;
                if (!values.TryGetValue(accountId, out encoded) || string.IsNullOrEmpty(encoded))
                    return string.Empty;

                try
                {
                    if (encoded.StartsWith(V2Prefix, StringComparison.Ordinal))
                        return Unprotect(encoded.Substring(V2Prefix.Length), Entropy);

                    // Legacy entry (<= 0.9.61, no entropy): read it and
                    // transparently re-encrypt it in the v2 format.
                    string password = Unprotect(encoded, null);
                    if (!string.IsNullOrEmpty(password))
                    {
                        try
                        {
                            values[accountId] = ProtectV2(password);
                            SaveRaw(values);
                        }
                        catch
                        {
                            // Migration is best effort; the legacy value stays readable.
                        }
                    }
                    return password;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private static string Unprotect(string base64, byte[] entropy)
        {
            byte[] bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(base64),
                entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        public void Delete(Guid accountId)
        {
            lock (sync)
            {
                var values = LoadRaw();
                if (values.Remove(accountId))
                    SaveRaw(values);
            }
        }

        public bool Has(Guid accountId)
        {
            lock (sync)
                return LoadRaw().ContainsKey(accountId);
        }

        private Dictionary<Guid, string> LoadRaw()
        {
            var result = new Dictionary<Guid, string>();
            if (!File.Exists(filePath))
                return result;

            foreach (var line in File.ReadAllLines(filePath, Encoding.UTF8))
            {
                var parts = line.Split(new[] { '\t' }, 2);
                if (parts.Length != 2)
                    continue;

                Guid id;
                if (Guid.TryParse(parts[0], out id))
                    result[id] = parts[1];
            }
            return result;
        }

        private void SaveRaw(Dictionary<Guid, string> values)
        {
            var temp = filePath + ".tmp";
            var lines = values.Select(x => x.Key.ToString("D") + "\t" + x.Value).ToArray();
            File.WriteAllLines(temp, lines, Encoding.UTF8);
            if (File.Exists(filePath))
                File.Replace(temp, filePath, null);
            else
                File.Move(temp, filePath);

            RestrictToCurrentUser(filePath);
        }

        /// <summary>
        /// Removes inherited permissions so only the current Windows user
        /// (plus SYSTEM, needed by backup/indexing) can open credentials.dat.
        /// </summary>
        internal static void RestrictToCurrentUser(string path)
        {
            try
            {
                var currentUser = WindowsIdentity.GetCurrent().User;
                if (currentUser == null)
                    return;

                var security = new FileSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
                File.SetAccessControl(path, security);
            }
            catch
            {
                // DPAPI encryption still protects the content if the ACL cannot be changed.
            }
        }
    }
}
