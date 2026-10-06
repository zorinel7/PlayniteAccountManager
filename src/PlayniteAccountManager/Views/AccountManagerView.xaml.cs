using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAccountManager.Models;
using PlayniteAccountManager.Services;

namespace PlayniteAccountManager.Views
{
    public partial class AccountManagerView : UserControl
    {
        private static string L(string key) => ResourceProvider.GetString(key);

        private readonly PlayniteAccountManagerPlugin plugin;
        private readonly AccountManagerStore store;
        private readonly AccountManagerViewModel vm;

        public AccountManagerView(PlayniteAccountManagerPlugin plugin)
        {
            InitializeComponent();
            this.plugin = plugin;
            store = plugin.Store;
            vm = new AccountManagerViewModel(store);
            DataContext = vm;
            UpdateButtons();
        }

        public void SetContextGames(System.Collections.Generic.IEnumerable<Game> games)
        {
            vm.SetContextGames(games);
            UpdateStatus();
        }

        private void NewAccountButton_Click(object sender, RoutedEventArgs e)
        {
            vm.BeginNewAccount();
            PasswordBox.Password = string.Empty;
            AutoLoginCheck.IsChecked = true;
            LogoutCheck.IsChecked = true;
            PrimaryAccountCheck.IsChecked = false;
            EpicLoginModeCombo.SelectedValue = EpicLoginMode.Manual;
            EALoginModeCombo.SelectedValue = EALoginMode.Manual;
            UbisoftLoginModeCombo.SelectedValue = UbisoftLoginMode.Automatic;
            EditorStatusText.Text = L("LOCPlayniteAccountManagerNewAccount");
            UpdateButtons();
            UpdateStatus();
        }

        private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (vm.SelectedAccount != null)
            {
                vm.LoadSelectedAccount();
                PasswordBox.Password = string.Empty;
                AutoLoginCheck.IsChecked = vm.EditAutoLogin;
                LogoutCheck.IsChecked = vm.EditLogoutAfterGame;
                PrimaryAccountCheck.IsChecked = vm.EditIsPrimary;
                EpicLoginModeCombo.SelectedValue = vm.EditEpicLoginMode;
                EALoginModeCombo.SelectedValue = vm.EditEALoginMode;
                UbisoftLoginModeCombo.SelectedValue = vm.EditUbisoftLoginMode;
                EditorStatusText.Text = string.Format(L("LOCPlayniteAccountManagerEditAccount"), vm.EditName);
            }
            UpdateButtons();
            UpdateStatus();
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            vm.EditPassword = PasswordBox.Password;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            string error;
            if (!vm.SaveCurrent(out error))
            {
                MessageBox.Show(error, L("LOCPlayniteAccountManagerTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            PasswordBox.Password = string.Empty;
            vm.Refresh();
            UpdateButtons();
            UpdateStatus(L("LOCPlayniteAccountManagerAccountSaved"));
        }

        private async void TestLoginButton_Click(object sender, RoutedEventArgs e)
        {
            var account = vm.SelectedAccount;
            if (account == null)
            {
                MessageBox.Show(L("LOCPlayniteAccountManagerSelectAccountFirst"), L("LOCPlayniteAccountManagerTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var model = store.GetAccount(account.Id);
            bool manualLauncher = model != null &&
                                  (model.Launcher == LauncherType.GOGGalaxy ||
                                   (model.Launcher == LauncherType.UbisoftConnect &&
                                    model.UbisoftLoginMode == UbisoftLoginMode.Manual) ||
                                   (model.Launcher == LauncherType.EpicGames &&
                                    model.EpicLoginMode == EpicLoginMode.Manual) ||
                                   (model.Launcher == LauncherType.EAApp &&
                                    model.EALoginMode == EALoginMode.Manual));

            bool automaticLauncher = model != null &&
                                     (model.Launcher == LauncherType.UbisoftConnect ||
                                      model.Launcher == LauncherType.Steam ||
                                      (model.Launcher == LauncherType.EpicGames &&
                                       model.EpicLoginMode == EpicLoginMode.Automatic) ||
                                      (model.Launcher == LauncherType.EAApp &&
                                       model.EALoginMode == EALoginMode.Automatic));

            if (model == null || (!manualLauncher && !automaticLauncher))
            {
                MessageBox.Show(
                    L("LOCPlayniteAccountManagerAutoUnavailable"),
                    L("LOCPlayniteAccountManagerTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (manualLauncher)
            {
                string manualError;
                bool manualOk = plugin.ShowManualLoginForTest(model.Id, out manualError);

                UpdateStatus(manualOk
                    ? L("LOCPlayniteAccountManagerManualDataShown")
                    : L("LOCPlayniteAccountManagerManualDataFail"));

                if (!manualOk && !string.IsNullOrWhiteSpace(manualError))
                {
                    MessageBox.Show(
                        manualError,
                        model.Launcher.GetDisplayName(),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                UpdateButtons();
                return;
            }

            TestLoginButton.IsEnabled = false;
            UpdateStatus(
                string.Format(L("LOCPlayniteAccountManagerAutoRunning"), model.Launcher.GetDisplayName()));

            try
            {
                string error = null;
                bool ok = await System.Threading.Tasks.Task.Run(
                    () => plugin.TryTestLogin(model.Id, out error));

                UpdateStatus(
                    ok
                        ? string.Format(L("LOCPlayniteAccountManagerAutoSuccess"), model.Launcher.GetDisplayName())
                        : L("LOCPlayniteAccountManagerAutoFail"));

                if (!ok && !string.IsNullOrWhiteSpace(error))
                {
                    MessageBox.Show(
                        error,
                        model.Launcher.GetDisplayName(),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                UpdateButtons();
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var account = vm.SelectedAccount;
            if (account == null)
                return;

            var answer = MessageBox.Show(
                string.Format(L("LOCPlayniteAccountManagerDeleteConfirm"), account.DisplayText),
                L("LOCPlayniteAccountManagerDeleteTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            store.DeleteAccount(account.Id);
            vm.Refresh();
            vm.BeginNewAccount();
            PasswordBox.Password = string.Empty;
            EditorStatusText.Text = L("LOCPlayniteAccountManagerNewAccount");
            UpdateButtons();
            UpdateStatus(L("LOCPlayniteAccountManagerAccountDeleted"));
        }

        private void AssignButton_Click(object sender, RoutedEventArgs e)
        {
            var account = vm.SelectedAccount;
            if (account == null)
                return;

            var games = vm.ContextGames.ToList();
            if (games.Count == 0)
            {
                MessageBox.Show(
                    L("LOCPlayniteAccountManagerSelectGamesFirst"),
                    L("LOCPlayniteAccountManagerTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            foreach (var game in games)
                store.SetAssignment(
                    game.Id,
                    account.Id,
                    vm.EditAutoLogin,
                    vm.EditLogoutAfterGame);

            UpdateStatus(
                string.Format(L("LOCPlayniteAccountManagerAccountAssigned"), account.Name, games.Count));
        }

        private void UpdateButtons()
        {
            var has = vm.SelectedAccount != null;
            DeleteButton.IsEnabled = has;
            AssignButton.IsEnabled = has && vm.ContextGames.Any();

            var selectedModel = has
                ? store.GetAccount(vm.SelectedAccount.Id)
                : null;

            bool supported = selectedModel != null &&
                             (selectedModel.Launcher == LauncherType.UbisoftConnect ||
                              selectedModel.Launcher == LauncherType.Steam ||
                              selectedModel.Launcher == LauncherType.EAApp ||
                              selectedModel.Launcher == LauncherType.EpicGames ||
                              selectedModel.Launcher == LauncherType.GOGGalaxy);

            TestLoginButton.IsEnabled = supported;

            TestLoginButton.Content =
                selectedModel != null &&
                ((selectedModel.Launcher == LauncherType.GOGGalaxy) ||
                 (selectedModel.Launcher == LauncherType.BattleNet) ||
                 (selectedModel.Launcher == LauncherType.UbisoftConnect &&
                  selectedModel.UbisoftLoginMode == UbisoftLoginMode.Manual) ||
                 (selectedModel.Launcher == LauncherType.EpicGames &&
                  selectedModel.EpicLoginMode == EpicLoginMode.Manual) ||
                 (selectedModel.Launcher == LauncherType.EAApp &&
                  selectedModel.EALoginMode == EALoginMode.Manual))
                    ? L("LOCPlayniteAccountManagerTestManual")
                    : L("LOCPlayniteAccountManagerTestAuto");

            EmptyAccountsText.Visibility =
                vm.Accounts != null && vm.Accounts.Count > 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }

        private void UpdateStatus(string custom = null)
        {
            if (!string.IsNullOrEmpty(custom))
            {
                StatusText.Text = custom;
                return;
            }

            var count = vm.ContextGames.Count;
            StatusText.Text = count > 0
                ? string.Format(L("LOCPlayniteAccountManagerSelectedGames"), count)
                : L("LOCPlayniteAccountManagerSelectGames");
        }
    }

    public sealed class LauncherOption
    {
        public LauncherType Value { get; }
        public string DisplayName { get; }

        public LauncherOption(LauncherType value)
        {
            Value = value;
            DisplayName = value.GetDisplayName();
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class EpicLoginModeOption
    {
        public EpicLoginMode Value { get; }
        public string DisplayName { get; }

        public EpicLoginModeOption(EpicLoginMode value)
        {
            Value = value;
            DisplayName = value == EpicLoginMode.Automatic
                ? ResourceProvider.GetString("LOCPlayniteAccountManagerAutomaticLogin")
                : ResourceProvider.GetString("LOCPlayniteAccountManagerManualLogin");
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class EALoginModeOption
    {
        public EALoginMode Value { get; }
        public string DisplayName { get; }

        public EALoginModeOption(EALoginMode value)
        {
            Value = value;
            DisplayName = value == EALoginMode.Automatic
                ? ResourceProvider.GetString("LOCPlayniteAccountManagerAutomaticLogin")
                : ResourceProvider.GetString("LOCPlayniteAccountManagerManualLogin");
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class UbisoftLoginModeOption
    {
        public UbisoftLoginMode Value { get; }
        public string DisplayName { get; }

        public UbisoftLoginModeOption(UbisoftLoginMode value)
        {
            Value = value;
            DisplayName = value == UbisoftLoginMode.Automatic
                ? ResourceProvider.GetString("LOCPlayniteAccountManagerAutomaticLogin")
                : ResourceProvider.GetString("LOCPlayniteAccountManagerManualLogin");
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class AccountListItem
    {
        public Guid Id { get; }
        public string Name { get; }
        public string UserName { get; }
        public string DisplayText { get; }
        public string DisplayLauncher { get; }

        public AccountListItem(AccountRecord record)
        {
            Id = record.Id;
            Name = record.Name;
            UserName = record.UserName;
            DisplayText = record.Name + "  •  " + record.Launcher.GetDisplayName();
            DisplayLauncher =
                record.Launcher.GetDisplayName() +
                ((record.Launcher == LauncherType.Steam) && record.IsPrimary
                    ? "  •  " + ResourceProvider.GetString("LOCPlayniteAccountManagerPrimarySteamShort")
                    : "");

            if (record.Launcher == LauncherType.EpicGames)
                DisplayLauncher += record.EpicLoginMode == EpicLoginMode.Automatic
                    ? "  •  " + ResourceProvider.GetString("LOCPlayniteAccountManagerAutoShort")
                    : "  •  " + ResourceProvider.GetString("LOCPlayniteAccountManagerManualShort");

            if (record.Launcher == LauncherType.EAApp)
                DisplayLauncher += record.EALoginMode == EALoginMode.Automatic
                    ? "  •  " + ResourceProvider.GetString("LOCPlayniteAccountManagerAutoShort")
                    : "  •  " + ResourceProvider.GetString("LOCPlayniteAccountManagerManualShort");

            if (record.Launcher == LauncherType.UbisoftConnect)
                DisplayLauncher += record.UbisoftLoginMode == UbisoftLoginMode.Automatic
                    ? "  •  " + ResourceProvider.GetString("LOCPlayniteAccountManagerAutoShort")
                    : "  •  " + ResourceProvider.GetString("LOCPlayniteAccountManagerManualShort");

            if (record.Launcher == LauncherType.GOGGalaxy)
                DisplayLauncher += "  •  " +
                                   ResourceProvider.GetString("LOCPlayniteAccountManagerManualShort");

            if (record.Launcher == LauncherType.BattleNet)
                DisplayLauncher += "  •  " +
                                   ResourceProvider.GetString("LOCPlayniteAccountManagerManualShort");

            if (!string.IsNullOrWhiteSpace(record.UserName))
                DisplayText += "  •  " + record.UserName;
        }
    }
}
