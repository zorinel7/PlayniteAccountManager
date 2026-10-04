using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PlayniteAccountManager.Services
{
    internal static class EAAppPermissionSetup
    {
        public static bool RunInteractive(Action<string> log, out string error)
        {
            error = null;

            string scriptPath = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAccountManager-EA-Setup-" + Guid.NewGuid().ToString("N") + ".ps1");

            try
            {
                File.WriteAllText(scriptPath, BuildScript(), new UTF8Encoding(false));

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                }))
                {
                    if (process == null)
                    {
                        error = "Nie udało się uruchomić konfiguratora uprawnień EA App.";
                        return false;
                    }

                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        error = "Konfiguracja EA App zakończyła się kodem " + process.ExitCode + ".";
                        return false;
                    }
                }

                log("EA App: jednorazowa konfiguracja dostępu bez administratora zakończona pomyślnie.");
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                error = "Konfiguracja została anulowana w oknie UAC.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się skonfigurować EA App bez administratora: " + ex.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(scriptPath))
                        File.Delete(scriptPath);
                }
                catch { }
            }
        }

        private static string BuildScript()
        {
            return @"
$ErrorActionPreference = 'Stop'

$eaProgramData = Join-Path $env:ProgramData 'EA Desktop'

# Give the currently logged-in Windows user Modify access to EA Desktop data.
# This is a one-time ACL change; Playnite itself remains non-elevated afterward.
if (Test-Path $eaProgramData) {
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $eaProgramData /grant ($sid + ':(OI)(CI)M') /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw ('Nie udało się nadać dostępu do ' + $eaProgramData + '. Kod icacls: ' + $LASTEXITCODE)
    }
}

# Allow only the current user to query/start/stop the EA background service.
# Existing service permissions are preserved; we append one ACE.
$service = 'EABackgroundService'
$sdLines = & sc.exe sdshow $service 2>$null
$sd = ($sdLines | Where-Object { $_ -match 'D:' } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($sd)) {
    throw 'Nie udało się odczytać zabezpieczeń usługi EABackgroundService.'
}

$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$ace = '(A;;CCLCRPWP;;;' + $sid + ')'

if ($sd -notmatch [regex]::Escape($ace)) {
    if ($sd -match 'S:') {
        $newSd = $sd -replace 'S:', ($ace + 'S:')
    }
    else {
        $newSd = $sd + $ace
    }

    & sc.exe sdset $service $newSd | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Nie udało się nadać użytkownikowi prawa start/stop do EABackgroundService.'
    }
}

# Verify folder write access under the elevated account for the target user's SID.
# The ACL itself is the actual persistent configuration.
if (Test-Path $eaProgramData) {
    $icaclsCheck = & icacls.exe $eaProgramData
    if ($icaclsCheck -notmatch [regex]::Escape($sid)) {
        throw 'Weryfikacja ACL dla EA Desktop nie powiodła się.'
    }
}

Write-Host ''
Write-Host 'Konfiguracja EA App zakończona pomyślnie.'
Write-Host 'Playnite może teraz przełączać konta EA bez uruchamiania całego programu jako administrator.'
exit 0
";
        }
    }
}
