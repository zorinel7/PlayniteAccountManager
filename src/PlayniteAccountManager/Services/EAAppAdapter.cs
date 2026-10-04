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
                // Do not scan Start Menu/registry when EA is already running.
                // That scan can be surprisingly slow and was the main source
                // of the ~1 minute delay observed before automation started.
                IntPtr existingHwnd = EAAppUiAutomation.FindMainWindowHandlePublic();
                if (existingHwnd != IntPtr.Zero)
                {
                    log("EA App: wykryto już uruchomione okno. Pomijam wyszukiwanie pliku EA App.");
                }
                else
                {
                    string exe = FindEAExecutable();
                    if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                    {
                        error = "Nie znaleziono EA App na tym komputerze.";
                        return false;
                    }

                    bool started = StartEA(exe);
                    if (!started)
                    {
                        error = "Nie udało się uruchomić EA App.";
                        return false;
                    }
                }

                if (!uiAutomation.PrepareAndLogin(account.UserName, password, 60, out error))
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
            return uiAutomation.Logout(out error);
        }

        private static bool StartEA(string exe)
        {
            IntPtr hwnd = EAAppUiAutomation.FindMainWindowHandlePublic();
            if (hwnd != IntPtr.Zero)
                return true;

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

        private static string FindEAExecutable()
        {
            var candidates = new List<string>();
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            AddCandidates(candidates, pf);
            AddCandidates(candidates, pf86);
            AddCandidates(candidates, local);

            foreach (Process p in SafeGetProcesses("EADesktop"))
            {
                try
                {
                    string path = null;
                    try { path = p.MainModule.FileName; } catch { }
                    if (!string.IsNullOrWhiteSpace(path)) candidates.Add(path);
                }
                catch { }
                finally { p.Dispose(); }
            }

            candidates.AddRange(FindFromUninstallRegistry());
            candidates.AddRange(FindFromStartMenuShortcuts());

            foreach (string candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
                    if (File.Exists(full) &&
                        (Path.GetFileName(full).Equals("EADesktop.exe", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileName(full).Equals("EALauncher.exe", StringComparison.OrdinalIgnoreCase)))
                        return full;
                }
                catch { }
            }

            return null;
        }

        private static void AddCandidates(List<string> results, string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return;
            results.Add(Path.Combine(basePath, "Electronic Arts", "EA Desktop", "EADesktop.exe"));
            results.Add(Path.Combine(basePath, "Electronic Arts", "EA Desktop", "EALauncher.exe"));
            results.Add(Path.Combine(basePath, "Electronic Arts", "EA Desktop", "EADesktop.exe"));
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
