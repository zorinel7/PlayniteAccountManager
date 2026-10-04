using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Steam account switching for Playnite game launches.
    ///
    /// Games without an assignment do not touch Steam at all.
    /// Games with a Steam assignment temporarily select that saved Steam account,
    /// then Playnite continues its normal launch pipeline. When the game ends and
    /// "Wyloguj po zakończeniu gry" is enabled, Steam is closed and the previous
    /// account-selection state is restored so the next normal Steam start returns
    /// to the user's main account.
    ///
    /// Newer Steam clients use "AutoLogin" / "AllowAutoLogin" in loginusers.vdf.
    /// Older clients may also expose "MostRecent", but it is not used as the runtime
    /// success signal because some client builds leave it stale during account switching.
    /// </summary>
    internal sealed class SteamAdapter
    {
        private static readonly string[] ProcessNames =
        {
            "steam",
            "steamwebhelper"
        };

        private readonly Action<string> log;
        private readonly SteamUiAutomation uiAutomation;
        private SteamSession activeSession;

        public SteamAdapter(Action<string> log)
        {
            this.log = log ?? (_ => { });
            uiAutomation = new SteamUiAutomation(this.log);
        }

        public bool PrepareAndLogin(AccountRecord account, string password, out string error)
        {
            return PrepareForGame(account, out error, true);
        }

        public bool PrepareForGame(AccountRecord account, out string error, bool leaveSelectedAccountRunning = false)
        {
            error = null;

            if (account == null || account.Id == Guid.Empty)
            {
                error = "Nie wybrano konta Steam.";
                return false;
            }

            if (account.Launcher != LauncherType.Steam)
            {
                error = "Wybrane konto nie jest kontem Steam.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(account.UserName))
            {
                error = "Brak nazwy konta Steam.";
                return false;
            }

            string steamPath = FindSteamExecutable();
            if (string.IsNullOrWhiteSpace(steamPath) || !File.Exists(steamPath))
            {
                error = "Nie znaleziono Steam.exe na tym komputerze.";
                return false;
            }

            try
            {
                bool steamWasRunning = IsSteamRunning();
                string loginUsersPath = Path.Combine(Path.GetDirectoryName(steamPath), "config", "loginusers.vdf");

                if (!File.Exists(loginUsersPath))
                {
                    error = "Nie znaleziono Steam\\config\\loginusers.vdf. Zaloguj wcześniej na dane konto Steam i pozwól Steam zapamiętać konto.";
                    return false;
                }

                SteamNativeState runningSnapshot = null;
                if (!TryCaptureNativeState(loginUsersPath, out runningSnapshot, out error))
                    return false;

                SteamAccountBlock runningTarget = runningSnapshot.Users.FirstOrDefault(x =>
                    AccountNamesEqual(x.AccountName, account.UserName));

                if (runningTarget == null)
                {
                    error = "Konto Steam „" + account.UserName + "” nie jest zapisane w loginusers.vdf. Najpierw zaloguj się na nie ręcznie w Steam i włącz zapamiętywanie konta.";
                    return false;
                }

                SteamNativeState snapshot = runningSnapshot;
                SteamAccountBlock target = null;
                if (steamWasRunning)
                {
                    log("Steam: zamykam klienta przed przełączeniem konta.");
                    StopSteamProcesses();

                    if (!TryCaptureNativeState(loginUsersPath, out snapshot, out error))
                        return false;

                    target = snapshot.Users.FirstOrDefault(x => AccountNamesEqual(x.AccountName, account.UserName));
                    if (target == null)
                    {
                        error = "Po zamknięciu Steam nie odnaleziono docelowego konta w loginusers.vdf.";
                        return false;
                    }
                }
                else
                {
                    target = runningTarget;
                }

                string nativeAccount = snapshot.RegistryAutoLoginUser;
                if (!steamWasRunning && AccountNamesEqual(nativeAccount, account.UserName))
                {
                    activeSession = new SteamSession
                    {
                        AccountId = account.Id,
                        Authenticated = true,
                        ChangedNativeState = false,
                        LeaveSelectedAccountRunning = leaveSelectedAccountRunning
                    };
                    log("Steam: zapisane konto docelowe jest już ustawione jako auto-login. Nie zmieniam konfiguracji.");
                    return true;
                }

                if (!target.HasSwitchableLoginFlag)
                {
                    error = "Nie udało się rozpoznać ustawień automatycznego logowania dla konta Steam „" + account.UserName + "”.";
                    return false;
                }

                log("Steam: przełączam zapisany profil na konto „" + account.UserName + "”.");
                if (!TryWriteTargetAccount(loginUsersPath, target.AccountName, out error))
                    return false;

                if (!TryWriteRegistryAutoLoginUser(account.UserName, out error))
                {
                    string ignoredRestoreError;
                    TryRestoreNativeState(loginUsersPath, snapshot, out ignoredRestoreError);
                    return false;
                }

                Process launcher = Process.Start(new ProcessStartInfo
                {
                    FileName = steamPath,
                    WorkingDirectory = Path.GetDirectoryName(steamPath),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                });

                if (launcher == null)
                {
                    string ignoredRestoreError;
                    TryRestoreNativeState(loginUsersPath, snapshot, out ignoredRestoreError);
                    error = "Windows nie uruchomił Steam.";
                    return false;
                }

                launcher.Dispose();

                if (!WaitUntilTargetAccountActive(loginUsersPath, target.AccountName, target.PersonaName, 60, out error))
                {
                    log("Steam: nie udało się potwierdzić aktywnego konta. Nie zamykam Steam automatycznie, aby nie przerywać ewentualnie poprawnie przełączonego profilu.");
                    return false;
                }

                activeSession = new SteamSession
                {
                    AccountId = account.Id,
                    Authenticated = true,
                    ChangedNativeState = true,
                    LeaveSelectedAccountRunning = leaveSelectedAccountRunning,
                    SteamPath = steamPath,
                    LoginUsersPath = loginUsersPath,
                    NativeState = snapshot
                };

                log("Steam: konto „" + account.Name + "” jest aktywne. Playnite kontynuuje normalne uruchomienie gry.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Błąd przełączania konta Steam: " + ex.Message;
                log(error);
                return false;
            }
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(text) || values == null)
                return false;

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private bool WaitUntilTargetAccountActive(string loginUsersPath, string targetAccountName, string targetPersonaName, int timeoutSeconds, out string error)
        {
            error = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, timeoutSeconds));
            DateTime shellSeenAt = DateTime.MinValue;
            bool steamSeen = false;
            bool targetAutoLoginSeen = false;

            log("Steam: czekam na uruchomienie klienta i aktywację wybranego profilu...");

            while (DateTime.UtcNow < deadline)
            {
                if (IsSteamRunning())
                {
                    steamSeen = true;
                    if (shellSeenAt == DateTime.MinValue)
                        shellSeenAt = DateTime.UtcNow;
                }

                try
                {
                    string autoLoginUser = ReadRegistryAutoLoginUser();
                    targetAutoLoginSeen = AccountNamesEqual(autoLoginUser, targetAccountName);
                }
                catch { }

                IntPtr hwnd = uiAutomation.GetMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    if (uiAutomation.IsAccountSelectionOrLoginVisible(out string uiState))
                    {
                    }
                    else if (uiAutomation.IsAuthenticatedWindow())
                    {
                        string visibleText = uiAutomation.GetVisibleText(hwnd);
                        bool targetIdentityVisible = ContainsAny(visibleText, targetAccountName, targetPersonaName);

                        if (targetIdentityVisible)
                        {
                            log("Steam: aktywne konto „" + targetAccountName + "” potwierdzone przez interfejs Steam.");
                            return true;
                        }

                        if (steamSeen && targetAutoLoginSeen && shellSeenAt != DateTime.MinValue &&
                            (DateTime.UtcNow - shellSeenAt).TotalSeconds >= 5)
                        {
                            log("Steam: klient jest zalogowany, AutoLoginUser wskazuje „" + targetAccountName + "”. UI nie ujawnia nazwy konta, więc uznaję przełączenie za zakończone.");
                            return true;
                        }
                    }
                }
                else if (steamSeen && targetAutoLoginSeen && shellSeenAt != DateTime.MinValue &&
                         (DateTime.UtcNow - shellSeenAt).TotalSeconds >= 12)
                {
                    log("Steam: proces klienta działa, AutoLoginUser wskazuje „" + targetAccountName + "” i nie ma już ekranu logowania. Uznaję przełączenie za zakończone.");
                    return true;
                }

                Thread.Sleep(450);
            }

            error = "Steam przełączył konto, ale plugin nie mógł potwierdzić gotowego klienta w wymaganym czasie.";
            return false;
        }


        public void Logout()
        {
            SteamSession session = activeSession;
            try
            {
                if (session == null || !session.Authenticated || !session.ChangedNativeState)
                    return;

                if (!session.LeaveSelectedAccountRunning)
                {
                    log("Steam: zamykam klienta i przywracam poprzedni profil.");
                    StopSteamProcesses();
                }

                RestoreNativeState(session);
            }
            finally
            {
                activeSession = null;
            }
        }

        public bool HasActiveSession => activeSession != null && activeSession.Authenticated;

        public void ForgetActiveSessionWithoutRestore()
        {
            activeSession = null;
        }


        private void RestoreNativeState(SteamSession session)
        {
            if (session == null || session.NativeState == null || string.IsNullOrWhiteSpace(session.LoginUsersPath))
                return;

            if (!File.Exists(session.LoginUsersPath))
                return;

            string restoreError;
            if (TryRestoreNativeState(session.LoginUsersPath, session.NativeState, out restoreError))
            {
                string registryError;
                TryWriteRegistryAutoLoginUser(session.NativeState.RegistryAutoLoginUser, out registryError);
                log("Steam: przywrócono poprzednie ustawienia konta.");
            }
            else
            {
                log("Steam: nie udało się przywrócić poprzednich ustawień: " + restoreError);
            }
        }

        private bool TryCaptureNativeState(string loginUsersPath, out SteamNativeState state, out string error)
        {
            state = null;
            error = null;
            try
            {
                string text = File.ReadAllText(loginUsersPath);
                List<SteamAccountBlock> users = ParseUsers(text).ToList();
                if (users.Count == 0)
                {
                    error = "Nie udało się odczytać żadnych kont z Steam\\config\\loginusers.vdf.";
                    return false;
                }

                state = new SteamNativeState
                {
                    RegistryAutoLoginUser = ReadRegistryAutoLoginUser(),
                    Users = users
                };
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się odczytać ustawień Steam: " + ex.Message;
                return false;
            }
        }

        private bool TryWriteTargetAccount(string path, string targetAccountName, out string error)
        {
            error = null;
            try
            {
                string currentText = File.ReadAllText(path);
                List<SteamAccountBlock> currentUsers = ParseUsers(currentText).ToList();
                if (!currentUsers.Any(x => AccountNamesEqual(x.AccountName, targetAccountName)))
                {
                    error = "Po zamknięciu Steam nie odnaleziono docelowego konta w loginusers.vdf.";
                    return false;
                }

                bool hasAutoLoginField = currentUsers.Any(x => x.AutoLogin != null);
                bool hasAllowAutoLoginField = currentUsers.Any(x => x.AllowAutoLogin != null);
                bool hasNativeAutoLoginField = hasAutoLoginField || hasAllowAutoLoginField;

                List<VdfReplacement> replacements = new List<VdfReplacement>();
                foreach (SteamAccountBlock user in currentUsers)
                {
                    string body = user.Body;
                    bool isTarget = AccountNamesEqual(user.AccountName, targetAccountName);

                    if (hasAutoLoginField && HasField(body, "AutoLogin"))
                        body = SetField(body, "AutoLogin", isTarget ? "1" : "0");
                    if (hasAllowAutoLoginField && HasField(body, "AllowAutoLogin"))
                        body = SetField(body, "AllowAutoLogin", isTarget ? "1" : "0");

                    if (!hasNativeAutoLoginField && HasField(body, "MostRecent"))
                        body = SetField(body, "MostRecent", isTarget ? "1" : "0");

                    replacements.Add(new VdfReplacement(user.BodyStart, user.BodyLength, body));
                }

                string updated = ApplyReplacements(currentText, replacements);
                File.Copy(path, path + ".playnite-backup", true);
                File.WriteAllText(path, updated);
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się zmienić loginusers.vdf: " + ex.Message;
                return false;
            }
        }

        private bool TryRestoreNativeState(string path, SteamNativeState snapshot, out string error)
        {
            error = null;
            try
            {
                string currentText = File.ReadAllText(path);
                List<SteamAccountBlock> currentUsers = ParseUsers(currentText).ToList();
                List<VdfReplacement> replacements = new List<VdfReplacement>();

                foreach (SteamAccountBlock current in currentUsers)
                {
                    SteamAccountSnapshot original = snapshot.Users.FirstOrDefault(x => AccountNamesEqual(x.AccountName, current.AccountName));
                    if (original == null)
                        continue;

                    string body = current.Body;
                    body = RestoreField(body, "AutoLogin", original.AutoLogin);
                    body = RestoreField(body, "AllowAutoLogin", original.AllowAutoLogin);
                    body = RestoreField(body, "MostRecent", original.MostRecent);
                    replacements.Add(new VdfReplacement(current.BodyStart, current.BodyLength, body));
                }

                string restored = ApplyReplacements(currentText, replacements);
                File.WriteAllText(path, restored);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string RestoreField(string body, string key, string originalValue)
        {
            bool hadField = originalValue != null;
            bool hasNow = HasField(body, key);
            if (hadField)
                return hasNow ? SetField(body, key, originalValue) : AddField(body, key, originalValue);
            return hasNow ? RemoveField(body, key) : body;
        }

        private static string SetField(string body, string key, string value)
        {
            Regex regex = new Regex(
                "(?m)^(?<indent>\\s*)\"" + Regex.Escape(key) + "\"\\s+\"[^\"]*\"");
            return regex.Replace(body, m => m.Groups["indent"].Value + "\"" + key + "\"\t\t\"" + value + "\"", 1);
        }

        private static string AddField(string body, string key, string value)
        {
            string trimmed = body.TrimEnd();
            return trimmed + Environment.NewLine + "\t\t\"" + key + "\"\t\t\"" + value + "\"" + Environment.NewLine;
        }

        private static string RemoveField(string body, string key)
        {
            Regex regex = new Regex(
                "(?m)^\\s*\"" + Regex.Escape(key) + "\"\\s+\"[^\"]*\"\\s*\\r?\\n?");
            return regex.Replace(body, string.Empty, 1);
        }

        private static bool HasField(string body, string key)
        {
            return Regex.IsMatch(body, "(?m)^\\s*\"" + Regex.Escape(key) + "\"");
        }

        private static string ApplyReplacements(string text, IEnumerable<VdfReplacement> replacements)
        {
            string result = text;
            foreach (VdfReplacement replacement in replacements.OrderByDescending(x => x.Start))
                result = result.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Replacement);
            return result;
        }

        private static IEnumerable<SteamAccountBlock> ParseUsers(string text)
        {
            Regex userStart = new Regex("\"(?<id>\\d{10,})\"\\s*\\{");
            foreach (Match match in userStart.Matches(text))
            {
                int openBrace = text.IndexOf('{', match.Index, match.Length + 1);
                if (openBrace < 0)
                    continue;

                int closeBrace = FindMatchingBrace(text, openBrace);
                if (closeBrace < 0)
                    continue;

                string body = text.Substring(openBrace + 1, closeBrace - openBrace - 1);
                yield return new SteamAccountBlock
                {
                    SteamId64 = match.Groups["id"].Value,
                    Body = body,
                    BodyStart = openBrace + 1,
                    BodyLength = closeBrace - openBrace - 1,
                    AccountName = GetField(body, "AccountName"),
                    PersonaName = GetField(body, "PersonaName"),
                    AutoLogin = GetField(body, "AutoLogin"),
                    AllowAutoLogin = GetField(body, "AllowAutoLogin"),
                    MostRecent = GetField(body, "MostRecent")
                };
            }
        }

        private static int FindMatchingBrace(string text, int openBrace)
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = openBrace; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        private static string GetField(string body, string key)
        {
            Match match = Regex.Match(
                body,
                "(?m)^\\s*\"" + Regex.Escape(key) + "\"\\s+\"(?<value>[^\"]*)\"");
            return match.Success ? match.Groups["value"].Value : null;
        }

        private static bool AccountNamesEqual(string a, string b)
        {
            return !string.IsNullOrWhiteSpace(a) &&
                   !string.IsNullOrWhiteSpace(b) &&
                   string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadRegistryAutoLoginUser()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    return key == null ? null : key.GetValue("AutoLoginUser") as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryWriteRegistryAutoLoginUser(string accountName, out string error)
        {
            error = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                {
                    if (key == null)
                    {
                        error = "Nie znaleziono klucza HKCU\\Software\\Valve\\Steam.";
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(accountName))
                        key.DeleteValue("AutoLoginUser", false);
                    else
                        key.SetValue("AutoLoginUser", accountName, RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Nie udało się ustawić AutoLoginUser: " + ex.Message;
                return false;
            }
        }

        private static bool IsSteamRunning()
        {
            return SafeGetProcesses("steam").Any();
        }

        private void StopSteamProcesses()
        {
            try
            {
                string steamPath = FindSteamExecutable();
                if (!string.IsNullOrWhiteSpace(steamPath) && File.Exists(steamPath))
                {
                    try
                    {
                        Process shutdown = Process.Start(new ProcessStartInfo
                        {
                            FileName = steamPath,
                            Arguments = "-shutdown",
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                        if (shutdown != null)
                        {
                            shutdown.WaitForExit(6000);
                            shutdown.Dispose();
                        }
                    }
                    catch { }
                }
            }
            catch { }

            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline && SafeGetProcesses("steam").Any())
                Thread.Sleep(200);

            foreach (string name in ProcessNames)
            foreach (Process process in SafeGetProcesses(name))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.CloseMainWindow(); } catch { }
                        if (!process.WaitForExit(1200))
                            process.Kill();
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }

            Thread.Sleep(800);
        }

        private string FindSteamExecutable()
        {
            List<string> candidates = new List<string>();
            foreach (Process process in SafeGetProcesses("steam"))
            {
                try
                {
                    string path = process.MainModule.FileName;
                    if (!string.IsNullOrWhiteSpace(path)) candidates.Add(path);
                }
                catch { }
                finally { process.Dispose(); }
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        candidates.Add(key.GetValue("SteamExe") as string);
                        string steamPath = key.GetValue("SteamPath") as string;
                        if (!string.IsNullOrWhiteSpace(steamPath))
                            candidates.Add(Path.Combine(steamPath, "steam.exe"));
                    }
                }
            }
            catch { }

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
                    string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"')));
                    if (File.Exists(full) && string.Equals(Path.GetFileName(full), "steam.exe", StringComparison.OrdinalIgnoreCase))
                        return full;
                }
                catch { }
            }
            return null;
        }

        private static void AddInstallCandidates(List<string> results, string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return;
            results.Add(Path.Combine(basePath, "Steam", "steam.exe"));
            results.Add(Path.Combine(basePath, "Valve", "Steam", "steam.exe"));
        }

        private IEnumerable<string> FindFromUninstallRegistry()
        {
            List<string> results = new List<string>();
            const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey baseKey = null;
                RegistryKey uninstallKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    uninstallKey = baseKey.OpenSubKey(subKey);
                    if (uninstallKey == null) continue;
                    foreach (string child in uninstallKey.GetSubKeyNames())
                    using (RegistryKey app = uninstallKey.OpenSubKey(child))
                    {
                        if (app == null) continue;
                        string name = app.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name) || name.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        string location = app.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location)) results.Add(Path.Combine(location, "steam.exe"));
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

        private IEnumerable<string> FindFromStartMenuShortcuts()
        {
            List<string> results = new List<string>();
            string[] folders =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };

            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return results;
                object shell = Activator.CreateInstance(shellType);
                try
                {
                    foreach (string folder in folders.Where(Directory.Exists))
                    foreach (string link in SafeEnumerateLnk(folder))
                    {
                        if (link.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        object shortcut = null;
                        try
                        {
                            shortcut = shellType.InvokeMember(
                                "CreateShortcut",
                                System.Reflection.BindingFlags.InvokeMethod,
                                null, shell, new object[] { link });
                            object target = shortcut.GetType().InvokeMember(
                                "TargetPath",
                                System.Reflection.BindingFlags.GetProperty,
                                null, shortcut, null);
                            string path = target as string;
                            if (!string.IsNullOrWhiteSpace(path) && path.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase))
                                results.Add(path);
                        }
                        catch { }
                        finally
                        {
                            try
                            {
                                if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                            }
                            catch { }
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
                            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                    }
                    catch { }
                }
            }
            catch { }
            return results;
        }

        private static IEnumerable<string> SafeEnumerateLnk(string folder)
        {
            try { return Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { yield break; }
            foreach (Process process in processes)
                yield return process;
        }

        private sealed class SteamSession
        {
            public Guid AccountId { get; set; }
            public bool Authenticated { get; set; }
            public bool ChangedNativeState { get; set; }
            public bool LeaveSelectedAccountRunning { get; set; }
            public string SteamPath { get; set; }
            public string LoginUsersPath { get; set; }
            public SteamNativeState NativeState { get; set; }
        }

        private sealed class SteamNativeState
        {
            public string RegistryAutoLoginUser { get; set; }
            public List<SteamAccountBlock> Users { get; set; }
        }

        private class SteamAccountSnapshot
        {
            public string SteamId64 { get; set; }
            public string AccountName { get; set; }
            public string PersonaName { get; set; }
            public string AutoLogin { get; set; }
            public string AllowAutoLogin { get; set; }
            public string MostRecent { get; set; }
        }

        private sealed class SteamAccountBlock : SteamAccountSnapshot
        {
            public string Body { get; set; }
            public int BodyStart { get; set; }
            public int BodyLength { get; set; }
            public bool HasSwitchableLoginFlag => AutoLogin != null || AllowAutoLogin != null || MostRecent != null;
        }

        private sealed class VdfReplacement
        {
            public int Start { get; }
            public int Length { get; }
            public string Replacement { get; }

            public VdfReplacement(int start, int length, string replacement)
            {
                Start = start;
                Length = length;
                Replacement = replacement;
            }
        }
    }
}