using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Ubisoft Connect adapter based on the working keyboard flow from the
    /// supplied AutoHotkey example. The external script is not required.
    ///
    /// The adapter dynamically locates the launcher, clears Ubisoft's local
    /// per-user state, starts the launcher, and then hands the real input work
    /// to UbisoftConnectUiAutomation, which uses Win32 SendInput plus UIA focus.
    /// </summary>
    internal sealed class UbisoftConnectAdapter
    {
        private static readonly string[] ProcessNames =
        {
            "upc",
            "UplayWebCore",
            "UbisoftConnect",
            "UbisoftGameLauncher",
            "UbisoftGameLauncher64"
        };

        private readonly Action<string> log;
        private readonly UbisoftConnectUiAutomation uiAutomation;
        private UbisoftSession activeSession;

        public UbisoftConnectAdapter(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            uiAutomation = new UbisoftConnectUiAutomation(this.log);
        }

        public bool PrepareForManualLogin(out string error)
        {
            error = null;

            try
            {
                string launcherPath = FindLauncherExecutable();
                if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                {
                    error = "Nie znaleziono programu Ubisoft Connect na tym komputerze. Przeszukano standardowe lokalizacje, rejestr, skróty Start Menu i katalogi instalacyjne.";
                    return false;
                }

                log("Ubisoft Connect: wykryto launcher pod ścieżką: " + launcherPath);
                StopLauncherProcesses();
                TryClearLocalStateNonBlocking();
                Thread.Sleep(800);

                Process launcher = Process.Start(new ProcessStartInfo
                {
                    FileName = launcherPath,
                    WorkingDirectory = Path.GetDirectoryName(launcherPath),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                });

                if (launcher == null)
                {
                    error = "Windows nie uruchomił Ubisoft Connect.";
                    return false;
                }

                launcher.Dispose();
                log("Ubisoft Connect: sesja została przygotowana i launcher uruchomiony do ręcznego logowania.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się przygotować Ubisoft Connect do ręcznego logowania: " + ex.Message;
                return false;
            }
        }

        public bool PrepareAndLogin(AccountRecord account, string password, out string error)
        {
            error = null;
            if (account == null || account.Id == Guid.Empty)
            {
                error = "Nie wybrano konta Ubisoft Connect.";
                return false;
            }

            if (account.Launcher != LauncherType.UbisoftConnect)
            {
                error = "Wybrane konto nie jest kontem Ubisoft Connect.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanego dla konta „" + account.Name + "”.";
                return false;
            }

            string launcherPath = FindLauncherExecutable();
            if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
            {
                error = "Nie znaleziono programu Ubisoft Connect na tym komputerze. Przeszukano standardowe lokalizacje, rejestr i skróty Start Menu.";
                return false;
            }

            try
            {
                log("Ubisoft Connect: wybrany launcher: " + launcherPath);
                StopLauncherProcesses();
                TryClearLocalStateNonBlocking();
                Thread.Sleep(2000);

                log("Ubisoft Connect: uruchamiam launcher jak w działającym AHK.");
                Process launcher = Process.Start(new ProcessStartInfo
                {
                    FileName = launcherPath,
                    WorkingDirectory = Path.GetDirectoryName(launcherPath),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                });

                if (launcher == null)
                {
                    error = "Windows nie uruchomił Ubisoft Connect.";
                    return false;
                }

                launcher.Dispose();

                if (!uiAutomation.Login(account.UserName, password, 60, out error))
                    return false;

                activeSession = new UbisoftSession
                {
                    AccountId = account.Id,
                    Authenticated = true
                };

                log("Ubisoft Connect: automatyczne logowanie zakończone pomyślnie dla konta „" + account.Name + "”.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Błąd automatycznego logowania Ubisoft Connect: " + ex.Message;
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
                activeSession = null;
            }
        }

        public bool HasActiveSession => activeSession != null && activeSession.Authenticated;

        private void TryClearLocalStateNonBlocking()
        {
            string localState = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ubisoft Game Launcher");

            if (!Directory.Exists(localState))
            {
                log("Ubisoft Connect: brak lokalnego katalogu stanu, pomijam czyszczenie.");
                return;
            }

            try
            {
                ClearReadOnlyAttributes(localState);
                using (Process p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                        Arguments = "/c rmdir /s /q " + QuoteForCmd(localState),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };
                    p.Start();
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);

                    log("Ubisoft Connect: próba wyczyszczenia lokalnego stanu (opcjonalna), kod wyjścia=" + p.ExitCode + ".");
                    if (!string.IsNullOrWhiteSpace(stderr))
                        log("Ubisoft Connect: rmdir stderr: " + stderr.Trim());
                    if (!string.IsNullOrWhiteSpace(stdout))
                        log("Ubisoft Connect: rmdir stdout: " + stdout.Trim());
                }

                if (Directory.Exists(localState))
                    log("Ubisoft Connect: nie udało się całkowicie usunąć lokalnego stanu — NIE zatrzymuję automatycznego wpisywania danych.");
                else
                    log("Ubisoft Connect: lokalny stan usunięty.");
            }
            catch (Exception ex)
            {
                log("Ubisoft Connect: czyszczenie lokalnego stanu pominięte: " + ex.Message);
            }
        }

        private static string QuoteForCmd(string value)
        {
            return "\"" + value + "\"";
        }

        private static void ClearReadOnlyAttributes(string directory)
        {
            try
            {
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                foreach (string child in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(child, FileAttributes.Normal); } catch { }
                }

                try { File.SetAttributes(directory, FileAttributes.Normal); } catch { }
            }
            catch { }
        }

        private string FindLauncherExecutable()
        {
            var candidates = new List<string>();

            foreach (Process process in SafeGetProcesses("upc"))
            {
                try
                {
                    string path = process.MainModule.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                        candidates.Add(path);
                }
                catch { }
                finally { process.Dispose(); }
            }

            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            AddInstallCandidates(candidates, pf86);
            AddInstallCandidates(candidates, pf);
            AddInstallCandidates(candidates, local);
            candidates.AddRange(FindFromUninstallRegistry());
            candidates.AddRange(FindFromStartMenuShortcuts());

            foreach (string candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
                    if (File.Exists(full))
                    {
                        if (string.Equals(Path.GetFileName(full), "UbisoftConnect.exe", StringComparison.OrdinalIgnoreCase))
                            return full;
                    }
                }
                catch { }
            }

            foreach (string candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
                    if (File.Exists(full))
                        return full;
                }
                catch { }
            }

            return null;
        }

        private static void AddInstallCandidates(List<string> results, string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return;

            results.Add(Path.Combine(basePath, "Ubisoft", "Ubisoft Game Launcher", "UbisoftConnect.exe"));
            results.Add(Path.Combine(basePath, "Ubisoft", "Ubisoft Game Launcher", "upc.exe"));
            results.Add(Path.Combine(basePath, "Ubisoft Game Launcher", "UbisoftConnect.exe"));
            results.Add(Path.Combine(basePath, "Ubisoft Game Launcher", "upc.exe"));
        }

        private IEnumerable<string> FindFromStartMenuShortcuts()
        {
            var results = new List<string>();
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };

            Type shellType = null;
            object shell = null;
            try
            {
                shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    return results;
                shell = Activator.CreateInstance(shellType);

                foreach (string folder in folders.Where(Directory.Exists))
                {
                    IEnumerable<string> links;
                    try { links = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories); }
                    catch { continue; }

                    foreach (string link in links)
                    {
                        if (link.IndexOf("Ubisoft", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        object shortcut = null;
                        try
                        {
                            shortcut = shellType.InvokeMember(
                                "CreateShortcut",
                                System.Reflection.BindingFlags.InvokeMethod,
                                null,
                                shell,
                                new object[] { link });

                            object target = shortcut.GetType().InvokeMember(
                                "TargetPath",
                                System.Reflection.BindingFlags.GetProperty,
                                null,
                                shortcut,
                                null);

                            string path = target as string;
                            if (!string.IsNullOrWhiteSpace(path) &&
                                (path.EndsWith("UbisoftConnect.exe", StringComparison.OrdinalIgnoreCase) ||
                                 path.EndsWith("upc.exe", StringComparison.OrdinalIgnoreCase)))
                            {
                                results.Add(path);
                            }
                        }
                        catch { }
                        finally
                        {
                            if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                            {
                                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); } catch { }
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                {
                    try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
                }
            }

            return results;
        }

        private IEnumerable<string> FindFromUninstallRegistry()
        {
            var results = new List<string>();
            const string subKey = @"SOFTWAREMicrosoftWindowsCurrentVersionUninstall";

            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey baseKey = null;
                RegistryKey uninstallKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    uninstallKey = baseKey.OpenSubKey(subKey);
                    if (uninstallKey == null)
                        continue;

                    foreach (string child in uninstallKey.GetSubKeyNames())
                    using (RegistryKey app = uninstallKey.OpenSubKey(child))
                    {
                        if (app == null) continue;
                        string name = app.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name) ||
                            (name.IndexOf("Ubisoft Connect", StringComparison.OrdinalIgnoreCase) < 0 &&
                             name.IndexOf("Ubisoft Game Launcher", StringComparison.OrdinalIgnoreCase) < 0))
                            continue;

                        string location = app.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            results.Add(Path.Combine(location, "UbisoftConnect.exe"));
                            results.Add(Path.Combine(location, "upc.exe"));
                        }

                        string icon = app.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            icon = icon.Trim().Trim('"');
                            int comma = icon.IndexOf(',');
                            if (comma > 0) icon = icon.Substring(0, comma).Trim().Trim('"');
                            if (icon.EndsWith("UbisoftConnect.exe", StringComparison.OrdinalIgnoreCase) ||
                                icon.EndsWith("upc.exe", StringComparison.OrdinalIgnoreCase))
                                results.Add(icon);
                        }
                    }
                }
                catch { }
                finally
                {
                    if (uninstallKey != null) uninstallKey.Dispose();
                    if (baseKey != null) baseKey.Dispose();
                }
            }

            return results;
        }

        private void StopLauncherProcesses()
        {
            foreach (string name in ProcessNames)
            foreach (Process process in SafeGetProcesses(name))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.CloseMainWindow(); } catch { }
                        if (!process.WaitForExit(1500))
                            process.Kill();
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }

            Thread.Sleep(800);
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { yield break; }
            foreach (Process process in processes)
                yield return process;
        }

        private sealed class UbisoftSession
        {
            public Guid AccountId { get; set; }
            public bool Authenticated { get; set; }
        }
    }
}
