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
    /// GOG Galaxy manual-login adapter.
    ///
    /// The adapter locates the installed GalaxyClient.exe, closes the current
    /// Galaxy processes, clears only authentication/session state, starts the
    /// detected launcher and returns control to the shared manual credential
    /// dialog.
    /// </summary>
    internal sealed class GOGGalaxyAdapter
    {
        private static readonly string[] ProcessNames =
        {
            "GalaxyClient",
            "GalaxyClientService",
            "GalaxyCommunication"
        };

        private readonly Action<string> log;
        private readonly GOGGalaxySessionStore sessionStore;

        public GOGGalaxyAdapter(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            sessionStore = new GOGGalaxySessionStore(this.log);
        }

        public bool PrepareForManualLogin(out string error)
        {
            error = null;

            try
            {
                string launcherPath = FindLauncherExecutable();
                if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                {
                    error = "Nie znaleziono GOG Galaxy na tym komputerze. Przeszukano standardowe lokalizacje, rejestr, uruchomione procesy i skróty Start Menu.";
                    return false;
                }

                log("GOG Galaxy: wykryto launcher pod ścieżką: " + launcherPath);
                log("GOG Galaxy: zamykam aktualne procesy launchera.");
                StopProcesses();

                Thread.Sleep(1000);

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                Thread.Sleep(800);

                Start(launcherPath);

                log("GOG Galaxy: launcher uruchomiony do ręcznego logowania.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się przygotować GOG Galaxy do ręcznego logowania: " + ex.Message;
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
                error = "Nie udało się wyczyścić sesji GOG Galaxy: " + ex.Message;
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

                        if (!process.WaitForExit(1200))
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
                        if (!string.IsNullOrWhiteSpace(path) &&
                            string.Equals(Path.GetFileName(path), "GalaxyClient.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(path);
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

            candidates.AddRange(FindFromGogRegistryPaths());
            candidates.AddRange(FindFromUninstallRegistry());
            candidates.AddRange(FindFromAppPaths());
            candidates.AddRange(FindFromStartMenuShortcuts());

            AddRecursiveCandidates(candidates, Path.Combine(pf86 ?? string.Empty, "GOG Galaxy"));
            AddRecursiveCandidates(candidates, Path.Combine(pf ?? string.Empty, "GOG Galaxy"));
            AddRecursiveCandidates(candidates, Path.Combine(pf64 ?? string.Empty, "GOG Galaxy"));
            AddRecursiveCandidates(candidates, Path.Combine(pf86 ?? string.Empty, "GalaxyClient"));
            AddRecursiveCandidates(candidates, Path.Combine(pf ?? string.Empty, "GalaxyClient"));
            AddRecursiveCandidates(candidates, Path.Combine(pf64 ?? string.Empty, "GalaxyClient"));

            foreach (string candidate in candidates
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeCandidate)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(candidate) &&
                        string.Equals(Path.GetFileName(candidate), "GalaxyClient.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        log("GOG Galaxy: wykryto launcher: " + candidate);
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            log("GOG Galaxy: nie znaleziono GalaxyClient.exe. ProgramFiles=" +
                (pf ?? "<null>") +
                ", ProgramFiles(x86)=" + (pf86 ?? "<null>") +
                ", ProgramW6432=" + (pf64 ?? "<null>"));

            return null;
        }

        private static void AddKnownInstallPaths(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            candidates.Add(Path.Combine(root, "GOG Galaxy", "GalaxyClient.exe"));
            candidates.Add(Path.Combine(root, "GalaxyClient", "GalaxyClient.exe"));
        }

        private static IEnumerable<string> FindFromGogRegistryPaths()
        {
            var results = new List<string>();

            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
            {
                RegistryKey baseKey = null;
                RegistryKey pathsKey = null;

                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    pathsKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\GalaxyClient\paths");
                    if (pathsKey == null)
                        continue;

                    AddRegistryPath(results, pathsKey.GetValue("client") as string);
                }
                catch
                {
                }
                finally
                {
                    if (pathsKey != null) pathsKey.Dispose();
                    if (baseKey != null) baseKey.Dispose();
                }
            }

            return results;
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
                        if (string.IsNullOrWhiteSpace(name) ||
                            name.IndexOf("GOG Galaxy", StringComparison.OrdinalIgnoreCase) < 0)
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
                RegistryKey appPaths = null;

                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    appPaths = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\GalaxyClient.exe");

                    if (appPaths == null)
                        continue;

                    AddRegistryPath(results, appPaths.GetValue(string.Empty) as string);
                    AddRegistryPath(results, appPaths.GetValue("Path") as string);
                }
                catch
                {
                }
                finally
                {
                    if (appPaths != null) appPaths.Dispose();
                    if (baseKey != null) baseKey.Dispose();
                }
            }

            return results;
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

            candidates.Add(Path.Combine(path, "GalaxyClient.exe"));
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
                        if (link.IndexOf("GOG", StringComparison.OrdinalIgnoreCase) < 0 &&
                            link.IndexOf("Galaxy", StringComparison.OrdinalIgnoreCase) < 0)
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
                                string.Equals(Path.GetFileName(targetPath), "GalaxyClient.exe", StringComparison.OrdinalIgnoreCase))
                                results.Add(targetPath);
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
                foreach (string file in Directory.EnumerateFiles(root, "GalaxyClient.exe", SearchOption.AllDirectories))
                    candidates.Add(file);
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
