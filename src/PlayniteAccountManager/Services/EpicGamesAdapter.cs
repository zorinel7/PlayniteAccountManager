using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Epic Games Launcher account switching through its local login state.
    /// No launcher logout button is used.
    /// </summary>
    internal sealed class EpicGamesAdapter
    {
        private static readonly string[] ProcessNames =
        {
            "EpicGamesLauncher"
        };

        private readonly Action<string> log;
        private readonly EpicGamesUiAutomation uiAutomation;
        private readonly EpicGamesSessionStore sessionStore;

        public EpicGamesAdapter(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            uiAutomation = new EpicGamesUiAutomation(this.log);
            sessionStore = new EpicGamesSessionStore(pluginUserDataPath, this.log);
        }

        public bool PrepareAndLogin(AccountRecord account, string password, out string error)
        {
            error = null;

            if (account == null || account.Id == Guid.Empty)
            {
                error = "Nie wybrano konta Epic Games.";
                return false;
            }

            if (account.Launcher != LauncherType.EpicGames)
            {
                error = "Wybrane konto nie jest kontem Epic Games.";
                return false;
            }

            string exe = FindLauncherExecutable();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                error = "Nie znaleziono Epic Games Launcher na tym komputerze.";
                return false;
            }

            bool hasSnapshot = sessionStore.HasSnapshot(account.Id);
            string currentId = sessionStore.GetCurrentAccountId();
            string expectedId = hasSnapshot
                ? sessionStore.GetSnapshotAccountId(account.Id)
                : string.Empty;

            try
            {
                // If Epic is already on the requested account and its process is
                // running, leave it alone. This is the fastest path.
                if (hasSnapshot &&
                    !string.IsNullOrWhiteSpace(currentId) &&
                    string.Equals(currentId, expectedId, StringComparison.OrdinalIgnoreCase) &&
                    IsLauncherRunning())
                {
                    log("Epic Games: właściwe konto jest już aktywne. Pomijam przełączanie.");
                    return true;
                }

                StopLauncherProcesses();

                if (hasSnapshot)
                {
                    log("Epic Games: przywracam zapisany stan konta.");
                    if (!sessionStore.Restore(account.Id, out error))
                        return false;

                    if (!StartLauncher(exe, out error))
                        return false;

                    if (WaitForExpectedAccount(expectedId, 15, out error))
                        return true;

                    // A stale session can happen after an Epic update. Clear only
                    // the documented web cache before rebuilding the session.
                    log("Epic Games: zapisany stan nie został potwierdzony. Czyszczę web cache i ponawiam start.");
                    StopLauncherProcesses();
                    sessionStore.ClearCache();
                    sessionStore.Restore(account.Id, out error);

                    if (!StartLauncher(exe, out error))
                        return false;

                    if (WaitForExpectedAccount(expectedId, 15, out error))
                        return true;

                    error = "Epic Games nie potwierdził zapisanego konta.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrEmpty(password))
                {
                    error = "Brak loginu lub hasła zapisanego dla konta \"" + account.Name + "\".";
                    return false;
                }

                // First login for this account: clear the small login-state files,
                // but preserve Epic's installation and game data.
                sessionStore.ClearLiveState(false);

                if (!StartLauncher(exe, out error))
                    return false;

                if (!uiAutomation.Login(account.UserName, password, 30, out error))
                    return false;

                if (!WaitForAccountIdAfterLogin(account.UserName, 20, out error))
                    return false;

                string saveError;
                if (!sessionStore.SaveCurrent(account.Id, out saveError))
                    log("Epic Games: ostrzeżenie — nie udało się zapisać snapshotu: " + saveError);

                log("Epic Games: automatyczne logowanie zakończone pomyślnie dla konta \"" + account.Name + "\".");
                return true;
            }
            catch (Exception ex)
            {
                error = "Błąd automatycznego logowania Epic Games: " + ex.Message;
                log(error);
                return false;
            }
        }

        public void Logout()
        {
            try
            {
                StopLauncherProcesses();
            }
            finally
            {
                sessionStore.ClearLiveState(false);
            }
        }

        private bool WaitForExpectedAccount(string expectedId, int seconds, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(expectedId))
            {
                error = "Brak oczekiwanego AccountId Epic Games.";
                return false;
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));

            while (DateTime.UtcNow < deadline)
            {
                string current = sessionStore.GetCurrentAccountId();
                if (string.Equals(current, expectedId, StringComparison.OrdinalIgnoreCase))
                {
                    // Give Epic's UI a short moment to finish rendering. The game
                    // itself can be launched once the AccountId is confirmed.
                    Thread.Sleep(250);
                    log("Epic Games: potwierdzono właściwy AccountId.");
                    return true;
                }

                Thread.Sleep(120);
            }

            error = "Epic Games nie potwierdził właściwego AccountId.";
            return false;
        }

        private bool WaitForAccountIdAfterLogin(string username, int seconds, out string error)
        {
            error = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));

            while (DateTime.UtcNow < deadline)
            {
                string id = sessionStore.GetCurrentAccountId();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    log("Epic Games: wykryto AccountId po logowaniu: " + id + ".");
                    return true;
                }

                Thread.Sleep(150);
            }

            error = "Epic Games nie udostępnił AccountId po logowaniu \"" + username + "\".";
            return false;
        }

        private bool IsLauncherRunning()
        {
            foreach (Process p in SafeGetProcesses("EpicGamesLauncher"))
            {
                try
                {
                    if (!p.HasExited)
                        return true;
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            return false;
        }

        private bool StartLauncher(string exe, out string error)
        {
            error = null;

            try
            {
                using (Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                }))
                {
                    if (process == null)
                    {
                        error = "Windows nie uruchomił Epic Games Launcher.";
                        return false;
                    }
                }

                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    if (IsLauncherRunning())
                    {
                        log("Epic Games: launcher został uruchomiony.");
                        return true;
                    }

                    Thread.Sleep(120);
                }

                error = "Epic Games Launcher nie wystartował w ciągu 15 sekund.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się uruchomić Epic Games Launcher: " + ex.Message;
                return false;
            }
        }

        private string FindLauncherExecutable()
        {
            var candidates = new List<string>();

            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");

            AddCandidates(candidates, pf86);
            AddCandidates(candidates, pf);

            foreach (Process p in SafeGetProcesses("EpicGamesLauncher"))
            {
                try
                {
                    if (p.HasExited)
                        continue;

                    string path = null;
                    try { path = p.MainModule.FileName; } catch { }

                    if (!string.IsNullOrWhiteSpace(path))
                        candidates.Add(path);
                }
                catch { }
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
                        return Path.GetFullPath(candidate);
                }
                catch { }
            }

            return null;
        }

        private static void AddCandidates(List<string> candidates, string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                return;

            candidates.Add(Path.Combine(
                basePath, "Epic Games", "Launcher", "Portal", "Binaries",
                "Win32", "EpicGamesLauncher.exe"));

            candidates.Add(Path.Combine(
                basePath, "Epic Games", "Launcher", "Portal", "Binaries",
                "Win64", "EpicGamesLauncher.exe"));
        }

        private static void StopLauncherProcesses()
        {
            foreach (Process p in SafeGetProcesses("EpicGamesLauncher"))
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
                finally
                {
                    p.Dispose();
                }
            }

            Thread.Sleep(350);
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] processes;

            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                yield break;
            }

            foreach (Process p in processes)
                yield return p;
        }
    }
}
