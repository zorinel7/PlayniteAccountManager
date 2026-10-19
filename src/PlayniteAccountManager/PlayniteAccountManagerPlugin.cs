using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
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
        private GOGGalaxyAdapter gog;
        private BattleNetAdapter battleNet;
        private RockstarGamesLauncherAdapter rockstar;
        private XboxAppAdapter xbox;
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
            epic = new EpicGamesAdapter(GetPluginUserDataPath(), message => logger.Info(message));
            gog = new GOGGalaxyAdapter(GetPluginUserDataPath(), message => logger.Info(message));
            battleNet = new BattleNetAdapter(GetPluginUserDataPath(), message => logger.Info(message));
            rockstar = new RockstarGamesLauncherAdapter(GetPluginUserDataPath(), message => logger.Info(message));
            xbox = new XboxAppAdapter(GetPluginUserDataPath(), message => logger.Info(message));
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@",
                Description = ResourceProvider.GetString("LOCPlayniteAccountManagerMenuTitle"),
                Action = _ => OpenManager(PlayniteApi.MainView.SelectedGames)
            };

        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games == null || args.Games.Count == 0)
                yield break;

            yield return new GameMenuItem
            {
                MenuSection = ResourceProvider.GetString("LOCPlayniteAccountManagerMenuTitle"),
                Description = ResourceProvider.GetString("LOCPlayniteAccountManagerMenuAssign"),
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
                        MenuSection = ResourceProvider.GetString("LOCPlayniteAccountManagerMenuTitle"),
                        Description = account == null
                            ? ResourceProvider.GetString("LOCPlayniteAccountManagerMenuUnassign")
                            : string.Format(ResourceProvider.GetString("LOCPlayniteAccountManagerMenuUnassignNamed"), account.Name),
                        Action = _ =>
                        {
                            Store.ClearAssignment(args.Games[0].Id);
                            PlayniteApi.Notifications.Add(new NotificationMessage(
                                "PlayniteAccountManager",
                                Loc.Get("AssignmentRemoved"),
                                NotificationType.Info));
                        }
                    };
                }
            }
        }

        /// <summary>
        /// Sizes a window to a share of the screen work area, so it fits on
        /// small and scaled displays and does not become huge on 4K ones.
        /// </summary>
        private static void FitToWorkArea(Window window, double maxWidth, double maxHeight,
            double widthShare, double heightShare, double minWidth, double minHeight)
        {
            Rect area = SystemParameters.WorkArea;
            double availableWidth = Math.Max(400, area.Width - 40);
            double availableHeight = Math.Max(300, area.Height - 40);

            window.MinWidth = Math.Min(minWidth, availableWidth);
            window.MinHeight = Math.Min(minHeight, availableHeight);
            window.Width = Math.Max(window.MinWidth, Math.Min(maxWidth, Math.Min(availableWidth, area.Width * widthShare)));
            window.Height = Math.Max(window.MinHeight, Math.Min(maxHeight, Math.Min(availableHeight, area.Height * heightShare)));
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
                window.Title = ResourceProvider.GetString("LOCPlayniteAccountManagerTitle");
                FitToWorkArea(window, 1280, 980, 0.85, 0.9, 860, 560);
                window.Content = view;
                window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Nie udało się otworzyć Menadżera Kont.");
                PlayniteApi.Dialogs.ShowErrorMessage(Loc.Get("ErrOpenManager", ex.Message), ResourceProvider.GetString("LOCPlayniteAccountManagerTitle"));
            }
        }

        internal bool ShowManualLoginForTest(Guid accountId, out string error)
        {
            error = null;

            var account = Store.GetAccount(accountId);
            if (account == null)
            {
                error = Loc.Get("ErrAccountNotFound");
                return false;
            }

            if (account.Launcher != LauncherType.EAApp &&
                account.Launcher != LauncherType.EpicGames &&
                account.Launcher != LauncherType.UbisoftConnect &&
                account.Launcher != LauncherType.GOGGalaxy &&
                account.Launcher != LauncherType.BattleNet &&
                account.Launcher != LauncherType.RockstarGamesLauncher)
            {
                error = Loc.Get("ErrNotManual");
                return false;
            }

            string username = account.UserName ?? string.Empty;
            string password = Store.Credentials.Get(account.Id) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = Loc.Get("ErrNoCredentialsAccount", account.Name);
                return false;
            }

            if (account.Launcher == LauncherType.GOGGalaxy)
            {
                string prepareError;
                if (!gog.PrepareForManualLogin(out prepareError))
                {
                    error = prepareError;
                    return false;
                }
            }
            else if (account.Launcher == LauncherType.BattleNet)
            {
                string prepareError;
                if (!battleNet.PrepareForManualLogin(out prepareError))
                {
                    error = prepareError;
                    return false;
                }
            }
            else if (account.Launcher == LauncherType.RockstarGamesLauncher)
            {
                string prepareError;
                if (!rockstar.PrepareForManualLogin(out prepareError))
                {
                    error = prepareError;
                    return false;
                }

                if (TryRockstarAutomaticLogin(username, password))
                    return true;
            }

            return ShowManualLoginWindow(
                account.Launcher.GetDisplayName(),
                Loc.Get("ManualTestGame"),
                username,
                password);
        }

        /// <summary>
        /// Rockstar Games Launcher: after the session was prepared, fills the sign-in
        /// window through its accessibility tree. Returns false (and notifies) when the
        /// caller should fall back to the manual login window.
        /// </summary>
        private bool TryRockstarAutomaticLogin(string username, string password)
        {
            string error;
            MsaaLoginResult result = new RockstarMsaaLogin(message => logger.Info(message))
                .Login(username, password, out error);

            if (result == MsaaLoginResult.Success)
                return true;

            logger.Warn("Rockstar Games Launcher: automatyczne logowanie nie powiodło się: " + error +
                        " Pokazuję okno ręcznego logowania.");
            PlayniteApi.Notifications.Add(new NotificationMessage(
                "PlayniteAccountManager-Rockstar",
                error,
                NotificationType.Info));
            return false;
        }

        internal bool TryTestLogin(Guid accountId, out string error)
        {
            var account = Store.GetAccount(accountId);
            if (account == null)
            {
                error = Loc.Get("ErrAccountNotFound");
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

                    error = Loc.Get("ErrManualMode", "Epic Games");
                    return false;
                case LauncherType.EAApp:
                    if (account.EALoginMode == (EALoginMode)1)
                        return ea.PrepareAndLogin(account, password, out error);

                    error = Loc.Get("ErrManualMode", "EA App");
                    return false;
                case LauncherType.XboxApp:
                    return xbox.PrepareAndLogin(account, out error);
                case LauncherType.BattleNet:
                    if (account.BattleNetLoginMode == BattleNetLoginMode.Automatic)
                        return TryBattleNetAutomaticLogin(account, password, out error);

                    error = Loc.Get("ErrManualMode", "Battle.net");
                    return false;
                case LauncherType.GOGGalaxy:
                    if (account.GOGLoginMode == GOGLoginMode.Automatic)
                        return TryGogAutomaticLogin(account, password, out error);

                    error = Loc.Get("ErrManualMode", "GOG Galaxy");
                    return false;
                default:
                    error = Loc.Get("ErrAutoNotImplemented", account.Launcher.GetDisplayName());
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
                    (account.Launcher == LauncherType.GOGGalaxy &&
                     account.GOGLoginMode == GOGLoginMode.Manual) ||
                    (account.Launcher == LauncherType.BattleNet &&
                     account.BattleNetLoginMode == BattleNetLoginMode.Manual) ||
                    account.Launcher == LauncherType.RockstarGamesLauncher ||
                    (account.Launcher == LauncherType.UbisoftConnect &&
                     account.UbisoftLoginMode == UbisoftLoginMode.Manual) ||
                    (account.Launcher == LauncherType.EAApp &&
                     account.EALoginMode == EALoginMode.Manual) ||
                    (account.Launcher == LauncherType.EpicGames &&
                     account.EpicLoginMode == EpicLoginMode.Manual);

                bool automaticEpic =
                    account.Launcher == LauncherType.EpicGames &&
                    account.EpicLoginMode == EpicLoginMode.Automatic;

                bool automaticEA =
                    account.Launcher == LauncherType.EAApp &&
                    account.EALoginMode == EALoginMode.Automatic;

                bool automaticGOG =
                    account.Launcher == LauncherType.GOGGalaxy &&
                    account.GOGLoginMode == GOGLoginMode.Automatic;

                bool automaticBattleNet =
                    account.Launcher == LauncherType.BattleNet &&
                    account.BattleNetLoginMode == BattleNetLoginMode.Automatic;

                bool xboxApp = account.Launcher == LauncherType.XboxApp;

                bool runAssignedFlow =
                    assignment.AutoLogin || manualLauncher || automaticEpic || automaticEA || automaticGOG || automaticBattleNet || xboxApp;

                if (runAssignedFlow)
                {
                    if (account.Launcher != LauncherType.UbisoftConnect &&
                        account.Launcher != LauncherType.Steam &&
                        !manualLauncher &&
                        !automaticEpic &&
                        !automaticEA &&
                        !automaticGOG &&
                        !automaticBattleNet &&
                        !xboxApp)
                        return;

                    logger.Info(account.Launcher.GetDisplayName() +
                                ": preparing login before starting: " +
                                args.Game.Name);

                    if (account.Launcher == LauncherType.EpicGames)
                        logger.Info("Epic Games: zapisany tryb logowania = " +
                                     account.EpicLoginMode +
                                     ", AutoLogin przypisania = " +
                                     assignment.AutoLogin + ".");
                    else if (account.Launcher == LauncherType.EAApp)
                        logger.Info("EA App: zapisany tryb logowania = " +
                                     account.EALoginMode +
                                     ", AutoLogin przypisania = " +
                                     assignment.AutoLogin + ".");
                    else if (account.Launcher == LauncherType.UbisoftConnect)
                        logger.Info("Ubisoft Connect: zapisany tryb logowania = " +
                                     account.UbisoftLoginMode +
                                     ", AutoLogin przypisania = " +
                                     assignment.AutoLogin + ".");
                    else if (account.Launcher == LauncherType.GOGGalaxy)
                        logger.Info("GOG Galaxy: zapisany tryb logowania = " +
                                     account.GOGLoginMode +
                                     ", AutoLogin przypisania = " +
                                     assignment.AutoLogin + ".");
                    else if (account.Launcher == LauncherType.BattleNet)
                        logger.Info("Battle.net: zapisany tryb logowania = " +
                                     account.BattleNetLoginMode +
                                     ", AutoLogin przypisania = " +
                                     assignment.AutoLogin + ".");
                    else if (account.Launcher == LauncherType.RockstarGamesLauncher)
                        logger.Info("Rockstar Games Launcher: ręczne logowanie, AutoLogin przypisania = " +
                                     assignment.AutoLogin + ".");

                    string password = Store.Credentials.Get(account.Id);
                    string error;
                    bool ok;

                    try
                    {
                        if (account.Launcher == LauncherType.UbisoftConnect)
                            ok = account.UbisoftLoginMode == UbisoftLoginMode.Automatic
                                ? ubisoft.PrepareAndLogin(account, password, out error)
                                : PrepareManualLauncherLogin(account, args.Game.Name, out error);
                        else if (account.Launcher == LauncherType.EpicGames &&
                                 account.EpicLoginMode == EpicLoginMode.Automatic)
                            ok = epic.PrepareAndLogin(account, password, out error);
                        else if (account.Launcher == LauncherType.EAApp &&
                                 account.EALoginMode == (EALoginMode)1)
                            ok = ea.PrepareAndLogin(account, password, out error);
                        else if (automaticBattleNet)
                            ok = PrepareBattleNetAutomaticLogin(account, password, args.Game.Name, out error);
                        else if (automaticGOG)
                            ok = PrepareGogAutomaticLogin(account, password, args.Game.Name, out error);
                        else if (xboxApp)
                            ok = PrepareXboxLogin(account, password, args.Game.Name, out error);
                        else if (manualLauncher)
                            ok = PrepareManualLauncherLogin(account, args.Game.Name, out error);
                        else
                            ok = steam.PrepareForGame(account, out error);
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = Loc.Get("ErrPrepareLauncher", account.Launcher.GetDisplayName(), ex.Message);
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

                logger.Info("Steam: gra „" + args.Game.Name + "” nie ma przypisanego konta. Ustawiam główne konto Steam: „" + LogPrivacy.MaskAccount(mainSteam.UserName) + "”.");

                string mainError;
                bool mainOk;
                try
                {
                    mainOk = steam.PrepareForGame(mainSteam, out mainError, false);
                }
                catch (Exception ex)
                {
                    mainOk = false;
                    mainError = Loc.Get("ErrSteamPrimary", ex.Message);
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
                error = Loc.Get("ErrAccountNotFound");
                return false;
            }

            string username = account.UserName ?? string.Empty;
            string password = Store.Credentials.Get(account.Id) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = Loc.Get("ErrNoCredentialsAccount", account.Name);
                return false;
            }

            try
            {
                if (account.Launcher == LauncherType.EAApp)
                {
                    if (!ea.PrepareForManualLogin(out error))
                        return false;
                }
                else if (account.Launcher == LauncherType.UbisoftConnect)
                {
                    if (!ubisoft.PrepareForManualLogin(out error))
                        return false;
                }
                else if (account.Launcher == LauncherType.EpicGames)
                {
                    if (!epic.PrepareForManualLogin(out error))
                        return false;
                }
                else if (account.Launcher == LauncherType.GOGGalaxy)
                {
                    if (!gog.PrepareForManualLogin(out error))
                        return false;
                }
                else if (account.Launcher == LauncherType.BattleNet)
                {
                    if (!battleNet.PrepareForManualLogin(out error))
                        return false;
                }
                else if (account.Launcher == LauncherType.RockstarGamesLauncher)
                {
                    if (!rockstar.PrepareForManualLogin(out error))
                        return false;

                    if (TryRockstarAutomaticLogin(username, password))
                        return true;
                }
                else
                {
                    error = Loc.Get("ErrUnsupportedLauncher");
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
                error = Loc.Get("ErrPrepareManualLogin", ex.Message);
                logger.Error(ex, "Manual launcher login preparation failed.");
                return false;
            }
        }

        /// <summary>
        /// GOG Galaxy automatic login: the session is cleared and Galaxy restarted as
        /// for manual login, then the sign-in window is filled automatically. When that
        /// fails the shared manual login window is shown, like the other launchers.
        /// </summary>
        private bool PrepareGogAutomaticLogin(AccountRecord account, string password, string gameName, out string error)
        {
            if (TryGogAutomaticLogin(account, password, out error))
                return true;

            if (!string.IsNullOrEmpty(error) && error.StartsWith(GogPrepareErrorPrefix, StringComparison.Ordinal))
                return false;

            logger.Warn("GOG Galaxy: automatyczne logowanie nie powiodło się: " + error + " Pokazuję okno ręcznego logowania.");
            PlayniteApi.Notifications.Add(new NotificationMessage(
                "PlayniteAccountManager-GOG",
                error,
                NotificationType.Info));

            bool accepted = ShowManualLoginWindow(
                account.Launcher.GetDisplayName(),
                gameName,
                account.UserName ?? string.Empty,
                password ?? string.Empty);

            if (accepted)
            {
                error = null;
                return true;
            }

            error = Loc.Get("ErrLoginCancelled", "GOG Galaxy");
            return false;
        }

        private const string GogPrepareErrorPrefix = "GOG Galaxy (przygotowanie): ";
        private const string BattleNetPrepareErrorPrefix = "Battle.net (przygotowanie): ";

        /// <summary>
        /// Battle.net automatic login: the session is cleared and Battle.net restarted as
        /// for manual login, then the sign-in window is filled through its accessibility
        /// tree. When that fails the shared manual login window is shown.
        /// </summary>
        private bool PrepareBattleNetAutomaticLogin(AccountRecord account, string password, string gameName, out string error)
        {
            if (TryBattleNetAutomaticLogin(account, password, out error))
                return true;

            if (!string.IsNullOrEmpty(error) && error.StartsWith(BattleNetPrepareErrorPrefix, StringComparison.Ordinal))
                return false;

            logger.Warn("Battle.net: automatyczne logowanie nie powiodło się: " + error + " Pokazuję okno ręcznego logowania.");
            PlayniteApi.Notifications.Add(new NotificationMessage(
                "PlayniteAccountManager-BattleNet",
                error,
                NotificationType.Info));

            bool accepted = ShowManualLoginWindow(
                account.Launcher.GetDisplayName(),
                gameName,
                account.UserName ?? string.Empty,
                password ?? string.Empty);

            if (accepted)
            {
                error = null;
                return true;
            }

            error = Loc.Get("ErrLoginCancelled", "Battle.net");
            return false;
        }

        private bool TryBattleNetAutomaticLogin(AccountRecord account, string password, out string error)
        {
            if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrEmpty(password))
            {
                error = Loc.Get("ErrNoCredentialsAccount", account.Name);
                return false;
            }

            string prepareError;
            if (!battleNet.PrepareForManualLogin(out prepareError))
            {
                error = BattleNetPrepareErrorPrefix + prepareError;
                return false;
            }

            MsaaLoginResult result = new BattleNetMsaaLogin(message => logger.Info(message))
                .Login(account.UserName, password, out error);

            return result == MsaaLoginResult.Success;
        }

        private bool TryGogAutomaticLogin(AccountRecord account, string password, out string error)
        {
            if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrEmpty(password))
            {
                error = Loc.Get("ErrNoCredentialsAccount", account.Name);
                return false;
            }

            DateTime startedLocal = DateTime.Now;
            string prepareError;
            if (!gog.PrepareForManualLogin(out prepareError))
            {
                error = GogPrepareErrorPrefix + prepareError;
                return false;
            }

            MsaaLoginResult result = new GOGGalaxyUiAutomation(message => logger.Info(message))
                .Login(account.UserName, password, startedLocal, out error);

            return result == MsaaLoginResult.Success;
        }

        /// <summary>
        /// Xbox app: automatic switch through the Windows account picker; when it
        /// fails (e.g. the account is not added to Windows) the shared manual login
        /// window is shown so the user can sign in in the Xbox app.
        /// </summary>
        private bool PrepareXboxLogin(AccountRecord account, string password, string gameName, out string error)
        {
            if (xbox.PrepareAndLogin(account, out error))
                return true;

            logger.Warn("Xbox: automatyczne przełączenie konta nie powiodło się: " + error + " Pokazuję okno ręcznego logowania.");
            PlayniteApi.Notifications.Add(new NotificationMessage(
                "PlayniteAccountManager-Xbox",
                error,
                NotificationType.Info));

            string openError;
            if (!xbox.EnsureAppOpen(out openError))
                logger.Warn(openError);

            bool accepted = ShowManualLoginWindow(
                account.Launcher.GetDisplayName(),
                gameName,
                account.UserName ?? string.Empty,
                password ?? string.Empty);

            if (accepted)
            {
                error = null;
                return true;
            }

            error = Loc.Get("ErrLoginCancelled", "Xbox");
            return false;
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
                    SecureClipboard.SetSensitiveText(username ?? string.Empty);
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
                    SecureClipboard.SetSensitiveText(password ?? string.Empty);
                    logger.Info(launcherName + ": password copied to clipboard (excluded from clipboard history, auto-clear in " +
                                (int)SecureClipboard.DefaultClearDelay.TotalSeconds + " s).");
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

                window.Title = string.Format(ResourceProvider.GetString("LOCPlayniteAccountManagerLoginTitle"), launcherName);
                FitToWorkArea(window, 760, 640, 0.6, 0.8, 560, 460);
                window.Content = view;
                window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                // OnGameStarting may execute outside the WPF UI thread.
                // Always create/show the modal login window on the Playnite UI
                // dispatcher, then block until the user finishes the dialog.
                Dispatcher dispatcher = Application.Current == null
                    ? null
                    : Application.Current.Dispatcher;

                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    logger.Info(launcherName + ": wyświetlam okno ręcznego logowania przez WPF Dispatcher.");
                    dispatcher.Invoke(new Action(() =>
                    {
                        window.ShowDialog();
                        SecureClipboard.ClearIfOwned();
                    }));
                }
                else
                {
                    logger.Info(launcherName + ": wyświetlam okno ręcznego logowania.");
                    window.ShowDialog();
                    SecureClipboard.ClearIfOwned();
                }

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
                else if (activeLauncher == LauncherType.GOGGalaxy)
                {
                    string error;
                    if (!gog.ClearSession(out error) && !string.IsNullOrWhiteSpace(error))
                        logger.Error(error);
                }
                else if (activeLauncher == LauncherType.BattleNet)
                {
                    string error;
                    if (!battleNet.ClearSession(out error) && !string.IsNullOrWhiteSpace(error))
                        logger.Error(error);
                }
                else if (activeLauncher == LauncherType.RockstarGamesLauncher)
                {
                    string error;
                    if (!rockstar.ClearSession(out error) && !string.IsNullOrWhiteSpace(error))
                        logger.Error(error);
                }
                else if (activeLauncher == LauncherType.XboxApp)
                {
                    // The Xbox app account stays signed in; the next game switches it
                    // only when a different account is assigned.
                    logger.Info("Xbox: konto aplikacji Xbox pozostaje zalogowane po grze.");
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
