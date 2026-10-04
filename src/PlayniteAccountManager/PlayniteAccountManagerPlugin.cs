using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;
using Playnite.SDK.Events;
using PlayniteAccountManager.Models;
using PlayniteAccountManager.Services;
using PlayniteAccountManager.Views;

namespace PlayniteAccountManager
{
    public class PlayniteAccountManagerPlugin : GenericPlugin
    {
        public override Guid Id { get; } = Guid.Parse("2A6DE1EF-2EF4-4D60-A0FD-4AFBD8F5B72C");

        internal AccountManagerStore Store { get; private set; }
        private ILogger logger;
        private UbisoftConnectAdapter ubisoft;
        private SteamAdapter steam;
        private EAAppAdapter ea;
        private Guid activeAccountId;
        private Guid activeGameId;
        private LauncherType activeLauncher;
        private bool activeLogoutAfterGame;

        public PlayniteAccountManagerPlugin(IPlayniteAPI api) : base(api)
        {
            logger = LogManager.GetLogger("MenadzerKont");
            Store = new AccountManagerStore(this);
            ubisoft = new UbisoftConnectAdapter(GetPluginUserDataPath(), message => logger.Info(message));
            steam = new SteamAdapter(message => logger.Info(message));
            ea = new EAAppAdapter(message => logger.Info(message));
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@",
                Description = "Menadżer Kont",
                Action = _ => OpenManager(PlayniteApi.MainView.SelectedGames)
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games == null || args.Games.Count == 0)
                yield break;

            yield return new GameMenuItem
            {
                MenuSection = "Menadżer Kont",
                Description = "Przypisz konto",
                Action = _ => OpenManager(args.Games)
            };

            if (args.Games.Count == 1)
            {
                var assignment = Store.GetAssignment(args.Games[0].Id);
                if (assignment != null)
                {
                    var account = Store.GetAccount(assignment.AccountId);
                    yield return new GameMenuItem
                    {
                        MenuSection = "Menadżer Kont",
                        Description = account == null
                            ? "Usuń przypisanie konta"
                            : "Usuń przypisanie: " + account.Name,
                        Action = _ =>
                        {
                            Store.ClearAssignment(args.Games[0].Id);
                            PlayniteApi.Notifications.Add(new NotificationMessage(
                                "PlayniteAccountManager",
                                "Przypisanie konta usunięte.",
                                NotificationType.Info));
                        }
                    };
                }
            }
        }

        private void OpenManager(IEnumerable<Game> contextGames)
        {
            try
            {
                var view = new AccountManagerView(this);
                view.SetContextGames(contextGames);

                var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true
                });
                window.Title = "Menadżer Kont";
                window.Width = 1020;
                window.Height = 720;
                window.MinWidth = 920;
                window.MinHeight = 620;
                window.Content = view;
                window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Nie udało się otworzyć Menadżera Kont.");
                PlayniteApi.Dialogs.ShowErrorMessage("Nie udało się otworzyć Menadżera Kont.

" + ex.Message, "Menadżer Kont");
            }
        }

        internal bool TryTestLogin(Guid accountId, out string error)
        {
            var account = Store.GetAccount(accountId);
            if (account == null)
            {
                error = "Nie znaleziono konta.";
                return false;
            }

            string password = Store.Credentials.Get(accountId);
            switch (account.Launcher)
            {
                case LauncherType.UbisoftConnect:
                    return ubisoft.PrepareAndLogin(account, password, out error);
                case LauncherType.Steam:
                    return steam.PrepareAndLogin(account, password, out error);
                case LauncherType.EAApp:
                    return ea.PrepareAndLogin(account, password, out error);
                default:
                    error = "Automatyczne logowanie nie jest jeszcze zaimplementowane dla: " + account.Launcher.GetDisplayName() + ".";
                    return false;
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            var assignment = Store.GetAssignment(args.Game.Id);

            if (assignment != null && assignment.AutoLogin && assignment.AccountId != Guid.Empty)
            {
                var account = Store.GetAccount(assignment.AccountId);
                if (account == null)
                    return;

                if (account.Launcher != LauncherType.UbisoftConnect && account.Launcher != LauncherType.Steam && account.Launcher != LauncherType.EAApp)
                    return;

                logger.Info(account.Launcher.GetDisplayName() + ": przygotowuję logowanie przed uruchomieniem: " + args.Game.Name);

                string password = Store.Credentials.Get(account.Id);
                string error;
                bool ok;

                try
                {
                    if (account.Launcher == LauncherType.UbisoftConnect)
                        ok = ubisoft.PrepareAndLogin(account, password, out error);
                    else if (account.Launcher == LauncherType.EAApp)
                        ok = ea.PrepareAndLogin(account, password, out error);
                    else
                        ok = steam.PrepareForGame(account, out error);
                }
                catch (Exception ex)
                {
                    ok = false;
                    error = "Błąd automatycznego logowania " + account.Launcher.GetDisplayName() + ": " + ex.Message;
                    logger.Error(ex, "Wyjątek podczas automatycznego logowania przed startem gry.");
                }

                if (!ok)
                {
                    args.CancelStartup = true;
                    logger.Error(error);
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "PlayniteAccountManager",
                        error,
                        NotificationType.Error));
                    return;
                }

                activeAccountId = account.Id;
                activeGameId = args.Game.Id;
                activeLauncher = account.Launcher;
                activeLogoutAfterGame = assignment.LogoutAfterGame;

                logger.Info(account.Launcher.GetDisplayName() + ": logowanie zakończone. Playnite kontynuuje normalne uruchamianie gry.");
                return;
            }

            if (IsEAGame(args.Game))
            {
                var mainEa = Store.GetPrimaryEAAccount();
                if (mainEa == null) mainEa = Store.EnsurePrimaryEAAccount();
                if (mainEa == null)
                {
                    logger.Info("EA App: gra „" + args.Game.Name + "” nie ma przypisanego konta i nie ustawiono głównego konta EA App. Nie zmieniam profilu EA.");
                    return;
                }

                logger.Info("EA App: gra „" + args.Game.Name + "” nie ma przypisanego konta. Ustawiam główne konto EA App: „" + mainEa.UserName + "”.");
                string mainEaError;
                string mainEaPassword = Store.Credentials.Get(mainEa.Id);
                bool mainEaOk;
                try { mainEaOk = ea.PrepareAndLogin(mainEa, mainEaPassword, out mainEaError); }
                catch (Exception ex) { mainEaOk = false; mainEaError = "Błąd ustawiania głównego konta EA App: " + ex.Message; logger.Error(ex, "Wyjątek podczas ustawiania głównego konta EA App."); }
                if (!mainEaOk)
                {
                    args.CancelStartup = true;
                    logger.Error(mainEaError);
                    PlayniteApi.Notifications.Add(new NotificationMessage("PlayniteAccountManager", mainEaError, NotificationType.Error));
                }
                return;
            }

            if (IsSteamGame(args.Game))
            {
                var mainSteam = Store.GetPrimarySteamAccount();
                if (mainSteam == null)
                    mainSteam = Store.EnsurePrimarySteamAccount();

                if (mainSteam == null)
                {
                    logger.Info("Steam: gra „" + args.Game.Name + "” nie ma przypisanego konta i nie ustawiono głównego konta Steam. Nie zmieniam profilu Steam.");
                    return;
                }

                logger.Info("Steam: gra „" + args.Game.Name + "” nie ma przypisanego konta. Ustawiam główne konto Steam: „" + mainSteam.UserName + "”.");

                string mainError;
                bool mainOk;
                try
                {
                    mainOk = steam.PrepareForGame(mainSteam, out mainError, false);
                }
                catch (Exception ex)
                {
                    mainOk = false;
                    mainError = "Błąd ustawiania głównego konta Steam: " + ex.Message;
                    logger.Error(ex, "Wyjątek podczas ustawiania głównego konta Steam.");
                }

                if (!mainOk)
                {
                    args.CancelStartup = true;
                    logger.Error(mainError);
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "PlayniteAccountManager",
                        mainError,
                        NotificationType.Error));
                    return;
                }

                steam.ForgetActiveSessionWithoutRestore();
                logger.Info("Steam: główne konto jest aktywne. Playnite kontynuuje normalne uruchamianie gry.");
            }
        }

        private static bool IsEAGame(Game game)
        {
            return game != null && game.Source != null && !string.IsNullOrWhiteSpace(game.Source.Name) && game.Source.Name.IndexOf("EA", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSteamGame(Game game)
        {
            return game != null && game.Source != null &&
                   !string.IsNullOrWhiteSpace(game.Source.Name) &&
                   game.Source.Name.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (activeGameId == args.Game.Id)
                logger.Info(activeLauncher.GetDisplayName() + ": Playnite potwierdził uruchomienie gry: " + args.Game.Name + ". PID=" + args.StartedProcessId + ".");
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (activeGameId != args.Game.Id || !activeLogoutAfterGame)
                return;

            LogoutActiveLauncher();
            ClearActiveSession();
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            if (activeGameId != args.Game.Id)
                return;

            LogoutActiveLauncher();
            ClearActiveSession();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            if (activeGameId == Guid.Empty)
                return;

            LogoutActiveLauncher();
            ClearActiveSession();
        }

        private void LogoutActiveLauncher()
        {
            try
            {
                if (activeLauncher == LauncherType.UbisoftConnect)
                    ubisoft.Logout();
                else if (activeLauncher == LauncherType.Steam)
                    steam.Logout();
                else if (activeLauncher == LauncherType.EAApp)
                    LogoutAndRestorePrimaryEA();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Nie udało się zakończyć sesji launchera.");
            }
        }

        private void LogoutAndRestorePrimaryEA()
        {
            string logoutError;
            if (!ea.Logout(out logoutError) && !string.IsNullOrWhiteSpace(logoutError))
                logger.Error(logoutError);

            var primary = Store.GetPrimaryEAAccount();
            if (primary == null || primary.Id == activeAccountId)
                return;

            string password = Store.Credentials.Get(primary.Id);
            string loginError;
            try
            {
                if (string.IsNullOrEmpty(password))
                {
                    logger.Error("EA App: brak hasła głównego konta, nie przywracam profilu po zakończeniu gry.");
                    return;
                }

                if (!ea.PrepareAndLogin(primary, password, out loginError))
                    logger.Error("EA App: nie udało się przywrócić głównego konta: " + loginError);
                else
                    logger.Info("EA App: przywrócono główne konto „" + primary.UserName + "” po zakończeniu gry.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "EA App: wyjątek podczas przywracania głównego konta.");
            }
        }

        private void ClearActiveSession()
        {
            activeAccountId = Guid.Empty;
            activeGameId = Guid.Empty;
            activeLauncher = LauncherType.UbisoftConnect;
            activeLogoutAfterGame = false;
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return Store.Settings;
        }

        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunView)
        {
            return null;
        }
    }
}
