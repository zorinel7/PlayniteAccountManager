using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Rockstar Games Launcher manual-login adapter.
    ///
    /// Detects the installed Launcher.exe, closes Rockstar launcher processes,
    /// clears only local user profile/session data, starts the launcher again
    /// and returns control to the shared manual credential dialog.
    /// </summary>
    internal sealed class RockstarGamesLauncherAdapter
    {
        private static readonly string[] ProcessNames =
        {
            "Launcher",
            "Rockstar-Games-Launcher",
            "LauncherPatcher",
            "SocialClubHelper",
            "RockstarService",
            "RockstarErrorHandler"
        };

        private static readonly string[] ExecutableNames =
        {
            "Launcher.exe",
            "Rockstar-Games-Launcher.exe"
        };

        private readonly Action<string> log;
        private readonly RockstarGamesLauncherSessionStore sessionStore;

        public RockstarGamesLauncherAdapter(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            sessionStore = new RockstarGamesLauncherSessionStore(this.log);
        }

        public bool PrepareForManualLogin(out string error)
        {
            error = null;

            try
            {
                string launcherPath = FindLauncherExecutable();
                if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
                {
                    error = "Nie znaleziono Rockstar Games Launcher na tym komputerze. Sprawdź, czy Launcher.exe jest zainstalowany.";
                    return false;
                }

                log("Rockstar Games Launcher: wykryto launcher pod ścieżką: " + launcherPath);
                log("Rockstar Games Launcher: zamykam aktualne procesy launchera.");
                StopProcesses();

                // RockstarService is installed as a Windows service and may briefly
                // respawn launcher components after Launcher.exe exits. Repeat the
                // process sweep immediately before deleting the profile state.
                System.Threading.Thread.Sleep(700);
                StopProcesses();
                System.Threading.Thread.Sleep(500);

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                System.Threading.Thread.Sleep(800);

                Start(launcherPath);

                log("Rockstar Games Launcher: launcher uruchomiony do ręcznego logowania.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się przygotować Rockstar Games Launcher do ręcznego logowania: " + ex.Message;
                return false;
            }
        }

        public bool ClearSession(out string error)
        {
            try
            {
                StopProcesses();
                System.Threading.Thread.Sleep(700);
                StopProcesses();
                return sessionStore.ClearLiveState(out error);
            }
            catch (Exception ex)
            {
                error = "Nie udało się wyczyścić sesji Rockstar Games Launcher: " + ex.Message;
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
                    if (!IsRockstarProcess(process))
                        continue;

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

        private static bool IsRockstarProcess(Process process)
        {
            try
            {
                string path = process.MainModule.FileName;
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                return path.IndexOf(
                           Path.DirectorySeparatorChar + "Rockstar Games" + Path.DirectorySeparatorChar,
                           StringComparison.OrdinalIgnoreCase) >= 0 ||
                       path.IndexOf(
                           Path.AltDirectorySeparatorChar + "Rockstar Games" + Path.AltDirectorySeparatorChar,
                           StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private string FindLauncherExecutable()
        {
            var candidates = new List<string>();

            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string pf64 = Environment.GetEnvironmentVariable("ProgramW6432");
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            AddKnownInstallPaths(candidates, pf);
            AddKnownInstallPaths(candidates, pf86);
            AddKnownInstallPaths(candidates, pf64);
            AddKnownInstallPaths(candidates, localAppData);

            AddRegistryInstallPaths(candidates);

            // A running Rockstar launcher gives us the exact executable path and
            // also covers installations outside the standard folders.
            foreach (string processName in ProcessNames)
            foreach (Process process in SafeGetProcesses(processName))
            {
                try
                {
                    if (!IsRockstarProcess(process) || process.HasExited)
                        continue;

                    try
                    {
                        string path = process.MainModule.FileName;
                        if (!string.IsNullOrWhiteSpace(path))
                            candidates.Add(path);
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

            AddRecursiveCandidates(
                candidates,
                Path.Combine(pf ?? string.Empty, "Rockstar Games", "Launcher"));
            AddRecursiveCandidates(
                candidates,
                Path.Combine(pf86 ?? string.Empty, "Rockstar Games", "Launcher"));
            AddRecursiveCandidates(
                candidates,
                Path.Combine(pf64 ?? string.Empty, "Rockstar Games", "Launcher"));
            AddRecursiveCandidates(
                candidates,
                Path.Combine(localAppData ?? string.Empty, "Rockstar Games", "Launcher"));

            foreach (string candidate in candidates
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizeCandidate)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(candidate) &&
                        ExecutableNames.Contains(Path.GetFileName(candidate), StringComparer.OrdinalIgnoreCase))
                    {
                        log("Rockstar Games Launcher: detected launcher at " + candidate);
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            log("Rockstar Games Launcher: launcher executable was not found. ProgramFiles=" +
                (pf ?? "<null>") +
                ", ProgramFiles(x86)=" +
                (pf86 ?? "<null>") +
                ", ProgramW6432=" +
                (pf64 ?? "<null>") + ".");
            return null;
        }

        private static void AddKnownInstallPaths(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            string basePath = Path.Combine(root, "Rockstar Games", "Launcher");
            candidates.Add(Path.Combine(basePath, "Launcher.exe"));
            candidates.Add(Path.Combine(basePath, "Rockstar-Games-Launcher.exe"));
        }

        private static void AddRegistryInstallPaths(List<string> candidates)
        {
            foreach (RegistryHive hive in new[]
            {
                RegistryHive.LocalMachine,
                RegistryHive.CurrentUser
            })
            {
                foreach (RegistryView view in new[]
                {
                    RegistryView.Registry64,
                    RegistryView.Registry32,
                    RegistryView.Default
                })
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        {
                            AddRockstarLauncherKey(candidates, baseKey);
                            AddFromUninstallKey(candidates, baseKey);
                            AddFromAppPaths(candidates, baseKey, "Launcher.exe");
                            AddFromAppPaths(candidates, baseKey, "Rockstar-Games-Launcher.exe");
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void AddRockstarLauncherKey(List<string> candidates, RegistryKey baseKey)
        {
            foreach (string path in new[]
            {
                @"SOFTWARERockstar GamesLauncher",
                @"SOFTWAREWOW6432NodeRockstar GamesLauncher"
            })
            {
                try
                {
                    using (RegistryKey key = baseKey.OpenSubKey(path))
                    {
                        if (key == null)
                            continue;

                        AddRegistryPath(candidates, key.GetValue("InstallFolder") as string);
                        AddRegistryPath(candidates, key.GetValue("InstallLocation") as string);
                        AddRegistryPath(candidates, key.GetValue("InstallPath") as string);
                    }
                }
                catch
                {
                }
            }
        }

        private static void AddFromUninstallKey(List<string> candidates, RegistryKey baseKey)
        {
            using (RegistryKey uninstall = baseKey.OpenSubKey(
                @"SOFTWAREMicrosoftWindowsCurrentVersionUninstall"))
            {
                if (uninstall == null)
                    return;

                string[] subKeyNames;
                try { subKeyNames = uninstall.GetSubKeyNames(); }
                catch { return; }

                foreach (string subKeyName in subKeyNames)
                {
                    try
                    {
                        using (RegistryKey key = uninstall.OpenSubKey(subKeyName))
                        {
                            if (key == null)
                                continue;

                            string displayName = key.GetValue("DisplayName") as string;
                            string publisher = key.GetValue("Publisher") as string;

                            bool looksLikeRockstarLauncher =
                                (!string.IsNullOrWhiteSpace(displayName) &&
                                 displayName.IndexOf("Rockstar Games Launcher", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (!string.IsNullOrWhiteSpace(publisher) &&
                                 publisher.IndexOf("Rockstar Games", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                 !string.IsNullOrWhiteSpace(displayName) &&
                                 displayName.IndexOf("Launcher", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (!looksLikeRockstarLauncher)
                                continue;

                            AddRegistryPath(candidates, key.GetValue("InstallLocation") as string);
                            AddRegistryPath(candidates, key.GetValue("DisplayIcon") as string);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void AddFromAppPaths(
            List<string> candidates,
            RegistryKey baseKey,
            string executableName)
        {
            using (RegistryKey key = baseKey.OpenSubKey(
                @"SOFTWAREMicrosoftWindowsCurrentVersionApp Paths" + executableName))
            {
                if (key == null)
                    return;

                try
                {
                    AddRegistryPath(candidates, key.GetValue(string.Empty) as string);
                    AddRegistryPath(candidates, key.GetValue("Path") as string);
                }
                catch
                {
                }
            }
        }

        private static void AddRegistryPath(List<string> candidates, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            string path = value.Trim();

            if (path.StartsWith("\"", StringComparison.Ordinal) &&
                path.EndsWith("\"", StringComparison.Ordinal))
            {
                path = path.Substring(1, path.Length - 2);
            }

            int comma = path.IndexOf(',');
            if (comma > 0)
                path = path.Substring(0, comma);

            path = path.Trim().Trim('"');

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(path);
                return;
            }

            foreach (string executableName in ExecutableNames)
                candidates.Add(Path.Combine(path, executableName));
        }

        private static void AddRecursiveCandidates(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;

            try
            {
                foreach (string file in Directory.EnumerateFiles(
                    root,
                    "*.exe",
                    SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    if (ExecutableNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        candidates.Add(file);
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

            string path = value.Trim().Trim('"');
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
