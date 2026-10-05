using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PlayniteAccountManager.Services
{
    internal sealed class EAAppAdapter
    {
        private static readonly string[] ProcessNames =
        {
            "EADesktop",
            "EALauncher"
        };

        private static readonly string[] ExecutableNames =
        {
            "EALauncher.exe",
            "EADesktop.exe"
        };

        private readonly Action<string> log;
        private readonly EAAppSessionStore sessionStore;

        public EAAppAdapter(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            sessionStore = new EAAppSessionStore(pluginUserDataPath, this.log);
        }

        public bool PrepareForManualLogin(out string error)
        {
            error = null;

            try
            {
                // Resolve the launcher before stopping EA processes so a custom
                // installation can still be discovered from the live process.
                string exe = FindExecutable();
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "Nie znaleziono EA App na tym komputerze. Sprawdź, czy EADesktop.exe lub EALauncher.exe są zainstalowane.";
                    return false;
                }

                StopProcesses();

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                Start(exe);
                log("EA App: active session cleared and launcher started for manual login.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się przygotować EA App do ręcznego logowania: " + ex.Message;
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
                error = "Nie udało się wyczyścić sesji EA App: " + ex.Message;
                return false;
            }
        }

        private static void Start(string exe)
        {
            using (var process = Process.Start(new ProcessStartInfo
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

        private string FindExecutable()
        {
            var candidates = new List<string>();

            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string pf64 = Environment.GetEnvironmentVariable("ProgramW6432");
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Default EA App location documented by EA.
            AddKnownInstallPaths(candidates, pf);
            AddKnownInstallPaths(candidates, pf86);
            AddKnownInstallPaths(candidates, pf64);
            AddKnownInstallPaths(candidates, localAppData);

            // If the user installed EA App somewhere else, use Windows registry
            // information written by the installer.
            AddRegistryInstallPaths(candidates);

            // A running launcher is the most authoritative source of its actual
            // executable path and also covers non-standard/custom installations.
            foreach (string processName in ProcessNames)
            foreach (Process p in SafeGetProcesses(processName))
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

            // Last-resort search inside likely EA installation roots. This handles
            // layouts where the installer added another nested directory level.
            AddRecursiveCandidates(candidates, Path.Combine(pf ?? string.Empty, "Electronic Arts", "EA Desktop"));
            AddRecursiveCandidates(candidates, Path.Combine(pf86 ?? string.Empty, "Electronic Arts", "EA Desktop"));
            AddRecursiveCandidates(candidates, Path.Combine(pf64 ?? string.Empty, "Electronic Arts", "EA Desktop"));
            AddRecursiveCandidates(candidates, Path.Combine(localAppData ?? string.Empty, "Electronic Arts", "EA Desktop"));

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
                        log("EA App: detected launcher at " + candidate);
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            log("EA App: launcher executable was not found. ProgramFiles=" + (pf ?? "<null>") + ", ProgramFiles(x86)=" + (pf86 ?? "<null>") + ", ProgramW6432=" + (pf64 ?? "<null>"));
            return null;
        }

        private static void AddKnownInstallPaths(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            string basePath = Path.Combine(
                root, "Electronic Arts", "EA Desktop", "EA Desktop");

            candidates.Add(Path.Combine(basePath, "EALauncher.exe"));
            candidates.Add(Path.Combine(basePath, "EADesktop.exe"));
        }

        private static void AddRegistryInstallPaths(List<string> candidates)
        {
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        {
                            AddFromUninstallKey(candidates, baseKey);
                            AddFromAppPaths(candidates, baseKey, "EALauncher.exe");
                            AddFromAppPaths(candidates, baseKey, "EADesktop.exe");
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void AddFromUninstallKey(List<string> candidates, RegistryKey baseKey)
        {
            using (RegistryKey uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
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

                            bool looksLikeEa =
                                (!string.IsNullOrWhiteSpace(displayName) &&
                                 (displayName.IndexOf("EA App", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  displayName.IndexOf("EA Desktop", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                (!string.IsNullOrWhiteSpace(publisher) &&
                                 publisher.IndexOf("Electronic Arts", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (!looksLikeEa)
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

        private static void AddFromAppPaths(List<string> candidates, RegistryKey baseKey, string executableName)
        {
            using (RegistryKey key = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executableName))
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

            if (path.StartsWith("\"", StringComparison.Ordinal) && path.EndsWith("\"", StringComparison.Ordinal))
                path = path.Substring(1, path.Length - 2);

            int comma = path.IndexOf(',');
            if (comma > 0)
                path = path.Substring(0, comma);

            path = path.Trim().Trim('"');
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(path);
                return;
            }

            if (!string.IsNullOrWhiteSpace(Path.GetFileName(path)))
            {
                foreach (string executableName in ExecutableNames)
                    candidates.Add(Path.Combine(path, executableName));
            }
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
