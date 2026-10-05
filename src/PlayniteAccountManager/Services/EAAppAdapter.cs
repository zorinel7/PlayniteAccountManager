using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PlayniteAccountManager.Services
{
    internal sealed class EAAppAdapter
    {
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
                StopProcesses();

                if (!sessionStore.ClearLiveState(out error))
                    return false;

                string exe = FindExecutable();
                if (string.IsNullOrWhiteSpace(exe))
                {
                    error = "Nie znaleziono EA App na tym komputerze.";
                    return false;
                }

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
            foreach (Process p in SafeGetProcesses("EADesktop"))
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

            foreach (Process p in SafeGetProcesses("EADesktop"))
            {
                try
                {
                    if (p.HasExited) continue;
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (!string.IsNullOrWhiteSpace(path))
                            candidates.Add(path);
                    }
                    catch { }
                }
                finally { p.Dispose(); }
            }

            foreach (string candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }

            return null;
        }

        private static void Add(List<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            candidates.Add(Path.Combine(root, "Electronic Arts", "EA Desktop", "EADesktop.exe"));
            candidates.Add(Path.Combine(root, "Electronic Arts", "EA Desktop", "EALauncher.exe"));
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
