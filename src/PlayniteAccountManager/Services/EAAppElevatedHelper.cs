using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PlayniteAccountManager.Services
{
    internal sealed class EAAppElevatedHelper
    {
        private const string TaskName = "Playnite Account Manager - EA Session Helper";

        private readonly string pluginUserDataPath;
        private readonly Action<string> log;
        private readonly string requestFile;
        private readonly string helperExe;

        public EAAppElevatedHelper(string pluginUserDataPath, Action<string> log)
        {
            this.pluginUserDataPath = pluginUserDataPath;
            this.log = log ?? (_ => { });
            requestFile = Path.Combine(pluginUserDataPath, "EAAccountSessions", "elevated.request");
            helperExe = Path.Combine(
                Path.GetDirectoryName(typeof(EAAppElevatedHelper).Assembly.Location) ?? string.Empty,
                "PlayniteAccountManager.EAHelper.exe");
        }

        public bool IsInstalled()
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Query /TN "" + TaskName + """,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (process == null)
                        return false;

                    process.WaitForExit(2500);
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public bool EnsureInstalledInteractive(out string error)
        {
            error = null;

            if (IsInstalled())
                return true;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(requestFile));

                string script = Path.Combine(
                    Path.GetTempPath(),
                    "PlayniteAccountManager-EA-Task-" + Guid.NewGuid().ToString("N") + ".ps1");

                string currentUser = WindowsIdentity.GetCurrent().Name;

                File.WriteAllText(script, @"
$ErrorActionPreference = 'Stop'
$taskName = '" + EscapePowerShell(TaskName) + @"'
$exe = '" + EscapePowerShell(helperExe) + @"'
$request = '" + EscapePowerShell(requestFile) + @"'
$action = New-ScheduledTaskAction -Execute $exe -Argument ('--run-task --request ""' + $request + '""')
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(10)
$principal = New-ScheduledTaskPrincipal -UserId '" + EscapePowerShell(currentUser) + @"' -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
", new UTF8Encoding(false));

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File "" + script + """,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null)
                    {
                        error = "Nie udało się skonfigurować pomocnika EA App.";
                        return false;
                    }

                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        error = "Konfiguracja pomocnika EA App zakończyła się kodem " + process.ExitCode + ".";
                        return false;
                    }
                }

                try { File.Delete(script); } catch { }

                if (!IsInstalled())
                {
                    error = "Pomocnik EA App nie został zarejestrowany.";
                    return false;
                }

                log("EA App: one-time elevated helper is ready; Playnite remains unelevated.");
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                error = "Konfiguracja pomocnika EA App została anulowana.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się skonfigurować pomocnika EA App: " + ex.Message;
                return false;
            }
        }

        public bool RunClear(out string error)
        {
            error = null;

            if (!EnsureInstalledInteractive(out error))
                return false;

            string cacheRoot = Path.Combine(pluginUserDataPath, "EAAccountSessions");
            Directory.CreateDirectory(cacheRoot);

            string resultFile = requestFile + ".result";

            try
            {
                if (File.Exists(resultFile))
                    File.Delete(resultFile);

                string request =
                    "clear" + Environment.NewLine +
                    Guid.Empty.ToString("D") + Environment.NewLine +
                    cacheRoot;

                string temp = requestFile + ".tmp";
                File.WriteAllText(temp, request, new UTF8Encoding(false));

                if (File.Exists(requestFile))
                    File.Delete(requestFile);

                File.Move(temp, requestFile);

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/Run /TN "" + TaskName + """,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (process == null)
                    {
                        error = "Nie udało się uruchomić pomocnika EA App.";
                        return false;
                    }

                    process.WaitForExit(3000);

                    if (process.ExitCode != 0)
                    {
                        error = "Nie udało się uruchomić pomocnika EA App. Kod: " + process.ExitCode;
                        return false;
                    }
                }

                DateTime deadline = DateTime.UtcNow.AddSeconds(20);

                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(resultFile))
                    {
                        string result = File.ReadAllText(resultFile, Encoding.UTF8);
                        try { File.Delete(resultFile); } catch { }

                        if (result.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                        {
                            log("EA App: active session was cleared.");
                            return true;
                        }

                        error = result.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                            ? result.Substring(5).Trim()
                            : result;

                        if (string.IsNullOrWhiteSpace(error))
                            error = "EA App helper returned an unknown error.";

                        return false;
                    }

                    Thread.Sleep(100);
                }

                error = "EA App helper did not return a result within 20 seconds.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się wyczyścić sesji EA App: " + ex.Message;
                return false;
            }
        }

        private static string EscapePowerShell(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }
    }
}
