using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Services
{
    internal sealed class EpicGamesAdapter
    {
        private static readonly string[] ProcessNames =
        {
            "EpicGamesLauncher",
            "EpicWebHelper"
        };

        private static readonly TimeSpan SessionLoginTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan KeyboardLoginConfirmTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(6);

        private readonly Action<string> log;
        private readonly EpicGamesSessionStore sessionStore;
        private readonly EpicGamesUiAutomation uiAutomation;
        private readonly EpicSessionVault vault;
        private readonly EpicLauncherLog launcherLog = new EpicLauncherLog();

        public EpicGamesAdapter(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            sessionStore = new EpicGamesSessionStore(this.log);
            uiAutomation = new EpicGamesUiAutomation(this.log);
            vault = new EpicSessionVault(pluginUserDataPath);
        }

        public bool PrepareForManualLogin(out string error)
        {
            error = null;

            try
            {
                // Resolve the launcher before stopping Epic so a non-standard
                // installation can still be discovered from the live process.
                string exe = FindExecutable();
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = Loc.Get("ErrLauncherNotFound", "Epic Games Launcher");
                    return false;
                }

                StopProcesses();
                SaveActiveSession();

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                SetActiveAccount(Guid.Empty);
                Start(exe);

                log("Epic Games: active session cleared and launcher started for manual login.");
                return true;
            }
            catch (Exception ex)
            {
                error = Loc.Get("ErrPrepareManual", "Epic Games", ex.Message);
                return false;
            }
        }

        public bool PrepareAndLogin(AccountRecord account, string password, out string error)
        {
            error = null;

            if (account == null || account.Id == Guid.Empty)
            {
                error = Loc.Get("ErrNoAccountSelected", "Epic Games");
                return false;
            }

            if (account.Launcher != LauncherType.EpicGames)
            {
                error = Loc.Get("ErrWrongLauncher", "Epic Games");
                return false;
            }

            if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrEmpty(password))
            {
                error = Loc.Get("ErrNoCredentialsAccount", account.Name);
                return false;
            }

            try
            {
                string exe = FindExecutable();
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = Loc.Get("ErrLauncherNotFound", "Epic Games Launcher");
                    return false;
                }

                log("Epic Games: automatyczne logowanie dla konta „" + account.Name + "”.");
                log("Epic Games: zamykam poprzednią sesję i launcher.");
                StopProcesses();
                SaveActiveSession();

                // Preferred: restore the saved session of this account (no typing).
                EpicSessionSnapshot snapshot;
                if (vault.TryLoad(account.Id, out snapshot))
                {
                    string sessionError;
                    if (TryLoginWithSnapshot(exe, account, snapshot, out sessionError))
                        return true;

                    log("Epic Games: " + sessionError + " Przechodzę na automatyczne logowanie (TAB).");
                    StopProcesses();
                }
                else
                {
                    log("Epic Games: brak zapisanej sesji dla tego konta, używam automatycznego logowania (TAB).");
                }

                // Fallback: keyboard sequence. Twoja sekwencja AHK ma 2 sekundy
                // między zamknięciem launchera a jego ponownym uruchomieniem.
                Thread.Sleep(2000);

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                SetActiveAccount(Guid.Empty);
                DateTime startedUtc = DateTime.UtcNow;
                Start(exe);

                log("Epic Games: launcher uruchomiony — odtwarzam sekwencję automatycznego logowania z AHK.");
                if (!uiAutomation.Login(account.UserName, password, out error))
                    return false;

                // Bind the new session to this account so the next switch can
                // restore it without typing.
                string epicAccountId;
                string confirmError;
                if (launcherLog.WaitForLogin(startedUtc, KeyboardLoginConfirmTimeout, out epicAccountId, out confirmError))
                {
                    SetActiveAccount(account.Id);
                    CaptureAfterLogin(account, epicAccountId, null);
                }
                else
                {
                    log("Epic Games: logowanie nie zostało potwierdzone w logu launchera (" + confirmError + "). Sesja nie zostanie zapisana.");
                }

                return true;
            }
            catch (Exception ex)
            {
                error = Loc.Get("ErrAutoLogin", "Epic Games", ex.Message);
                return false;
            }
        }

        public bool ClearSession(out string error)
        {
            try
            {
                StopProcesses();
                SaveActiveSession();
                SetActiveAccount(Guid.Empty);
                return sessionStore.ClearLiveState(out error);
            }
            catch (Exception ex)
            {
                error = Loc.Get("ErrClearSession", "Epic Games", ex.Message);
                return false;
            }
        }

        private bool TryLoginWithSnapshot(string exe, AccountRecord account, EpicSessionSnapshot snapshot, out string error)
        {
            if (!sessionStore.ClearLiveState(out error))
                return false;

            if (!sessionStore.RestoreSession(snapshot, out error))
                return false;

            SetActiveAccount(Guid.Empty);
            DateTime startedUtc = DateTime.UtcNow;
            Start(exe);
            log("Epic Games: uruchomiono launcher z zapisaną sesją, czekam na potwierdzenie logowania.");

            string epicAccountId;
            if (!launcherLog.WaitForLogin(startedUtc, SessionLoginTimeout, out epicAccountId, out error))
                return false;

            if (!string.Equals(epicAccountId, snapshot.AccountId, StringComparison.OrdinalIgnoreCase))
            {
                // Never keep a snapshot that signs in a different Epic account.
                vault.Delete(account.Id);
                error = Loc.Get("ErrEpicWrongAccount");
                return false;
            }

            SetActiveAccount(account.Id);
            CaptureAfterLogin(account, epicAccountId, snapshot.RememberMeData);
            log("Epic Games: zalogowano z zapisanej sesji (bez wpisywania hasła).");
            return true;
        }

        /// <summary>
        /// After a sign-in the launcher writes a new (rotated) refresh token to its
        /// configuration. Save it right away, but only when the live session belongs
        /// to the Epic account that just signed in.
        /// </summary>
        private void CaptureAfterLogin(AccountRecord account, string epicAccountId, string previousData)
        {
            DateTime deadline = DateTime.UtcNow + CaptureTimeout;
            EpicSessionSnapshot live = null;

            while (DateTime.UtcNow < deadline)
            {
                live = sessionStore.CaptureLiveSession();
                if (live != null && live.IsComplete &&
                    (epicAccountId == null || string.Equals(live.AccountId, epicAccountId, StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(live.RememberMeData, previousData, StringComparison.Ordinal))
                    break;

                Thread.Sleep(500);
            }

            if (live == null || !live.IsComplete ||
                (epicAccountId != null && !string.Equals(live.AccountId, epicAccountId, StringComparison.OrdinalIgnoreCase)))
            {
                log("Epic Games: launcher nie zapisał sesji „Zapamiętaj mnie” – snapshot nie został utworzony.");
                return;
            }

            TrySave(account.Id, live, "Epic Games: zapisano sesję konta „" + account.Name + "” do szybkiego przełączania.");
        }

        /// <summary>
        /// Stores the latest session of the account that was live before the launcher
        /// was closed. Skipped when the live session belongs to another Epic account
        /// (e.g. somebody signed in manually outside the plugin).
        /// </summary>
        private void SaveActiveSession()
        {
            Guid active = vault.GetActiveAccount();
            if (active == Guid.Empty)
                return;

            EpicSessionSnapshot live = sessionStore.CaptureLiveSession();
            if (live == null || !live.IsComplete)
                return;

            string bound = vault.GetBoundAccountId(active);
            if (!string.Equals(bound, live.AccountId, StringComparison.OrdinalIgnoreCase))
            {
                log("Epic Games: aktywna sesja należy do innego konta Epic niż zapisane – nie aktualizuję snapshotu.");
                return;
            }

            TrySave(active, live, "Epic Games: zaktualizowano zapisaną sesję poprzedniego konta.");
        }

        private void TrySave(Guid accountId, EpicSessionSnapshot snapshot, string message)
        {
            try
            {
                vault.Save(accountId, snapshot);
                log(message);
            }
            catch (Exception ex)
            {
                log("Epic Games: nie udało się zapisać snapshotu sesji: " + ex.Message);
            }
        }

        private void SetActiveAccount(Guid accountId)
        {
            try { vault.SetActiveAccount(accountId); }
            catch (Exception ex) { log("Epic Games: nie udało się zapisać znacznika aktywnego konta: " + ex.Message); }
        }

        private static void Start(string exe)
        {
            using (Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            }))
            {
            }
        }

        private static void StopProcesses()
        {
            // Epic uses Chromium/EOS helper processes to keep launcher state open.
            // They must be stopped as well, otherwise session files can remain locked
            // or the previous web session can be restored immediately.
            foreach (string processName in ProcessNames)
            foreach (Process p in SafeGetProcesses(processName))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        try { p.CloseMainWindow(); } catch { }

                        if (!p.WaitForExit(1200))
                        {
                            try { p.Kill(); } catch { }
                            try { p.WaitForExit(1200); } catch { }
                        }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        private static string FindExecutable()
        {
            var candidates = new List<string>();
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");

            Add(candidates, pf);
            Add(candidates, pf86);

            foreach (Process p in SafeGetProcesses("EpicGamesLauncher"))
            {
                try
                {
                    if (p.HasExited)
                        continue;

                    try
                    {
                        string path = ProcessPaths.Get(p);
                        if (!string.IsNullOrWhiteSpace(path))
                            candidates.Add(path);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }

            foreach (string candidate in candidates
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                }
            }

            return null;
        }

        private static void Add(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            candidates.Add(Path.Combine(
                root, "Epic Games", "Launcher", "Portal",
                "Binaries", "Win32", "EpicGamesLauncher.exe"));

            candidates.Add(Path.Combine(
                root, "Epic Games", "Launcher", "Portal",
                "Binaries", "Win64", "EpicGamesLauncher.exe"));
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] processes;

            try { processes = Process.GetProcessesByName(name); }
            catch { yield break; }

            foreach (Process process in processes)
                yield return process;
        }
    }
}
