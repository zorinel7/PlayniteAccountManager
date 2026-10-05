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
        private EpicGamesAdapter epic;
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
            ea = new EAAppAdapter(GetPluginUserDataPath(), message => logger.Info(message));
            epic = new EpicGamesAdapter(message => logger.Info(message));
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
                PlayniteApi.Dialogs.ShowErrorMessage("Nie udało się otworzyć Menadżera Kont.\n\n" + ex.Message, "Menadżer Kont");
            }
        }

        internal bool ShowManualLoginForTest(Guid accountId, out string error)
        {
            error = null;

            var account = Store.GetAccount(accountId);
            if (account == null)
            {
                error = "Nie znaleziono konta.";
                return false;
            }

            if (account.Launcher != LauncherType.EAApp &&
                account.Launcher != LauncherType.EpicGames)
            {
                error = "To konto nie korzysta z ręcznego logowania.";
                return false;
            }

            string username = account.UserName ?? string.Empty;
            string password = Store.Credentials.Get(account.Id) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Konto „" + account.Name +
                        "” nie ma zapisanych danych logowania.";
                return false;
            }

            return ShowManualLoginWindow(
                account.Launcher.GetDisplayName(),
                "ręczny test logowania",
                username,
                password);
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
                case LauncherType.EpicGames:
                    if (account.EpicLoginMode == EpicLoginMode.Automatic)
                        return epic.PrepareAndLogin(account, password, out error);

                    error = "To konto Epic Games jest ustawione na logowanie ręczne.";
                    return false;
                case LauncherType.EAApp:
                    error = "To konto EA App używa ręcznego logowania.";
                    return false;
                default:
                    error = "Automatyczne logowanie nie jest jeszcze zaimplementowane dla: " + account.Launcher.GetDisplayName() + ".";
                    return false;
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            var assignment = Store.GetAssignment(args.Game.Id);

            if (assignment != null && assignment.AccountId != Guid.Empty)
            {
                var account = Store.GetAccount(assignment.AccountId);
                if (account == null)
                    return;

                bool manualLauncher =
                    account.Launcher == LauncherType.EAApp ||
                    (account.Launcher == LauncherType.EpicGames &&
                     account.EpicLoginMode == EpicLoginMode.Manual);

                bool runAssignedFlow = assignment.AutoLogin || manualLauncher;

                if (runAssignedFlow)
                {
                    if (account.Launcher != LauncherType.UbisoftConnect &&
                        account.Launcher != LauncherType.Steam &&
                        !manualLauncher)
                        return;

                    logger.Info(account.Launcher.GetDisplayName() +
                                ": preparing login before starting: " +
                                args.Game.Name);

                    string password = Store.Credentials.Get(account.Id);
                    string error;
                    bool ok;

                    try
                    {
                        if (account.Launcher == LauncherType.UbisoftConnect)
                            ok = ubisoft.PrepareAndLogin(account, password, out error);
                        else if (account.Launcher == LauncherType.EpicGames &&
                                 account.EpicLoginMode == EpicLoginMode.Automatic)
                            ok = epic.PrepareAndLogin(account, password, out error);
                        else if (manualLauncher)
                            ok = PrepareManualLauncherLogin(account, args.Game.Name, out error);
                        else
                            ok = steam.PrepareForGame(account, out error);
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = "Launcher login preparation failed for " +
                                account.Launcher.GetDisplayName() + ": " + ex.Message;
                        logger.Error(ex, "Exception while preparing launcher login.");
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

                    logger.Info(account.Launcher.GetDisplayName() +
                                ": login preparation completed. Playnite continues normal game startup.");
                    return;
                }
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

        private bool PrepareManualLauncherLogin(
            AccountRecord account,
            string gameName,
            out string error)
        {
            error = null;

            if (account == null)
            {
                error = "Nie znaleziono konta.";
                return false;
            }

            string username = account.UserName ?? string.Empty;
            string password = Store.Credentials.Get(account.Id) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Konto „" + account.Name +
                        "” nie ma zapisanych danych logowania.";
                return false;
            }

            try
            {
                if (account.Launcher == LauncherType.EAApp)
                {
                    if (!ea.PrepareForManualLogin(out error))
                        return false;
                }
                else if (account.Launcher == LauncherType.EpicGames)
                {
                    if (!epic.PrepareForManualLogin(out error))
                        return false;
                }
                else
                {
                    error = "Nieobsługiwany launcher.";
                    return false;
                }

                return ShowManualLoginWindow(
                    account.Launcher.GetDisplayName(),
                    gameName,
                    username,
                    password);
            }
            catch (Exception ex)
            {
                error = "Nie udało się przygotować ręcznego logowania: " + ex.Message;
                logger.Error(ex, "Manual launcher login preparation failed.");
                return false;
            }
        }

        private bool ShowManualLoginWindow(
            string launcherName,
            string gameName,
            string username,
            string password)
        {
            bool accepted = false;
            System.Windows.Window window = null;

            Action copyLogin = () =>
            {
                try
                {
                    Clipboard.SetText(username ?? string.Empty);
                    logger.Info(launcherName + ": login copied to clipboard.");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Failed to copy launcher login.");
                }
            };

            Action copyPassword = () =>
            {
                try
                {
                    Clipboard.SetText(password ?? string.Empty);
                    logger.Info(launcherName + ": password copied to clipboard.");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Failed to copy launcher password.");
                }
            };

            Action<bool> close = result =>
            {
                accepted = result;
                if (window != null)
                    window.Close();
            };

            try
            {
                var view = new ManualLauncherLoginView(
                    launcherName,
                    gameName,
                    username,
                    password,
                    copyLogin,
                    copyPassword,
                    close);

                window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

                window.Title = "Logowanie — " + launcherName;
                window.Width = 580;
                window.Height = 500;
                window.MinWidth = 520;
                window.MinHeight = 430;
                window.Content = view;
                window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.ShowDialog();

                return accepted;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to show manual launcher login window.");
                return false;
            }
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
                {
                    string error;
                    if (!ea.ClearSession(out error) && !string.IsNullOrWhiteSpace(error))
                        logger.Error(error);
                }
                else if (activeLauncher == LauncherType.EpicGames)
                {
                    string error;
                    if (!epic.ClearSession(out error) && !string.IsNullOrWhiteSpace(error))
                        logger.Error(error);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Nie udało się zakończyć sesji launchera.");
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
