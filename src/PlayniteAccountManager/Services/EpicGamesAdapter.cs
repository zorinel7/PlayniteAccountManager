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

        private readonly Action<string> log;
        private readonly EpicGamesSessionStore sessionStore;
        private readonly EpicGamesUiAutomation uiAutomation;

        public EpicGamesAdapter(Action<string> log)
        {
            this.log = log ?? (_ => { });
            sessionStore = new EpicGamesSessionStore(this.log);
            uiAutomation = new EpicGamesUiAutomation(this.log);
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
                    error = "Nie znaleziono Epic Games Launcher na tym komputerze.";
                    return false;
                }

                StopProcesses();

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                }))
                {
                }

                log("Epic Games: active session cleared and launcher started for manual login.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się przygotować Epic Games do ręcznego logowania: " + ex.Message;
                return false;
            }
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

            if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanego dla konta „" + account.Name + "”.";
                return false;
            }

            try
            {
                string exe = FindExecutable();
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "Nie znaleziono Epic Games Launcher na tym komputerze.";
                    return false;
                }

                log("Epic Games: automatyczne logowanie dla konta „" + account.Name + "”.");
                log("Epic Games: zamykam poprzednią sesję i launcher.");
                StopProcesses();

                // Twoja sekwencja AHK ma 2 sekundy między zamknięciem launchera
                // a jego ponownym uruchomieniem.
                Thread.Sleep(2000);

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                }))
                {
                }

                log("Epic Games: launcher uruchomiony — odtwarzam sekwencję automatycznego logowania z AHK.");
                return uiAutomation.Login(account.UserName, password, out error);
            }
            catch (Exception ex)
            {
                error = "Błąd automatycznego logowania Epic Games: " + ex.Message;
                return false;
            }
        }

        public bool ClearSession(out string error)
        {
            try
            {
                StopProcesses();
                return sessionStore.ClearLiveState(out error);
            }
            catch (Exception ex)
            {
                error = "Nie udało się wyczyścić sesji Epic Games: " + ex.Message;
                return false;
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
                        string path = p.MainModule.FileName;
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
