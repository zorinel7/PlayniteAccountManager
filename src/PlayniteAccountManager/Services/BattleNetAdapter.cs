using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Battle.net manual-login adapter.
    ///
    /// The adapter locates the installed Battle.net launcher, closes the
    /// current Battle.net processes, clears local authentication/session state,
    /// starts the detected launcher and then returns control to the shared
    /// manual credential dialog.
    /// </summary>
    internal sealed class BattleNetAdapter
    {
        private static readonly string[] ProcessNames =
        {
            "Battle.net",
            "Battle.net Launcher",
            "Agent"
        };

        private readonly Action<string> log;
        private readonly BattleNetSessionStore sessionStore;

        public BattleNetAdapter(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            sessionStore = new BattleNetSessionStore(this.log);
        }

        public bool PrepareForManualLogin(out string error)
        {
            error = null;

            try
            {
                string launcherPath = FindLauncherExecutable();
                if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                {
                    error = "Nie znaleziono Battle.net na tym komputerze. Przeszukano standardowe lokalizacje, rejestr, uruchomione procesy i skróty Start Menu.";
                    return false;
                }

                log("Battle.net: wykryto launcher pod ścieżką: " + launcherPath);
                log("Battle.net: zamykam aktualne procesy launchera.");
                StopProcesses();

                // Give Battle.net Agent a moment to exit before touching its files.
                Thread.Sleep(1000);

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                Thread.Sleep(800);

                Start(launcherPath);

                log("Battle.net: launcher uruchomiony do ręcznego logowania.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się przygotować Battle.net do ręcznego logowania: " + ex.Message;
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
                error = "Nie udało się wyczyścić sesji Battle.net: " + ex.Message;
                return false;
            }
        }

        private static void Start(string executable)
        {
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            }))
            {
            }
        }

        private static void StopProcesses()
        {
            foreach (string processName in ProcessNames)
            foreach (Process process in SafeGetProcesses(processName))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.CloseMainWindow(); } catch { }

                        if (!process.WaitForExit(1500))
                        {
                            try { process.Kill(); } catch { }
                            try { process.WaitForExit(1200); } catch { }
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private string FindLauncherExecutable()
        {
            var candidates = new List<string>();

            // A running Battle.net instance provides the most authoritative
            // path and also covers custom installation locations.
            foreach (string processName in ProcessNames)
            foreach (Process process in SafeGetProcesses(processName))
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    try
                    {
                        string path = process.MainModule.FileName;
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            string fileName = Path.GetFileName(path);
                            if (string.Equals(fileName, "Battle.net Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(fileName, "Battle.net.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                candidates.Add(path);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf64 = Environment.GetEnvironmentVariable("ProgramW6432");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            AddKnownInstallPaths(candidates, pf86);
            AddKnownInstallPaths(candidates, pf);
            AddKnownInstallPaths(candidates, pf64);
            AddKnownInstallPaths(candidates, local);

            candidates.AddRange(FindFromUninstallRegistry());
            candidates.AddRange(FindFromAppPaths());
            candidates.AddRange(FindFromStartMenuShortcuts());

            AddRecursiveCandidates(candidates, Path.Combine(pf86 ?? string.Empty, "Battle.net"));
            AddRecursiveCandidates(candidates, Path.Combine(pf ?? string.Empty, "Battle.net"));
            AddRecursiveCandidates(candidates, Path.Combine(pf64 ?? string.Empty, "Battle.net"));
            AddRecursiveCandidates(candidates, Path.Combine(local ?? string.Empty, "Battle.net"));

            foreach (string candidate in candidates
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeCandidate)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(candidate) &&
                        (string.Equals(Path.GetFileName(candidate), "Battle.net Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Path.GetFileName(candidate), "Battle.net.exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        log("Battle.net: wykryto launcher: " + candidate);
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            log("Battle.net: nie znaleziono launchera. ProgramFiles=" +
                (pf ?? "<null>") +
                ", ProgramFiles(x86)=" + (pf86 ?? "<null>") +
                ", ProgramW6432=" + (pf64 ?? "<null>"));

            return null;
        }

        private static void AddKnownInstallPaths(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            string basePath = Path.Combine(root, "Battle.net");
            candidates.Add(Path.Combine(basePath, "Battle.net Launcher.exe"));
            candidates.Add(Path.Combine(basePath, "Battle.net.exe"));
        }

        private static IEnumerable<string> FindFromUninstallRegistry()
        {
            var results = new List<string>();
            const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
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
                        if (app == null)
                            continue;

                        string name = app.GetValue("DisplayName") as string;
                        string publisher = app.GetValue("Publisher") as string;

                        bool looksLikeBattleNet =
                            (!string.IsNullOrWhiteSpace(name) &&
                             (name.IndexOf("Battle.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("Blizzard Battle.net", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                            (!string.IsNullOrWhiteSpace(publisher) &&
                             publisher.IndexOf("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase) >= 0);

                        if (!looksLikeBattleNet)
                            continue;

                        AddRegistryPath(results, app.GetValue("InstallLocation") as string);
                        AddRegistryPath(results, app.GetValue("DisplayIcon") as string);
                    }
                }
                catch
                {
                }
                finally
                {
                    if (uninstallKey != null) uninstallKey.Dispose();
                    if (baseKey != null) baseKey.Dispose();
                }
            }

            return results;
        }

        private static IEnumerable<string> FindFromAppPaths()
        {
            var results = new List<string>();

            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
            {
                RegistryKey baseKey = null;

                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    AddAppPath(results, baseKey, "Battle.net Launcher.exe");
                    AddAppPath(results, baseKey, "Battle.net.exe");
                }
                catch
                {
                }
                finally
                {
                    if (baseKey != null) baseKey.Dispose();
                }
            }

            return results;
        }

        private static void AddAppPath(List<string> results, RegistryKey baseKey, string executableName)
        {
            using (RegistryKey key = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executableName))
            {
                if (key == null)
                    return;

                AddRegistryPath(results, key.GetValue(string.Empty) as string);
                AddRegistryPath(results, key.GetValue("Path") as string);
            }
        }

        private static void AddRegistryPath(List<string> candidates, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            string path = value.Trim().Trim('"');
            int comma = path.IndexOf(',');
            if (comma > 0)
                path = path.Substring(0, comma).Trim().Trim('"');

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(path);
                return;
            }

            candidates.Add(Path.Combine(path, "Battle.net Launcher.exe"));
            candidates.Add(Path.Combine(path, "Battle.net.exe"));
        }

        private static IEnumerable<string> FindFromStartMenuShortcuts()
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
                        if (link.IndexOf("Battle.net", StringComparison.OrdinalIgnoreCase) < 0 &&
                            link.IndexOf("Blizzard", StringComparison.OrdinalIgnoreCase) < 0)
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

                            string targetPath = target as string;
                            if (!string.IsNullOrWhiteSpace(targetPath) &&
                                (string.Equals(Path.GetFileName(targetPath), "Battle.net Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(Path.GetFileName(targetPath), "Battle.net.exe", StringComparison.OrdinalIgnoreCase)))
                            {
                                results.Add(targetPath);
                            }
                        }
                        catch
                        {
                        }
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
            catch
            {
            }
            finally
            {
                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                {
                    try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
                }
            }

            return results;
        }

        private static void AddRecursiveCandidates(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;

            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    if (string.Equals(name, "Battle.net Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Battle.net.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(file);
                    }
                }
            }
            catch
            {
            }
        }

        private static string NormalizeCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            int comma = path.IndexOf(',');
            if (comma > 0)
                path = path.Substring(0, comma).Trim().Trim('"');

            return path;
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
