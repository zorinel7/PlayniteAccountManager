using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace PlayniteAccountManager.Services
{
    internal enum XboxSwitchResult
    {
        Switched,
        AlreadySignedIn,
        AccountNotInWindows,
        Failed
    }

    /// <summary>
    /// Switches the Microsoft account of the Xbox app (Microsoft.GamingApp) through
    /// UI Automation. No password is typed: the target account must be added to
    /// Windows (Settings → Accounts → Email &amp; accounts), so the Windows account
    /// picker signs it in with one selection.
    ///
    /// Flow (stable AutomationIds of Xbox app 2610.x):
    ///   Xbox app        ProfileSettingsButton_MainButton  name ends with ": &lt;gamertag&gt;"
    ///                   profile menu → "Sign out"
    ///                   AccountSignInScreen-SignInButton
    ///   Windows picker  ListItem Account_N (first line = e-mail) → ContinueButton
    ///   Gaming Services confirmation (gamingservicestcui) shows the e-mail → its only button
    ///   Xbox app        profile button shows the new gamertag
    /// </summary>
    internal sealed class XboxAppUiAutomation
    {
        private const string AppUserModelId = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";
        private const string ProfileButtonId = "ProfileSettingsButton_MainButton";
        private const string SignInButtonId = "AccountSignInScreen-SignInButton";
        private const string PickerContinueId = "ContinueButton";
        private const string PickerAccountPrefix = "Account_";
        private const string GamingServicesUiProcess = "gamingservicestcui";

        // The profile menu items have no AutomationId; the sign-out item is found by
        // its (localized) caption.
        private static readonly string[] SignOutCaptions =
        {
            "Wyloguj się", "Sign out", "Abmelden", "Se déconnecter", "Cerrar sesión",
            "Esci", "Sair", "Terminar sessão", "Afmelden", "Odhlásit se", "Odhlásiť sa",
            "Kijelentkezés", "Выйти", "Вийти", "Oturumu kapat", "Logga ut", "Log ud",
            "Logg av", "Kirjaudu ulos", "サインアウト", "로그아웃", "注销", "登出"
        };

        private static readonly TimeSpan AppTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan PickerTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(40);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

        private readonly Action<string> log;

        public XboxAppUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        /// <summary>
        /// Signs the Xbox app in with <paramref name="email"/>. When the app is already
        /// signed in with <paramref name="knownGamertag"/> nothing is changed.
        /// </summary>
        public XboxSwitchResult SwitchAccount(string email, string knownGamertag, out string gamertag, out string error)
        {
            XboxSwitchResult result = XboxSwitchResult.Failed;
            string workerGamertag = null;
            string workerError = null;
            Exception workerException = null;

            var thread = new Thread(() =>
            {
                try { result = SwitchCore(email, knownGamertag, out workerGamertag, out workerError); }
                catch (Exception ex) { workerException = ex; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            gamertag = workerGamertag;
            error = workerException != null
                ? Loc.Get("ErrAutomation", "Xbox", workerException.Message)
                : workerError;
            return workerException != null ? XboxSwitchResult.Failed : result;
        }

        /// <summary>Starts the Xbox app (if needed) and waits for its main window.</summary>
        public bool EnsureAppOpen(out string error)
        {
            error = null;
            AutomationElement app = WaitForApp(AppTimeout);
            if (app != null)
                return true;

            error = Loc.Get("ErrStartFailed", "Xbox");
            return false;
        }

        private XboxSwitchResult SwitchCore(string email, string knownGamertag, out string gamertag, out string error)
        {
            gamertag = null;
            error = null;

            log("Xbox: czekam na aplikację Xbox.");
            Stopwatch timer = Stopwatch.StartNew();
            AutomationElement app = WaitForApp(AppTimeout);
            if (app == null)
            {
                error = Loc.Get("ErrStartFailed", "Xbox");
                return XboxSwitchResult.Failed;
            }

            log("Xbox: aplikacja znaleziona po " + timer.Elapsed.TotalSeconds.ToString("0.0") + " s.");

            AutomationElement profile = FindById(app, ProfileButtonId);
            if (profile != null)
            {
                string current = ParseGamertag(profile);
                if (!string.IsNullOrEmpty(knownGamertag) &&
                    string.Equals(current, knownGamertag, StringComparison.OrdinalIgnoreCase))
                {
                    gamertag = current;
                    log("Xbox: aplikacja jest już zalogowana na przypisane konto.");
                    return XboxSwitchResult.AlreadySignedIn;
                }

                log("Xbox: wylogowuję bieżące konto aplikacji Xbox.");
                if (!SignOut(app, profile, out error))
                    return XboxSwitchResult.Failed;
            }

            AutomationElement signIn = WaitFor(() => FindById(app, SignInButtonId), StepTimeout);
            if (signIn == null || !Invoke(signIn))
            {
                error = Loc.Get("ErrXboxNoSignIn");
                return XboxSwitchResult.Failed;
            }

            log("Xbox: czekam na okno wyboru konta Windows.");
            AutomationElement picker = WaitFor(FindAccountPicker, PickerTimeout);
            if (picker == null)
            {
                error = Loc.Get("ErrXboxNoPicker");
                return XboxSwitchResult.Failed;
            }

            AutomationElement tile = FindAccountTile(picker, email);
            if (tile == null)
            {
                error = Loc.Get("ErrXboxNotInWindows", email);
                return XboxSwitchResult.AccountNotInWindows;
            }

            if (!Select(tile))
            {
                error = Loc.Get("ErrXboxSelect");
                return XboxSwitchResult.Failed;
            }

            Thread.Sleep(500);
            AutomationElement next = FindById(picker, PickerContinueId);
            if (next == null || !Invoke(next))
            {
                error = Loc.Get("ErrXboxConfirm");
                return XboxSwitchResult.Failed;
            }

            log("Xbox: wybrano konto w oknie Windows, czekam na potwierdzenie.");
            if (!ConfirmGamingServices(app, email, out error))
                return XboxSwitchResult.Failed;

            AutomationElement signedIn = WaitFor(() => FindById(app, ProfileButtonId), SignInTimeout);
            if (signedIn == null)
            {
                error = Loc.Get("ErrNotConfirmed", "Xbox");
                return XboxSwitchResult.Failed;
            }

            gamertag = ParseGamertag(signedIn);
            log("Xbox: zalogowano konto aplikacji Xbox.");
            return XboxSwitchResult.Switched;
        }

        private bool SignOut(AutomationElement app, AutomationElement profile, out string error)
        {
            error = null;
            IntPtr hwnd = new IntPtr(app.Current.NativeWindowHandle);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                EAAppUiAutomation.EnsureWindowForeground(hwnd);
                Thread.Sleep(300);

                // The profile button exposes no Invoke pattern; the menu reliably
                // opens with a click.
                ClickCenter(profile);

                AutomationElement item = WaitFor(() => FindSignOutItem(app), TimeSpan.FromSeconds(4));
                if (item != null)
                {
                    if (!Invoke(item))
                        ClickCenter(item);

                    if (WaitFor(() => FindById(app, SignInButtonId), StepTimeout) != null)
                        return true;

                    error = Loc.Get("ErrXboxNoSignInScreen");
                    return false;
                }

                NativeKeyboardInput.Key(0x1B, log); // Escape: close a half-open menu
                Thread.Sleep(500);
            }

            error = Loc.Get("ErrXboxNoSignOut");
            return false;
        }

        private bool ConfirmGamingServices(AutomationElement app, string email, out string error)
        {
            error = null;
            DateTime deadline = DateTime.UtcNow + SignInTimeout;

            while (DateTime.UtcNow < deadline)
            {
                AutomationElement dialog = FindGamingServicesDialog();
                if (dialog != null)
                {
                    List<string> addresses = FindEmailTexts(dialog);
                    LogDialogOnce(dialog, addresses.Count);
                    if (addresses.Count > 0 &&
                        !addresses.Exists(a => string.Equals(a, email, StringComparison.OrdinalIgnoreCase)))
                    {
                        error = Loc.Get("ErrXboxWrongAccount");
                        return false;
                    }

                    AutomationElement button = FindPrimaryPageButton(dialog);
                    if (addresses.Count > 0 && button != null)
                    {
                        Invoke(button);
                        log("Xbox: potwierdzono konto w oknie Gaming Services.");
                        return true;
                    }
                }
                else if (FindById(app, ProfileButtonId) != null)
                {
                    // Some sign-ins finish without the confirmation dialog.
                    return true;
                }

                Thread.Sleep(PollInterval);
            }

            error = Loc.Get("ErrNotConfirmed", "Xbox");
            return false;
        }

        private string lastDialogDescription;

        /// <summary>Logs the structure (control types only, no texts) when it changes.</summary>
        private void LogDialogOnce(AutomationElement dialog, int emailCount)
        {
            try
            {
                var types = new List<string>();
                foreach (AutomationElement e in dialog.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    string type = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty);
                    if (type == "Button" || type == "Hyperlink" || type == "Edit")
                        types.Add(type + "[" + e.Current.Name + " w=" + (int)e.Current.BoundingRectangle.Width + "]" +
                                  (e.Current.IsOffscreen ? "(off)" : string.Empty));
                }

                string description = "okno Gaming Services: adresy=" + emailCount + ", elementy=" + string.Join(",", types);
                if (description != lastDialogDescription)
                {
                    lastDialogDescription = description;
                    log("Xbox: " + description);
                }
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- finders

        private static AutomationElement WaitForApp(TimeSpan timeout)
        {
            AutomationElement app = FindApp();
            if (app != null)
                return app;

            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "shell:AppsFolder\\" + AppUserModelId,
                    UseShellExecute = true
                }))
                {
                }
            }
            catch
            {
                return null;
            }

            return WaitFor(FindApp, timeout);
        }

        /// <summary>The Xbox app frame: the window that hosts its profile or sign-in button.</summary>
        private static AutomationElement FindApp()
        {
            foreach (AutomationElement window in TopLevelWindows())
            {
                try
                {
                    if (window.Current.ClassName != "ApplicationFrameWindow")
                        continue;

                    if (FindById(window, ProfileButtonId) != null || FindById(window, SignInButtonId) != null)
                        return window;
                }
                catch
                {
                }
            }

            return null;
        }

        private static AutomationElement FindAccountPicker()
        {
            foreach (AutomationElement window in TopLevelWindows())
            {
                try
                {
                    if (window.Current.ClassName == "ApplicationFrameWindow" &&
                        FindById(window, PickerContinueId) != null &&
                        FindAccountTiles(window).Count > 0)
                        return window;
                }
                catch
                {
                }
            }

            return null;
        }

        private static AutomationElement FindGamingServicesDialog()
        {
            var ids = new HashSet<int>();
            foreach (Process process in SafeGetProcesses(GamingServicesUiProcess))
            {
                try { ids.Add(process.Id); }
                catch { }
                finally { process.Dispose(); }
            }

            if (ids.Count == 0)
                return null;

            foreach (AutomationElement window in TopLevelWindows())
            {
                try
                {
                    if (ids.Contains(window.Current.ProcessId) && !window.Current.IsOffscreen)
                        return window;
                }
                catch
                {
                }
            }

            return null;
        }

        /// <summary>
        /// The confirmation page is a WebView2 document inside a native frame that has
        /// its own (close) button. The primary action ("Let's play") is the widest
        /// visible button inside the web document.
        /// </summary>
        private static AutomationElement FindPrimaryPageButton(AutomationElement dialog)
        {
            AutomationElement document = dialog.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            if (document == null)
                return null;

            AutomationElement best = null;
            double bestWidth = 0;
            foreach (AutomationElement button in document.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                try
                {
                    if (button.Current.IsOffscreen || !button.Current.IsEnabled)
                        continue;

                    double width = button.Current.BoundingRectangle.Width;
                    if (width > bestWidth)
                    {
                        best = button;
                        bestWidth = width;
                    }
                }
                catch
                {
                }
            }

            return best;
        }

        private static List<AutomationElement> FindAccountTiles(AutomationElement picker)
        {
            var tiles = new List<AutomationElement>();
            AutomationElementCollection items = picker.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

            foreach (AutomationElement item in items)
            {
                try
                {
                    if (item.Current.AutomationId.StartsWith(PickerAccountPrefix, StringComparison.Ordinal))
                        tiles.Add(item);
                }
                catch
                {
                }
            }

            return tiles;
        }

        private static AutomationElement FindAccountTile(AutomationElement picker, string email)
        {
            foreach (AutomationElement tile in FindAccountTiles(picker))
            {
                try
                {
                    // Name: "<e-mail>\nMicrosoft account"
                    string first = tile.Current.Name.Split('\n')[0].Trim();
                    if (string.Equals(first, email.Trim(), StringComparison.OrdinalIgnoreCase))
                        return tile;
                }
                catch
                {
                }
            }

            return null;
        }

        private static AutomationElement FindSignOutItem(AutomationElement app)
        {
            AutomationElementCollection items = app.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

            foreach (AutomationElement item in items)
            {
                try
                {
                    if (item.Current.IsOffscreen)
                        continue;

                    string name = item.Current.Name;
                    foreach (string caption in SignOutCaptions)
                    {
                        if (string.Equals(name, caption, StringComparison.OrdinalIgnoreCase))
                            return item;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static List<string> FindEmailTexts(AutomationElement root)
        {
            var result = new List<string>();
            AutomationElementCollection texts = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

            foreach (AutomationElement text in texts)
            {
                try
                {
                    string name = text.Current.Name.Trim();
                    if (name.IndexOf('@') > 0 && name.IndexOf(' ') < 0)
                        result.Add(name);
                }
                catch
                {
                }
            }

            return result;
        }

        private static AutomationElement FindById(AutomationElement root, string automationId)
        {
            try
            {
                return root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>"Profile and settings — signed-in user: Gamertag" → "Gamertag".</summary>
        private static string ParseGamertag(AutomationElement profile)
        {
            try
            {
                string name = profile.Current.Name ?? string.Empty;
                int colon = name.LastIndexOf(':');
                return colon >= 0 ? name.Substring(colon + 1).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Visible top-level windows of other processes. Listed through Win32 instead of
        /// the UIA desktop: during game start Playnite's UI thread waits for this switch,
        /// so querying Playnite's own windows through UIA would block until timeout.
        /// </summary>
        private static IEnumerable<AutomationElement> TopLevelWindows()
        {
            uint ownProcessId = (uint)Process.GetCurrentProcess().Id;
            var handles = new List<IntPtr>();
            EnumWindows((hwnd, _) =>
            {
                uint processId;
                if (IsWindowVisible(hwnd) &&
                    GetWindowThreadProcessId(hwnd, out processId) != 0 &&
                    processId != ownProcessId)
                    handles.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr hwnd in handles)
            {
                AutomationElement window = null;
                try { window = AutomationElement.FromHandle(hwnd); }
                catch { }

                if (window != null)
                    yield return window;
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { yield break; }

            foreach (Process process in processes)
                yield return process;
        }

        private static T WaitFor<T>(Func<T> probe, TimeSpan timeout) where T : class
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                T value = null;
                try { value = probe(); } catch { }
                if (value != null)
                    return value;

                Thread.Sleep(PollInterval);
            }

            return null;
        }

        // ---------------------------------------------------------------- actions

        private static bool Invoke(AutomationElement element)
        {
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool Select(AutomationElement element)
        {
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                {
                    ((SelectionItemPattern)pattern).Select();
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>Clicks the centre of an element and puts the mouse cursor back.</summary>
        private static void ClickCenter(AutomationElement element)
        {
            System.Windows.Rect rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty || double.IsInfinity(rect.X))
                return;

            POINT previous;
            GetCursorPos(out previous);

            SetCursorPos((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
            Thread.Sleep(80);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);

            SetCursorPos(previous.X, previous.Y);
        }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    }
}
