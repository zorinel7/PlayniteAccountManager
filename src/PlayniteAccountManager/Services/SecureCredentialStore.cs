using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PlayniteAccountManager.Services
{
    internal sealed class SecureCredentialStore
    {
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
                    var protectedBytes = ProtectedData.Protect(
                        Encoding.UTF8.GetBytes(password),
                        null,
                        DataProtectionScope.CurrentUser);
                    values[accountId] = Convert.ToBase64String(protectedBytes);
                }
                SaveRaw(values);
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
                    var bytes = ProtectedData.Unprotect(
                        Convert.FromBase64String(encoded),
                        null,
                        DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return string.Empty;
                }
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
        }
    }
}
