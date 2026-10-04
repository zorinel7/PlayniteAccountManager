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
            EditorStatusText.Text = "Nowe konto";
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
                EditorStatusText.Text = "Edycja: " + vm.EditName;
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
                MessageBox.Show(error, "Menadżer Kont", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            PasswordBox.Password = string.Empty;
            vm.Refresh();
            UpdateButtons();
            UpdateStatus("Konto zapisane.");
        }

        private async void TestLoginButton_Click(object sender, RoutedEventArgs e)
        {
            var account = vm.SelectedAccount;
            if (account == null)
            {
                MessageBox.Show("Najpierw wybierz zapisane konto.", "Menadżer Kont", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var model = store.GetAccount(account.Id);
            if (model == null || (model.Launcher != LauncherType.UbisoftConnect && model.Launcher != LauncherType.Steam && model.Launcher != LauncherType.EAApp && model.Launcher != LauncherType.EpicGames && model.Launcher != LauncherType.EpicGames))
            {
                MessageBox.Show("Automatyczne logowanie nie jest jeszcze dostępne dla tego launchera.", "Menadżer Kont", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // EA needs one elevated helper task because its background
            // service owns protected ProgramData. Playnite itself remains
            // non-elevated. The setup is integrated here and happens once.
            if (model.Launcher == LauncherType.EAApp && !plugin.IsEAHelperReady())
            {
                UpdateStatus("Pierwsza konfiguracja EA App — pojawi się jednorazowy monit UAC...");
                string setupError;
                if (!plugin.EnsureEAHelper(out setupError))
                {
                    UpdateStatus("Nie skonfigurowano pomocnika EA.");
                    if (!string.IsNullOrWhiteSpace(setupError))
                        MessageBox.Show(setupError, "EA App", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            TestLoginButton.IsEnabled = false;
            UpdateStatus("Uruchamiam " + model.Launcher.GetDisplayName() + " i automatycznie wprowadzam dane logowania...");

            try
            {
                string error = null;
                bool ok = await System.Threading.Tasks.Task.Run(() => plugin.TryTestLogin(model.Id, out error));
                UpdateStatus(ok ? "Automatyczne logowanie " + model.Launcher.GetDisplayName() + " zakończone pomyślnie." : "Nie udało się zalogować automatycznie.");
                if (!ok && !string.IsNullOrWhiteSpace(error))
                    MessageBox.Show(error, model.Launcher.GetDisplayName(), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                UpdateButtons();
            }
        }

        private void EAHelperButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Konfiguruję jednorazowy pomocnik EA App...");
            string error;

            if (plugin.EnsureEAHelper(out error))
            {
                UpdateStatus("EA App skonfigurowana do pracy bez administratora.");
                UpdateButtons();
            }
            else
            {
                UpdateStatus("Nie skonfigurowano pomocnika EA.");
                if (!string.IsNullOrWhiteSpace(error))
                    MessageBox.Show(error, "EA App", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var account = vm.SelectedAccount;
            if (account == null)
                return;

            var answer = MessageBox.Show(
                "Usunąć konto „" + account.DisplayText + "”?\n\nPrzypisania tego konta do gier również zostaną usunięte.",
                "Usuń konto",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            store.DeleteAccount(account.Id);
            vm.Refresh();
            vm.BeginNewAccount();
            PasswordBox.Password = string.Empty;
            EditorStatusText.Text = "Nowe konto";
            UpdateButtons();
            UpdateStatus("Konto usunięte.");
        }

        private void AssignButton_Click(object sender, RoutedEventArgs e)
        {
            var account = vm.SelectedAccount;
            if (account == null)
                return;

            var games = vm.ContextGames.ToList();
            if (games.Count == 0)
            {
                MessageBox.Show("Najpierw zaznacz co najmniej jedną grę w Playnite.", "Menadżer Kont", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var game in games)
            {
                store.SetAssignment(game.Id, account.Id, vm.EditAutoLogin, vm.EditLogoutAfterGame);
            }

            UpdateStatus("Konto „" + account.Name + "” przypisano do " + games.Count + " gier.");
        }

        private void UpdateButtons()
        {
            var has = vm.SelectedAccount != null;
            DeleteButton.IsEnabled = has;
            AssignButton.IsEnabled = has && vm.ContextGames.Any();
            var selectedModel = has ? store.GetAccount(vm.SelectedAccount.Id) : null;
            bool supported = selectedModel != null && (selectedModel.Launcher == LauncherType.UbisoftConnect || selectedModel.Launcher == LauncherType.Steam || selectedModel.Launcher == LauncherType.EAApp || selectedModel.Launcher == LauncherType.EpicGames || selectedModel.Launcher == LauncherType.EpicGames);
            TestLoginButton.IsEnabled = supported;

            bool showEASetup = selectedModel != null && selectedModel.Launcher == LauncherType.EAApp;
            EAHelperButton.Visibility = showEASetup ? Visibility.Visible : Visibility.Collapsed;
            if (showEASetup)
            {
                bool ready = plugin.IsEAHelperReady();
                EAHelperButton.Content = ready
                    ? "EA: TRYB BEZ ADMINISTRATORA ✓"
                    : "EA: KONFIGURUJ BEZ ADMINISTRATORA";
                EAHelperButton.IsEnabled = !ready;
            }

            EmptyAccountsText.Visibility = vm.Accounts != null && vm.Accounts.Count > 0
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
                ? "Zaznaczone gry w Playnite: " + count + "."
                : "Zaznacz grę lub gry w Playnite, aby przypisać konto.";
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
            DisplayLauncher = record.Launcher.GetDisplayName() + ((record.Launcher == LauncherType.Steam || record.Launcher == LauncherType.EAApp || record.Launcher == LauncherType.EpicGames) && record.IsPrimary ? "  •  GŁÓWNE" : "");
            if (!string.IsNullOrWhiteSpace(record.UserName))
                DisplayText += "  •  " + record.UserName;
        }
    }
}
