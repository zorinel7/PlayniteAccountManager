using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Xbox app account switching. The account login (UserName) is the e-mail of the
    /// Microsoft account; it must be added to Windows so it can be selected in the
    /// Windows account picker without a password. The gamertag seen after the first
    /// successful switch is remembered (not secret) so a later start with the same
    /// account skips the sign-out/sign-in round trip.
    /// </summary>
    internal sealed class XboxAppAdapter
    {
        private readonly Action<string> log;
        private readonly XboxAppUiAutomation automation;
        private readonly string gamertagFile;
        private readonly object sync = new object();

        public XboxAppAdapter(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            automation = new XboxAppUiAutomation(this.log);
            gamertagFile = Path.Combine(pluginUserDataPath, "xbox-gamertags.txt");
        }

        public bool PrepareAndLogin(AccountRecord account, out string error)
        {
            error = null;

            if (account == null || account.Launcher != LauncherType.XboxApp)
            {
                error = "Wybrane konto nie jest kontem Xbox App.";
                return false;
            }

            string email = (account.UserName ?? string.Empty).Trim();
            if (email.IndexOf('@') <= 0)
            {
                error = "Konto „" + account.Name + "” wymaga adresu e-mail konta Microsoft jako loginu.";
                return false;
            }

            string known = GetGamertag(account.Id);
            string gamertag;
            XboxSwitchResult result = automation.SwitchAccount(email, known, out gamertag, out error);

            if (result == XboxSwitchResult.Switched || result == XboxSwitchResult.AlreadySignedIn)
            {
                if (!string.IsNullOrEmpty(gamertag))
                    SetGamertag(account.Id, gamertag);

                log("Xbox: konto „" + account.Name + "” jest aktywne w aplikacji Xbox.");
                return true;
            }

            return false;
        }

        public bool EnsureAppOpen(out string error)
        {
            return automation.EnsureAppOpen(out error);
        }

        private string GetGamertag(Guid accountId)
        {
            string value;
            return ReadMap().TryGetValue(accountId, out value) ? value : null;
        }

        private void SetGamertag(Guid accountId, string gamertag)
        {
            try
            {
                lock (sync)
                {
                    Dictionary<Guid, string> map = ReadMap();
                    map[accountId] = gamertag;
                    Directory.CreateDirectory(Path.GetDirectoryName(gamertagFile));
                    File.WriteAllLines(gamertagFile,
                        map.Select(x => x.Key.ToString("D") + "\t" + x.Value),
                        new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                log("Xbox: nie udało się zapisać gamertagu: " + ex.Message);
            }
        }

        private Dictionary<Guid, string> ReadMap()
        {
            var map = new Dictionary<Guid, string>();
            try
            {
                lock (sync)
                {
                    if (!File.Exists(gamertagFile))
                        return map;

                    foreach (string line in File.ReadAllLines(gamertagFile, Encoding.UTF8))
                    {
                        string[] parts = line.Split(new[] { '\t' }, 2);
                        Guid id;
                        if (parts.Length == 2 && Guid.TryParse(parts[0], out id))
                            map[id] = parts[1];
                    }
                }
            }
            catch
            {
            }

            return map;
        }
    }
}
