using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// EA App account switching. Unlike Steam, EA has no stable local profile
    /// selector we can safely manipulate. We therefore reproduce the normal
    /// sign-out/sign-in flow through the EA App's accessible UI.
    /// </summary>
    internal sealed class EAAppAdapter
    {
        private readonly Action<string> log;
        private readonly EAAppUiAutomation uiAutomation;

        public EAAppAdapter(Action<string> log)
        {
            this.log = log ?? (_ => { });
            uiAutomation = new EAAppUiAutomation(this.log);
        }

        public bool PrepareAndLogin(AccountRecord account, string password, out string error)
        {
            error = null;
            if (account == null || account.Id == Guid.Empty)
            {
                error = "Nie wybrano konta EA App.";
                return false;
            }

            if (account.Launcher != LauncherType.EAApp)
            {
                error = "Wybrane konto nie jest kontem EA App.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(account.UserName) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanych dla konta EA App.";
                return false;
            }

            try
            {
                // Do not use EA's GUI logout at all. The EA app is a Qt/Cef
                // application and its menu coordinates/automation tree vary
                // between builds. Instead use the same local-session reset
                // strategy used by account switchers such as TcNo.
                string exe = FindEAExecutableFast();
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                {
                    error = "Nie znaleziono programu EA App na tym komputerze.";
                    return false;
                }

                StopLauncherProcesses();
                if (!ClearEAAuthenticationState(out error))
                    return false;

                log("EA App: lokalny stan aktywnej sesji wyczyszczony. Uruchamiam EA App.");

                if (!StartEA(exe))
                {
                    error = "Nie udało się uruchomić EA App.";
                    return false;
                }

                // The window is discovered by EnumWindows as soon as the Qt
                // top-level window exists, avoiding the previous long wait for
                // Process.MainWindowHandle.
                if (!uiAutomation.PrepareAndLogin(account.UserName, password, 25, out error))
                    return false;

                log("EA App: konto „" + account.Name + "” jest aktywne. Playnite kontynuuje normalne uruchomienie gry.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Błąd przełączania konta EA App: " + ex.Message;
                log(error);
                return false;
            }
        }

        public bool Logout(out string error)
        {
            error = null;

            try
            {
                StopLauncherProcesses();
                return ClearEAAuthenticationState(out error);
            }
            catch (Exception ex)
            {
                error = "Błąd czyszczenia sesji EA App: " + ex.Message;
                log(error);
                return false;
            }
        }

        private bool ClearEAAuthenticationState(out string error)
        {
            error = null;

            string localAuth = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Electronic Arts", "EA Desktop");

            string programDataAuth = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EA Desktop");

            bool okLocal = ClearDirectoryContents(localAuth, "LocalAppData\\Electronic Arts\\EA Desktop");
            bool okProgram = ClearDirectoryContents(programDataAuth, "ProgramData\\EA Desktop");

            if (!okLocal || !okProgram)
            {
                error = "Nie udało się całkowicie wyczyścić lokalnego stanu logowania EA App.";
                return false;
            }

            log("EA App: wyczyszczono lokalny stan logowania. Przy następnym uruchomieniu wymagane będzie nowe uwierzytelnienie.");
            return true;
        }

        private bool ClearDirectoryContents(string directory, string label)
        {
            if (!Directory.Exists(directory))
            {
                log("EA App: brak " + label + " — nic do czyszczenia.");
                return true;
            }

            bool allOk = true;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    ClearReadOnlyAttributes(directory);

                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            allOk = false;
                            log("EA App: nie udało się usunąć pliku " + file + ": " + ex.Message);
                        }
                    }

                    foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            ClearReadOnlyAttributes(child);
                            Directory.Delete(child, true);
                        }
                        catch (Exception ex)
                        {
                            allOk = false;
                            log("EA App: nie udało się usunąć katalogu " + child + ": " + ex.Message);
                        }
                    }

                    bool empty = !Directory.EnumerateFileSystemEntries(directory).Any();
                    if (empty)
                    {
                        log("EA App: wyczyszczono " + label + ".");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    allOk = false;
                    log("EA App: błąd czyszczenia " + label + " (próba " + (attempt + 1) + "): " + ex.Message);
                }

                Thread.Sleep(150);
            }

            if (!allOk)
                log("EA App: " + label + " nie został całkowicie wyczyszczony.");
            return allOk && !Directory.EnumerateFileSystemEntries(directory).Any();
        }

        private static void ClearReadOnlyAttributes(string directory)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(child, FileAttributes.Normal); } catch { }
                }

                try { File.SetAttributes(directory, FileAttributes.Normal); } catch { }
            }
            catch { }
        }

        private static bool StartEA(string exe)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                }))
                {
                    return p != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string FindEAExecutableFast()
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] candidates =
            {
                Path.Combine(pf ?? string.Empty, "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
                Path.Combine(pf ?? string.Empty, "Electronic Arts", "EA Desktop", "EADesktop.exe"),
                Path.Combine(pf86 ?? string.Empty, "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
                Path.Combine(local, "Electronic Arts", "EA Desktop", "EADesktop.exe")
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }

            // If EA is already open, use its process path before doing any
            // expensive registry/Start Menu scanning.
            foreach (Process p in SafeGetProcesses("EADesktop"))
            {
                try
                {
                    if (p.HasExited)
                        continue;

                    string path = null;
                    try { path = p.MainModule.FileName; } catch { }
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        return path;
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            return FindEAExecutable();
        }

        private void StopLauncherProcesses()
        {
            string[] processNames =
            {
                "EADesktop",
                "EABackgroundService",
                "EALauncher"
            };

            foreach (string name in processNames)
            {
                foreach (Process process in SafeGetProcesses(name))
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        try { process.CloseMainWindow(); } catch { }

                        if (!process.WaitForExit(1200))
                        {
                            try { process.Kill(); } catch { }
                            try { process.WaitForExit(1200); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        log("EA App: nie udało się zatrzymać procesu " + name + ": " + ex.Message);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            Thread.Sleep(250);
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { yield break; }
            foreach (var p in processes) yield return p;
        }

        private static IEnumerable<string> FindFromUninstallRegistry()
        {
            var results = new List<string>();
            const string subKey = @"SOFTWAREMicrosoftWindowsCurrentVersionUninstall";
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                Microsoft.Win32.RegistryKey baseKey = null, uninstall = null;
                try
                {
                    baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    uninstall = baseKey.OpenSubKey(subKey);
                    if (uninstall == null) continue;
                    foreach (string child in uninstall.GetSubKeyNames())
                    {
                        using (var key = uninstall.OpenSubKey(child))
                        {
                            if (key == null) continue;
                            string display = key.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(display) || display.IndexOf("EA", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            string install = key.GetValue("InstallLocation") as string;
                            string displayIcon = key.GetValue("DisplayIcon") as string;
                            if (!string.IsNullOrWhiteSpace(install))
                            {
                                results.Add(Path.Combine(install.Trim('"'), "EADesktop.exe"));
                                results.Add(Path.Combine(install.Trim('"'), "EALauncher.exe"));
                            }
                            if (!string.IsNullOrWhiteSpace(displayIcon)) results.Add(displayIcon.Split(',')[0].Trim('"'));
                        }
                    }
                }
                catch { }
                finally
                {
                    if (uninstall != null) try { uninstall.Dispose(); } catch { }
                    if (baseKey != null) try { baseKey.Dispose(); } catch { }
                }
            }
            return results;
        }

        private static IEnumerable<string> FindFromStartMenuShortcuts()
        {
            var results = new List<string>();
            var folders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) };
            foreach (string folder in folders.Where(Directory.Exists))
            {
                IEnumerable<string> links;
                try { links = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (string link in links)
                {
                    if (link.IndexOf("EA", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                        if (shellType == null) continue;
                        object shell = Activator.CreateInstance(shellType);
                        object shortcut = null;
                        try
                        {
                            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { link });
                            string target = shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
                            if (!string.IsNullOrWhiteSpace(target)) results.Add(target);
                        }
                        finally
                        {
                            if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); } catch { }
                            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
                        }
                    }
                    catch { }
                }
            }
            return results;
        }
    }
}
