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
        private EpicLoginMode editEpicLoginMode = EpicLoginMode.Manual;
        private EALoginMode editEALoginMode = EALoginMode.Manual;
        private UbisoftLoginMode editUbisoftLoginMode = UbisoftLoginMode.Automatic;
        private GOGLoginMode editGOGLoginMode = GOGLoginMode.Manual;
        private BattleNetLoginMode editBattleNetLoginMode = BattleNetLoginMode.Manual;
        private bool editingExisting;

        public ObservableCollection<AccountListItem> Accounts { get; private set; }
        public ObservableCollection<ContextGameRow> ContextGameRows { get; private set; } = new ObservableCollection<ContextGameRow>();
        public bool HasContextGames => ContextGames.Count > 0;
        public bool NoContextGames => ContextGames.Count == 0;
        public string ContextBannerTitle { get; private set; }
        public string ContextBannerGames { get; private set; }
        public string ContextBannerAssignment { get; private set; }
        public bool HasContextBannerAssignment => !string.IsNullOrEmpty(ContextBannerAssignment);
        public string ContextMoreGames { get; private set; }
        public bool HasContextMoreGames => !string.IsNullOrEmpty(ContextMoreGames);
        public string LauncherNote { get; private set; }
        public bool HasLauncherNote => !string.IsNullOrEmpty(LauncherNote);
        public Guid LastSavedAccountId { get; private set; }
        public ObservableCollection<LauncherOption> Launchers { get; private set; }
        public List<Game> ContextGames { get; private set; }
        public ObservableCollection<EpicLoginModeOption> EpicLoginModes { get; private set; }
        public ObservableCollection<EALoginModeOption> EALoginModes { get; private set; }
        public ObservableCollection<UbisoftLoginModeOption> UbisoftLoginModes { get; private set; }
        public ObservableCollection<GOGLoginModeOption> GOGLoginModes { get; private set; }
        public ObservableCollection<BattleNetLoginModeOption> BattleNetLoginModes { get; private set; }

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
                OnPropertyChanged(nameof(IsEpicGamesLauncher));
                OnPropertyChanged(nameof(IsEAAppLauncher));
                OnPropertyChanged(nameof(IsUbisoftConnectLauncher));
                OnPropertyChanged(nameof(IsGOGGalaxyLauncher));
                OnPropertyChanged(nameof(IsBattleNetLauncher));
                UpdateLauncherNote();
            }
        }
        public bool EditAutoLogin { get => editAutoLogin; set => SetValue(ref editAutoLogin, value); }
        public bool EditLogoutAfterGame { get => editLogoutAfterGame; set => SetValue(ref editLogoutAfterGame, value); }
        public bool EditIsPrimary { get => editIsPrimary; set => SetValue(ref editIsPrimary, value); }
        public bool IsSteamLauncher => EditLauncher == LauncherType.Steam;
        public bool IsPrimaryEligibleLauncher => EditLauncher == LauncherType.Steam;
        public bool IsEpicGamesLauncher => EditLauncher == LauncherType.EpicGames;
        public bool IsEAAppLauncher => EditLauncher == LauncherType.EAApp;
        public bool IsUbisoftConnectLauncher => EditLauncher == LauncherType.UbisoftConnect;
        public bool IsGOGGalaxyLauncher => EditLauncher == LauncherType.GOGGalaxy;
        public bool IsBattleNetLauncher => EditLauncher == LauncherType.BattleNet;
        public EpicLoginMode EditEpicLoginMode { get => editEpicLoginMode; set => SetValue(ref editEpicLoginMode, value); }
        public EALoginMode EditEALoginMode { get => editEALoginMode; set => SetValue(ref editEALoginMode, value); }
        public UbisoftLoginMode EditUbisoftLoginMode { get => editUbisoftLoginMode; set => SetValue(ref editUbisoftLoginMode, value); }
        public GOGLoginMode EditGOGLoginMode { get => editGOGLoginMode; set => SetValue(ref editGOGLoginMode, value); }
        public BattleNetLoginMode EditBattleNetLoginMode { get => editBattleNetLoginMode; set => SetValue(ref editBattleNetLoginMode, value); }

        internal AccountManagerViewModel(AccountManagerStore store)
        {
            this.store = store;
            ContextGames = new List<Game>();
            Launchers = new ObservableCollection<LauncherOption>(Enum.GetValues(typeof(LauncherType)).Cast<LauncherType>().Select(x => new LauncherOption(x)));
            EpicLoginModes = new ObservableCollection<EpicLoginModeOption>(Enum.GetValues(typeof(EpicLoginMode)).Cast<EpicLoginMode>().Select(x => new EpicLoginModeOption(x)));
            EALoginModes = new ObservableCollection<EALoginModeOption>(Enum.GetValues(typeof(EALoginMode)).Cast<EALoginMode>().Select(x => new EALoginModeOption(x)));
            UbisoftLoginModes = new ObservableCollection<UbisoftLoginModeOption>(Enum.GetValues(typeof(UbisoftLoginMode)).Cast<UbisoftLoginMode>().Select(x => new UbisoftLoginModeOption(x)));
            GOGLoginModes = new ObservableCollection<GOGLoginModeOption>(Enum.GetValues(typeof(GOGLoginMode)).Cast<GOGLoginMode>().Select(x => new GOGLoginModeOption(x)));
            BattleNetLoginModes = new ObservableCollection<BattleNetLoginModeOption>(Enum.GetValues(typeof(BattleNetLoginMode)).Cast<BattleNetLoginMode>().Select(x => new BattleNetLoginModeOption(x)));
            Refresh();
            BeginNewAccount();
        }

        public void SetContextGames(IEnumerable<Game> games)
        {
            ContextGames = games == null ? new List<Game>() : games.Where(x => x != null).ToList();
            OnPropertyChanged(nameof(ContextGames));
            Refresh();
        }

        public void Refresh()
        {
            var assignedToContext = new HashSet<Guid>(ContextGames
                .Select(x => store.GetAssignment(x.Id))
                .Where(x => x != null)
                .Select(x => x.AccountId));

            Accounts = new ObservableCollection<AccountListItem>(
                store.Settings.Accounts.Select(x => new AccountListItem(x, assignedToContext.Contains(x.Id))));
            OnPropertyChanged(nameof(Accounts));
            RefreshContext();
        }

        /// <summary>Texts describing the games selected in Playnite and their current accounts.</summary>
        private void RefreshContext()
        {
            const int maxRows = 6;
            const int maxBannerNames = 3;

            ContextGameRows = new ObservableCollection<ContextGameRow>(ContextGames.Take(maxRows).Select(game =>
            {
                AccountRecord account = GetAssignedAccount(game);
                return new ContextGameRow
                {
                    Name = game.Name,
                    HasAssignment = account != null,
                    AssignmentText = account == null
                        ? L("LOCPlayniteAccountManagerNoAssignment")
                        : account.Name + "  \u2022  " + account.Launcher.GetDisplayName()
                };
            }));

            int count = ContextGames.Count;
            ContextBannerTitle = count == 1
                ? L("LOCPlayniteAccountManagerGameToAssign")
                : string.Format(L("LOCPlayniteAccountManagerGamesToAssign"), count);

            ContextBannerGames = string.Join(", ", ContextGames.Take(maxBannerNames).Select(x => x.Name));
            if (count > maxBannerNames)
                ContextBannerGames += " " + string.Format(L("LOCPlayniteAccountManagerMoreGames"), count - maxBannerNames);

            ContextBannerAssignment = null;
            if (count == 1)
            {
                AccountRecord account = GetAssignedAccount(ContextGames[0]);
                ContextBannerAssignment = account == null
                    ? L("LOCPlayniteAccountManagerNoAssignment")
                    : string.Format(L("LOCPlayniteAccountManagerCurrentAssignment"),
                        account.Name + "  \u2022  " + account.Launcher.GetDisplayName());
            }

            ContextMoreGames = count > maxRows
                ? string.Format(L("LOCPlayniteAccountManagerMoreGames"), count - maxRows)
                : null;

            OnPropertyChanged(nameof(ContextGameRows));
            OnPropertyChanged(nameof(HasContextGames));
            OnPropertyChanged(nameof(NoContextGames));
            OnPropertyChanged(nameof(ContextBannerTitle));
            OnPropertyChanged(nameof(ContextBannerGames));
            OnPropertyChanged(nameof(ContextBannerAssignment));
            OnPropertyChanged(nameof(HasContextBannerAssignment));
            OnPropertyChanged(nameof(ContextMoreGames));
            OnPropertyChanged(nameof(HasContextMoreGames));
        }

        private AccountRecord GetAssignedAccount(Game game)
        {
            GameAccountAssignment assignment = store.GetAssignment(game.Id);
            return assignment == null ? null : store.GetAccount(assignment.AccountId);
        }

        /// <summary>Explains launchers that have no login-mode choice.</summary>
        private void UpdateLauncherNote()
        {
            switch (EditLauncher)
            {
                case LauncherType.Steam:
                    LauncherNote = L("LOCPlayniteAccountManagerSteamNote");
                    break;
                case LauncherType.RockstarGamesLauncher:
                    LauncherNote = L("LOCPlayniteAccountManagerRockstarNote");
                    break;
                case LauncherType.XboxApp:
                    LauncherNote = L("LOCPlayniteAccountManagerXboxNote");
                    break;
                case LauncherType.Other:
                    LauncherNote = L("LOCPlayniteAccountManagerOtherNote");
                    break;
                default:
                    LauncherNote = null;
                    break;
            }

            OnPropertyChanged(nameof(LauncherNote));
            OnPropertyChanged(nameof(HasLauncherNote));
        }

        private static string L(string key) => ResourceProvider.GetString(key);

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
            EditEpicLoginMode = EpicLoginMode.Manual;
            EditEALoginMode = EALoginMode.Manual;
            EditUbisoftLoginMode = UbisoftLoginMode.Automatic;
            EditGOGLoginMode = GOGLoginMode.Manual;
            EditBattleNetLoginMode = BattleNetLoginMode.Manual;
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
            EditEpicLoginMode = model.EpicLoginMode;
            EditEALoginMode = model.EALoginMode;
            EditUbisoftLoginMode = model.UbisoftLoginMode;
            EditGOGLoginMode = model.GOGLoginMode;
            EditBattleNetLoginMode = model.BattleNetLoginMode;
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
                error = L("LOCPlayniteAccountManagerErrorName");
                return false;
            }
            if (string.IsNullOrWhiteSpace(EditUserName))
            {
                error = L("LOCPlayniteAccountManagerErrorLogin");
                return false;
            }
            // Xbox app accounts sign in through the Windows account picker; the
            // password is optional (only used in the manual fallback window).
            if (!editingExisting && string.IsNullOrEmpty(EditPassword) && EditLauncher != LauncherType.XboxApp)
            {
                error = L("LOCPlayniteAccountManagerErrorPassword");
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
            record.EpicLoginMode = record.Launcher == LauncherType.EpicGames
                ? EditEpicLoginMode
                : EpicLoginMode.Manual;
            record.EALoginMode = record.Launcher == LauncherType.EAApp
                ? EditEALoginMode
                : EALoginMode.Manual;
            record.UbisoftLoginMode = record.Launcher == LauncherType.UbisoftConnect
                ? EditUbisoftLoginMode
                : UbisoftLoginMode.Automatic;
            record.GOGLoginMode = record.Launcher == LauncherType.GOGGalaxy
                ? EditGOGLoginMode
                : GOGLoginMode.Manual;
            record.BattleNetLoginMode = record.Launcher == LauncherType.BattleNet
                ? EditBattleNetLoginMode
                : BattleNetLoginMode.Manual;
            record.IsPrimary = IsPrimaryEligibleLauncher && EditIsPrimary;

            if (record.Launcher == LauncherType.Steam && record.IsPrimary)
                store.SetPrimarySteamAccount(record.Id);
            else if (wasPrimary && record.Launcher == LauncherType.Steam && !record.IsPrimary)
                store.ClearPrimarySteamAccount(record.Id);

            if (!string.IsNullOrEmpty(EditPassword))
                store.Credentials.Set(record.Id, EditPassword);

            store.Save();
            editingExisting = true;
            LastSavedAccountId = record.Id;
            return true;
        }
    }
}
