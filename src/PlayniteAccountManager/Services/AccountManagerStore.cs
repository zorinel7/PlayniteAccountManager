using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Services
{
    internal sealed class AccountManagerStore
    {
        private readonly GenericPlugin plugin;
        private readonly SecureCredentialStore credentials;
        private AccountManagerSettings settings;

        public AccountManagerSettings Settings => settings;
        public SecureCredentialStore Credentials => credentials;

        public AccountManagerStore(GenericPlugin plugin)
        {
            this.plugin = plugin;
            settings = plugin.LoadPluginSettings<AccountManagerSettings>() ?? new AccountManagerSettings();
            if (settings.Accounts == null) settings.Accounts = new List<AccountRecord>();
            if (settings.Assignments == null) settings.Assignments = new List<GameAccountAssignment>();
            credentials = new SecureCredentialStore(plugin.GetPluginUserDataPath());
        }

        public void Save()
        {
            plugin.SavePluginSettings(settings);
        }

        public AccountRecord GetAccount(Guid accountId)
        {
            return settings.Accounts.FirstOrDefault(x => x.Id == accountId);
        }

        public AccountRecord GetPrimarySteamAccount()
        {
            return settings.Accounts.FirstOrDefault(x => x.Launcher == LauncherType.Steam && x.IsPrimary);
        }


        public AccountRecord GetPrimaryEAAccount()
        {
            return settings.Accounts.FirstOrDefault(x => x.Launcher == LauncherType.EAApp && x.IsPrimary);
        }

        public AccountRecord EnsurePrimaryEAAccount()
        {
            var primary = GetPrimaryEAAccount();
            if (primary != null)
                return primary;

            var firstEa = settings.Accounts.FirstOrDefault(x => x.Launcher == LauncherType.EAApp);
            if (firstEa != null)
            {
                firstEa.IsPrimary = true;
                Save();
                return firstEa;
            }

            return null;
        }

        public void SetPrimaryEAAccount(Guid accountId)
        {
            foreach (var account in settings.Accounts.Where(x => x.Launcher == LauncherType.EAApp))
                account.IsPrimary = account.Id == accountId;
            Save();
        }

        public void ClearPrimaryEAAccount(Guid accountId)
        {
            var account = GetAccount(accountId);
            if (account != null && account.Launcher == LauncherType.EAApp)
                account.IsPrimary = false;
            Save();
        }
        public void SetPrimarySteamAccount(Guid accountId)
        {
            foreach (var account in settings.Accounts.Where(x => x.Launcher == LauncherType.Steam))
                account.IsPrimary = account.Id == accountId;

            Save();
        }

        public void ClearPrimarySteamAccount(Guid accountId)
        {
            var account = GetAccount(accountId);
            if (account != null && account.Launcher == LauncherType.Steam)
                account.IsPrimary = false;
            Save();
        }

        public AccountRecord EnsurePrimarySteamAccount()
        {
            var primary = GetPrimarySteamAccount();
            if (primary != null)
                return primary;

            var firstSteam = settings.Accounts.FirstOrDefault(x => x.Launcher == LauncherType.Steam);
            if (firstSteam != null)
            {
                firstSteam.IsPrimary = true;
                Save();
                return firstSteam;
            }

            return null;
        }

        public GameAccountAssignment GetAssignment(Guid gameId)
        {
            return settings.Assignments.FirstOrDefault(x => x.GameId == gameId);
        }

        public void SetAssignment(Guid gameId, Guid accountId, bool autoLogin, bool logoutAfterGame)
        {
            var current = GetAssignment(gameId);
            if (current == null)
            {
                current = new GameAccountAssignment { GameId = gameId };
                settings.Assignments.Add(current);
            }

            current.AccountId = accountId;
            current.AutoLogin = autoLogin;
            current.LogoutAfterGame = logoutAfterGame;
            Save();
        }

        public void ClearAssignment(Guid gameId)
        {
            settings.Assignments.RemoveAll(x => x.GameId == gameId);
            Save();
        }

        public void DeleteAccount(Guid accountId)
        {
            settings.Accounts.RemoveAll(x => x.Id == accountId);
            settings.Assignments.RemoveAll(x => x.AccountId == accountId);
            credentials.Delete(accountId);
            Save();
        }

        public void RemoveOrphanedAssignments()
        {
            var valid = new HashSet<Guid>(settings.Accounts.Select(x => x.Id));
            settings.Assignments.RemoveAll(x => !valid.Contains(x.AccountId));
            Save();
        }
    }
}
