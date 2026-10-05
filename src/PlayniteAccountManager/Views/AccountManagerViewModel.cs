using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAccountManager.Models;
using PlayniteAccountManager.Services;

namespace PlayniteAccountManager.Views
{
    public sealed class AccountManagerViewModel : ObservableObject
    {
        private readonly AccountManagerStore store;
        private AccountListItem selectedAccount;
        private string editName;
        private string editUserName;
        private string editPassword;
        private LauncherType editLauncher;
        private bool editAutoLogin = true;
        private bool editLogoutAfterGame = true;
        private bool editIsPrimary;
        private bool editingExisting;

        public ObservableCollection<AccountListItem> Accounts { get; private set; }
        public ObservableCollection<LauncherOption> Launchers { get; private set; }
        public List<Game> ContextGames { get; private set; }

        public AccountListItem SelectedAccount
        {
            get => selectedAccount;
            set => SetValue(ref selectedAccount, value);
        }

        public string EditName { get => editName; set => SetValue(ref editName, value); }
        public string EditUserName { get => editUserName; set => SetValue(ref editUserName, value); }
        public string EditPassword { get => editPassword; set => SetValue(ref editPassword, value); }
        public LauncherType EditLauncher
        {
            get => editLauncher;
            set
            {
                if (editLauncher == value)
                    return;

                editLauncher = value;
                OnPropertyChanged(nameof(EditLauncher));
                OnPropertyChanged(nameof(IsSteamLauncher));
                OnPropertyChanged(nameof(IsPrimaryEligibleLauncher));
            }
        }
        public bool EditAutoLogin { get => editAutoLogin; set => SetValue(ref editAutoLogin, value); }
        public bool EditLogoutAfterGame { get => editLogoutAfterGame; set => SetValue(ref editLogoutAfterGame, value); }
        public bool EditIsPrimary { get => editIsPrimary; set => SetValue(ref editIsPrimary, value); }
        public bool IsSteamLauncher => EditLauncher == LauncherType.Steam;
        public bool IsPrimaryEligibleLauncher => EditLauncher == LauncherType.Steam;

        internal AccountManagerViewModel(AccountManagerStore store)
        {
            this.store = store;
            ContextGames = new List<Game>();
            Launchers = new ObservableCollection<LauncherOption>(Enum.GetValues(typeof(LauncherType)).Cast<LauncherType>().Select(x => new LauncherOption(x)));
            Refresh();
            BeginNewAccount();
        }

        public void SetContextGames(IEnumerable<Game> games)
        {
            ContextGames = games == null ? new List<Game>() : games.Where(x => x != null).ToList();
            OnPropertyChanged(nameof(ContextGames));
        }

        public void Refresh()
        {
            Accounts = new ObservableCollection<AccountListItem>(store.Settings.Accounts.Select(x => new AccountListItem(x)));
            OnPropertyChanged(nameof(Accounts));
        }

        public void BeginNewAccount()
        {
            SelectedAccount = null;
            editingExisting = false;
            EditName = string.Empty;
            EditUserName = string.Empty;
            EditPassword = string.Empty;
            EditLauncher = LauncherType.UbisoftConnect;
            EditAutoLogin = true;
            EditLogoutAfterGame = true;
            EditIsPrimary = false;
        }

        public void LoadSelectedAccount()
        {
            var model = SelectedAccount == null ? null : store.GetAccount(SelectedAccount.Id);
            if (model == null)
                return;

            editingExisting = true;
            EditName = model.Name;
            EditUserName = model.UserName;
            EditPassword = string.Empty;
            EditLauncher = model.Launcher;
            EditIsPrimary = model.IsPrimary;

            var assignment = ContextGames.Count == 1 ? store.GetAssignment(ContextGames[0].Id) : null;
            EditAutoLogin = assignment == null || assignment.AutoLogin;
            EditLogoutAfterGame = assignment == null || assignment.LogoutAfterGame;
        }

        public bool SaveCurrent(out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(EditName))
            {
                error = "Podaj nazwę konta.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(EditUserName))
            {
                error = "Podaj login lub adres e-mail.";
                return false;
            }
            if (!editingExisting && string.IsNullOrEmpty(EditPassword))
            {
                error = "Podaj hasło dla nowego konta.";
                return false;
            }

            AccountRecord record;
            if (editingExisting && SelectedAccount != null)
                record = store.GetAccount(SelectedAccount.Id);
            else
            {
                record = new AccountRecord();
                store.Settings.Accounts.Add(record);
            }

            bool wasPrimary = record.IsPrimary;

            record.Name = EditName.Trim();
            record.UserName = EditUserName.Trim();
            record.Launcher = EditLauncher;
            record.IsPrimary = IsPrimaryEligibleLauncher && EditIsPrimary;

            if (record.Launcher == LauncherType.Steam && record.IsPrimary)
                store.SetPrimarySteamAccount(record.Id);
            else if (wasPrimary && record.Launcher == LauncherType.Steam && !record.IsPrimary)
                store.ClearPrimarySteamAccount(record.Id);

            if (!string.IsNullOrEmpty(EditPassword))
                store.Credentials.Set(record.Id, EditPassword);

            store.Save();
            editingExisting = true;
            return true;
        }
    }
}
