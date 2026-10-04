using AutomationCondition = System.Windows.Automation.Condition;
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using PlayniteAccountManager.Services;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// EA App automation based on its current Qt/Cef accessibility tree.
    /// The hamburger button is not exposed as a named UIA control in the
    /// supplied diagnostic, so the automation uses UIA when available and
    /// falls back to a native click at the stable, window-relative hamburger
    /// location. The logout command is then located through UIA, with a
    /// DPI-aware native fallback matching the visible EA menu.
    /// </summary>
    internal sealed class EAAppUiAutomation
    {
        private readonly Action<string> log;
        private const int SW_RESTORE = 9;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const ushort VK_DOWN = 0x0028;

        public EAAppUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public static IntPtr FindMainWindowHandlePublic() => FindMainWindowHandle();

        public bool PrepareAndLogin(string username, string password, int timeoutSeconds, out string error)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return PrepareAndLoginCore(username, password, timeoutSeconds, out error);

            bool result = false; string workerError = null; Exception workerException = null;
            var thread = new Thread(() =>
            {
                try { result = PrepareAndLoginCore(username, password, timeoutSeconds, out workerError); }
                catch (Exception ex) { workerException = ex; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (workerException != null) { error = "Błąd wątku automatyzacji EA App: " + workerException.Message; return false; }
            error = workerError;
            return result;
        }

        private bool PrepareAndLoginCore(string username, string password, int timeoutSeconds, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanych dla konta EA App.";
                return false;
            }

            IntPtr hwnd = WaitForMainWindow(timeoutSeconds);
            if (hwnd == IntPtr.Zero)
            {
                error = "Nie znaleziono okna EA App w czasie oczekiwania.";
                return false;
            }

            EnsureWindowForeground(hwnd);

            bool authenticated;
            if (!WaitForEAState(hwnd, 6, out authenticated))
            {
                // When EA is already logged out, some builds expose too little
                // accessibility metadata to identify the login page. In that
                // case we deliberately treat a non-authenticated window as the
                // login surface and let the native login fallback take over.
                authenticated = IsAuthenticatedScreen(hwnd);
                if (authenticated)
                {
                    error = "EA App pozostaje zalogowana, ale jej stan UI nie może zostać jednoznacznie rozpoznany.";
                    return false;
                }

                log("EA App: stan UI nie został jednoznacznie rozpoznany, ale brak oznak aktywnej sesji. Kontynuuję jako ekran logowania.");
            }

            if (authenticated)
            {
                log("EA App: wykryto aktywną sesję. Rozpoczynam pełne wylogowanie obecnego konta.");
                if (!OpenHamburgerAndLogout(hwnd, 15, out error))
                    return false;
            }
            else
            {
                log("EA App: wykryto ekran logowania. Pomijam wylogowanie i przechodzę do logowania.");
            }

            hwnd = WaitForMainWindow(20);
            if (hwnd == IntPtr.Zero)
            {
                error = "Nie znaleziono okna logowania EA App.";
                return false;
            }

            if (!WaitForLoginScreen(hwnd, 10))
                log("EA App: ekran logowania nie został jednoznacznie wykryty przez UIA. Kontynuuję z natywnym fallbackiem.");

            if (!PerformLogin(hwnd, username, password, 12, out error))
                return false;

            if (!WaitForAuthenticated(hwnd, 15))
            {
                error = "EA App nie potwierdziła zalogowania po wysłaniu danych.";
                return false;
            }

            return true;
        }

        public bool Logout(out string error)
        {
            error = null;
            IntPtr hwnd = FindMainWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                error = "Nie znaleziono okna EA App.";
                return false;
            }

            EnsureWindowForeground(hwnd);
            return OpenHamburgerAndLogout(hwnd, 12, out error);
        }

        private bool OpenHamburgerAndLogout(IntPtr hwnd, int timeoutSeconds, out string error)
        {
            error = null;

            if (!EnsureWindowForeground(hwnd))
                log("EA App: nie uzyskano pełnej pewności aktywnego okna, ale kontynuuję.");

            // EA's hamburger is not exposed reliably through UIA. Use the
            // actual on-screen click first; UIA is only a fallback.
            log("EA: otwieram menu przez natywne kliknięcie hamburgera.");
            if (!NativeClickRelative(hwnd, 0.010, 0.017, "hamburger menu"))
            {
                AutomationElement button = FindClickableAtRelativeRegion(hwnd, 0.0, 0.0, 0.055, 0.055, true);
                if (button == null || !Invoke(button))
                {
                    error = "Nie udało się otworzyć menu EA App.";
                    return false;
                }
            }

            Thread.Sleep(280);

            // The supplied screenshot shows the logout row at roughly
            // y=255 px. Start with the center of the text row, then try two
            // nearby points only when the first click did not change state.
            double[,] logoutPoints =
            {
                { 56.0, 255.0 },
                { 44.0, 255.0 },
                { 72.0, 255.0 }
            };

            for (int i = 0; i < logoutPoints.GetLength(0); i++)
            {
                if (NativeClickMenuLogoutAt(hwnd, logoutPoints[i, 0], logoutPoints[i, 1]))
                {
                    log("EA native: kliknięto „Wyloguj się” (" +
                        logoutPoints[i, 0].ToString("0") + "," +
                        logoutPoints[i, 1].ToString("0") + ").");

                    if (WaitForLoginScreen(hwnd, 2))
                    {
                        log("EA: wylogowanie potwierdzone ekranem logowania.");
                        return true;
                    }

                    Thread.Sleep(100);
                }
            }

            // UIA fallback: some EA builds expose the menu row only after it
            // has been rendered for a moment.
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                AutomationElement logout = FindVisibleNamedInvokable(hwnd, new[] { "Wyloguj się", "Wyloguj", "Sign out", "Log out" });
                if (logout == null)
                    logout = FindTextInPopup(hwnd, new[] { "Wyloguj się", "Wyloguj", "Sign out", "Log out" });

                if (logout != null)
                {
                    log("EA UIA: znaleziono pozycję wylogowania: „" + SafeName(logout) + "”.");
                    if (Invoke(logout) && WaitForLoginScreen(hwnd, 2))
                        return true;
                    break;
                }

                Thread.Sleep(100);
            }

            // Last fallback: if the menu captured keyboard focus, navigate to
            // the logout row and press Enter.
            if (TryKeyboardMenuLogout())
            {
                log("EA native: próba wylogowania przez nawigację klawiaturą.");
                if (WaitForLoginScreen(hwnd, 2))
                    return true;
            }

            error = "Po otwarciu menu EA App nie udało się wykonać pozycji „Wyloguj się”.";
            return false;
        }

        private bool PerformLogin(IntPtr hwnd, string username, string password, int timeoutSeconds, out string error)
        {
            error = null;
            // UIA is given only a short window. Current EA builds can expose
            // the login page as Qt/Cef content without stable edit metadata, so
            // waiting tens of seconds here only makes the automation look dead.
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Min(4, Math.Max(2, timeoutSeconds)));

            AutomationElement emailEdit = null;
            while (DateTime.UtcNow < deadline && emailEdit == null)
            {
                emailEdit = FindLoginEdit(hwnd, 0);
                if (emailEdit == null)
                    Thread.Sleep(200);
            }

            if (emailEdit != null)
            {
                log("EA UIA: znaleziono pole e-mail.");
                if (!FocusElement(emailEdit))
                    emailEdit = null;
            }

            if (emailEdit == null)
            {
                log("EA UIA: pole e-mail nie jest dostępne przez UIA. Używam natywnego kliknięcia.");
                if (!NativeClickRelative(hwnd, 0.50, 0.49, "pole e-mail / EA ID"))
                {
                    error = "Nie udało się ustawić pola e-mail / EA ID.";
                    return false;
                }
            }
            else
            {
                log("EA UIA: ustawiam fokus na pole e-mail przez SetFocus().");
            }

            if (!NativeKeyboardInput.TypeText(username, hwnd, log))
            {
                error = "Nie udało się wpisać e-maila / EA ID.";
                return false;
            }

            Thread.Sleep(250);

            // IMPORTANT: do this after the e-mail has been entered. On some
            // EA Qt/Cef builds clicking the checkbox before the first edit can
            // steal focus and make the subsequent keyboard input unreliable.
            bool rememberMeChanged = EnsureRememberMeUnchecked(hwnd);
            if (!rememberMeChanged)
                log("EA: nie potwierdzono stanu „Nie wylogowuj mnie”; kontynuuję logowanie, aby nie blokować testu.");

            Thread.Sleep(180);

            AutomationElement next = FindButtonByNames(hwnd, "Dalej", "Continue", "Next");
            if (next != null && Invoke(next))
            {
                log("EA UIA: wykonano przycisk „Dalej”.");
            }
            else
            {
                log("EA UIA: przycisk „Dalej” nie jest dostępny. Używam natywnego kliknięcia / Enter.");
                if (!NativeClickRelative(hwnd, 0.50, 0.685, "przycisk „DALEJ”"))
                {
                    if (!NativeKeyboardInput.SendEnter(log))
                    {
                        error = "Nie udało się przejść do pola hasła w EA App.";
                        return false;
                    }
                }
            }

            Thread.Sleep(500);

            AutomationElement passwordEdit = null;
            DateTime passDeadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < passDeadline && passwordEdit == null)
            {
                passwordEdit = FindPasswordEdit(hwnd);
                if (passwordEdit == null)
                    Thread.Sleep(250);
            }

            if (passwordEdit != null)
            {
                log("EA UIA: znaleziono pole hasła.");
                if (!FocusElement(passwordEdit))
                    passwordEdit = null;
            }

            if (passwordEdit == null)
            {
                log("EA UIA: pole hasła nie jest dostępne przez UIA. Używam natywnego kliknięcia.");
                if (!NativeClickRelative(hwnd, 0.50, 0.47, "pole hasła"))
                {
                    error = "Nie udało się ustawić pola hasła EA App.";
                    return false;
                }
            }

            if (!NativeKeyboardInput.TypeText(password, hwnd, log))
            {
                error = "Nie udało się wpisać hasła EA App.";
                return false;
            }

            Thread.Sleep(300);

            AutomationElement loginButton = FindButtonByNames(hwnd, "Zaloguj", "Zaloguj się", "Log in", "Sign in");
            if (loginButton != null && Invoke(loginButton))
            {
                log("EA UIA: wykonano przycisk logowania.");
            }
            else
            {
                log("EA UIA: przycisk logowania nie jest dostępny. Używam natywnego kliknięcia / Enter.");
                if (!NativeClickRelative(hwnd, 0.50, 0.56, "przycisk „ZALOGUJ”"))
                {
                    if (!NativeKeyboardInput.SendEnter(log))
                    {
                        error = "Nie udało się zatwierdzić logowania EA App.";
                        return false;
                    }
                }
            }

            return true;
        }

        private static AutomationElement FindLoginEdit(IntPtr hwnd, int index)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            var edits = root.FindAll(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));

            var list = OrderByScreenPosition(edits);
            return index >= 0 && index < list.Count ? list[index] : null;
        }

        private static AutomationElement FindPasswordEdit(IntPtr hwnd)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            var edits = root.FindAll(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));

            foreach (AutomationElement e in edits)
            {
                try
                {
                    if (e.Current.IsPassword)
                        return e;
                }
                catch { }
            }

            var list = OrderByScreenPosition(edits);
            return list.Count > 0 ? list[0] : null;
        }

        private static List<AutomationElement> OrderByScreenPosition(AutomationElementCollection collection)
        {
            var list = new List<Tuple<double, AutomationElement>>();
            foreach (AutomationElement e in collection)
            {
                try
                {
                    var r = e.Current.BoundingRectangle;
                    if (r.Width > 0 && r.Height > 0)
                        list.Add(Tuple.Create(r.Top * 100000 + r.Left, e));
                }
                catch { }
            }

            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return list.Select(x => x.Item2).ToList();
        }

        private static AutomationElement FindButtonByNames(IntPtr hwnd, params string[] names)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            var all = root.FindAll(TreeScope.Descendants, AutomationCondition.TrueCondition);
            foreach (AutomationElement e in all)
            {
                try
                {
                    if (!e.Current.IsEnabled || e.Current.IsOffscreen)
                        continue;
                    if (e.Current.ControlType != ControlType.Button)
                        continue;

                    string name = e.Current.Name ?? string.Empty;
                    if (names.Any(n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                        return e;
                }
                catch { }
            }

            return null;
        }

        private static AutomationElement FindVisibleNamedInvokable(IntPtr hwnd, string[] needles)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return null;

                var all = root.FindAll(TreeScope.Descendants, AutomationCondition.TrueCondition);
                foreach (AutomationElement e in all)
                {
                    try
                    {
                        if (e.Current.IsOffscreen || !e.Current.IsEnabled)
                            continue;

                        string name = e.Current.Name ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name) ||
                            !needles.Any(n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;

                        try
                        {
                            e.GetCurrentPattern(InvokePattern.Pattern);
                            return e;
                        }
                        catch
                        {
                            AutomationElement parent = SafeParent(e);
                            for (int i = 0; parent != null && i < 6; i++, parent = SafeParent(parent))
                            {
                                try
                                {
                                    if (parent.Current.IsOffscreen || !parent.Current.IsEnabled)
                                        continue;
                                    parent.GetCurrentPattern(InvokePattern.Pattern);
                                    return parent;
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        private enum EAUiState
        {
            Unknown,
            Login,
            Authenticated
        }

        private static EAUiState DetectEAState(IntPtr hwnd)
        {
            try
            {
                string text = GetVisibleText(hwnd);
                if (string.IsNullOrWhiteSpace(text))
                    return EAUiState.Unknown;

                bool hasLibrary = text.IndexOf("Biblioteka", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasHome = text.IndexOf("Strona główna", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasInstalled = text.IndexOf("Zainstalowane gry", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasSearch = text.IndexOf("Szukaj", StringComparison.OrdinalIgnoreCase) >= 0;

                // Authenticated state is a strong positive signal. Check it
                // before the generic Edit-count fallback because the main EA
                // screen contains the search Edit control too.
                if ((hasLibrary && hasHome) || (hasLibrary && hasInstalled) || (hasHome && hasSearch))
                    return EAUiState.Authenticated;

                bool loginHint =
                    text.IndexOf("Zaloguj się na swoje konto EA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("TWOJ E-MAIL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("TWÓJ E-MAIL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("Podaj hasło", StringComparison.OrdinalIgnoreCase) >= 0;

                if (loginHint)
                    return EAUiState.Login;

                // Only perform the second UIA query when the text itself was
                // not enough to decide. This avoids scanning the whole tree
                // twice on every polling cycle.
                return CountVisibleEdits(hwnd) >= 1
                    ? EAUiState.Login
                    : EAUiState.Unknown;
            }
            catch
            {
                return EAUiState.Unknown;
            }
        }

        private static bool IsLoginScreen(IntPtr hwnd)
        {
            return DetectEAState(hwnd) == EAUiState.Login;
        }

        private static bool IsAuthenticatedScreen(IntPtr hwnd)
        {
            return DetectEAState(hwnd) == EAUiState.Authenticated;
        }

        private static bool WaitForEAState(IntPtr hwnd, int seconds, out bool authenticated)
        {
            authenticated = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));

            while (DateTime.UtcNow < deadline)
            {
                EAUiState state = DetectEAState(hwnd);
                if (state == EAUiState.Authenticated)
                {
                    authenticated = true;
                    return true;
                }

                if (state == EAUiState.Login)
                {
                    authenticated = false;
                    return true;
                }

                Thread.Sleep(120);
                hwnd = FindMainWindowHandle();
                if (hwnd == IntPtr.Zero)
                    continue;
            }

            EAUiState finalState = DetectEAState(hwnd);
            if (finalState == EAUiState.Authenticated)
            {
                authenticated = true;
                return true;
            }

            if (finalState == EAUiState.Login)
                return true;

            return false;
        }

        private static int CountVisibleEdits(IntPtr hwnd)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return 0;

                return root.FindAll(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false))).Count;
            }
            catch
            {
                return 0;
            }
        }

        private static bool WaitForLoginScreen(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
            while (DateTime.UtcNow < deadline)
            {
                if (IsLoginScreen(hwnd))
                    return true;

                Thread.Sleep(120);
                hwnd = FindMainWindowHandle();
                if (hwnd == IntPtr.Zero)
                    continue;
            }

            return IsLoginScreen(hwnd);
        }

        private static bool WaitForAuthenticated(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));
            while (DateTime.UtcNow < deadline)
            {
                EAUiState state = DetectEAState(hwnd);
                if (state == EAUiState.Authenticated)
                    return true;

                Thread.Sleep(140);
                hwnd = FindMainWindowHandle();
                if (hwnd == IntPtr.Zero)
                    continue;
            }

            return DetectEAState(hwnd) == EAUiState.Authenticated;
        }

        private static AutomationElement FindClickableAtRelativeRegion(
            IntPtr hwnd,
            double leftPct,
            double topPct,
            double rightPct,
            double bottomPct,
            bool preferButton)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect))
                return null;

            double left = rect.Left + (rect.Right - rect.Left) * leftPct;
            double top = rect.Top + (rect.Bottom - rect.Top) * topPct;
            double right = rect.Left + (rect.Right - rect.Left) * rightPct;
            double bottom = rect.Top + (rect.Bottom - rect.Top) * bottomPct;

            AutomationElement best = null;
            double bestScore = double.MaxValue;

            for (double y = top; y <= Math.Max(top, bottom); y += 6)
            for (double x = left; x <= Math.Max(left, right); x += 6)
            {
                try
                {
                    AutomationElement e = AutomationElement.FromPoint(new Point(x, y));
                    if (e == null || e.Current.IsOffscreen || !e.Current.IsEnabled)
                        continue;

                    bool invokable = false;
                    try
                    {
                        e.GetCurrentPattern(InvokePattern.Pattern);
                        invokable = true;
                    }
                    catch { }

                    if (!invokable)
                        continue;

                    if (preferButton &&
                        e.Current.ControlType != ControlType.Button &&
                        e.Current.ControlType != ControlType.Custom)
                        continue;

                    var r = e.Current.BoundingRectangle;
                    if (r.Width <= 0 || r.Height <= 0)
                        continue;

                    // Avoid invoking a huge root/container element returned
                    // by FromPoint instead of the actual clickable control.
                    if (r.Width > 240 || r.Height > 120)
                        continue;

                    double centerX = r.Left + r.Width / 2.0;
                    double centerY = r.Top + r.Height / 2.0;
                    double targetX = left + (right - left) / 2.0;
                    double targetY = top + (bottom - top) / 2.0;
                    double score = Math.Abs(centerX - targetX) + Math.Abs(centerY - targetY);

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = e;
                    }
                }
                catch { }
            }

            return best;
        }

        private static AutomationElement FindTextInPopup(IntPtr hwnd, string[] needles)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect))
                return null;

            double left = rect.Left;
            double right = rect.Left + Math.Min(420, rect.Right - rect.Left);
            double top = rect.Top + Math.Min(100, (rect.Bottom - rect.Top) * 0.12);
            double bottom = rect.Top + Math.Min(560, (rect.Bottom - rect.Top) * 0.70);

            var seen = new HashSet<int>();
            for (double y = top; y <= bottom; y += 8)
            for (double x = left + 5; x <= right; x += 8)
            {
                try
                {
                    AutomationElement e = AutomationElement.FromPoint(new Point(x, y));
                    if (e == null || !seen.Add(e.GetHashCode()))
                        continue;

                    AutomationElement current = e;
                    for (int level = 0; current != null && level < 8; level++)
                    {
                        string name = SafeName(current);
                        if (!string.IsNullOrWhiteSpace(name) &&
                            needles.Any(n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            try
                            {
                                if (!current.Current.IsEnabled || current.Current.IsOffscreen)
                                {
                                    current = SafeParent(current);
                                    continue;
                                }
                            }
                            catch { }

                            try
                            {
                                current.GetCurrentPattern(InvokePattern.Pattern);
                                return current;
                            }
                            catch { }

                            AutomationElement parent = SafeParent(current);
                            for (int p = 0; parent != null && p < 5; p++, parent = SafeParent(parent))
                            {
                                try
                                {
                                    if (!parent.Current.IsEnabled || parent.Current.IsOffscreen)
                                        continue;

                                    parent.GetCurrentPattern(InvokePattern.Pattern);
                                    return parent;
                                }
                                catch { }
                            }
                        }

                        current = SafeParent(current);
                    }
                }
                catch { }
            }

            return null;
        }

        private bool NativeClickRelative(IntPtr hwnd, double xPct, double yPct, string what)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect))
                return false;

            int x = rect.Left + (int)((rect.Right - rect.Left) * xPct);
            int y = rect.Top + (int)((rect.Bottom - rect.Top) * yPct);
            return NativeClickScreen(x, y, what);
        }

        private bool TryKeyboardMenuLogout()
        {
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    if (!NativeKeyboardInput.Key(VK_DOWN, log))
                        return false;
                    Thread.Sleep(70);
                }

                if (!NativeKeyboardInput.SendEnter(log))
                    return false;

                Thread.Sleep(300);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool EnsureRememberMeUnchecked(IntPtr hwnd)
        {
            // First try a real TogglePattern exposed by UIA.
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root != null)
                {
                    var all = root.FindAll(TreeScope.Descendants, AutomationCondition.TrueCondition);
                    foreach (AutomationElement e in all)
                    {
                        try
                        {
                            string name = e.Current.Name ?? string.Empty;
                            if (e.Current.IsOffscreen || !e.Current.IsEnabled)
                                continue;

                            if (name.IndexOf("Nie wylogowuj mnie", StringComparison.OrdinalIgnoreCase) < 0 &&
                                name.IndexOf("Keep me signed in", StringComparison.OrdinalIgnoreCase) < 0 &&
                                name.IndexOf("Stay signed in", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            var toggle = (TogglePattern)e.GetCurrentPattern(TogglePattern.Pattern);
                            if (toggle.Current.ToggleState == ToggleState.On)
                            {
                                toggle.Toggle();
                                log("EA UIA: wyłączono „Nie wylogowuj mnie”.");
                            }
                            else
                            {
                                log("EA UIA: „Nie wylogowuj mnie” było już wyłączone.");
                            }
                            return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            RECT rect;
            if (!GetWindowRect(hwnd, out rect))
                return false;

            // The supplied login screenshot is 516x862 and places the center
            // of the checkbox at roughly (67, 481), i.e. 13.1% x / 55.8% y.
            // GetWindowRect + SetCursorPos already use screen pixels, therefore
            // applying DPI scaling here would move the click away from the box.
            double width = rect.Right - rect.Left;
            double height = rect.Bottom - rect.Top;
            int sx = rect.Left + (int)Math.Round(width * 0.131);
            int sy = rect.Top + (int)Math.Round(height * 0.558);

            // Try point-based UIA around the real checkbox location.
            try
            {
                for (int dy = -8; dy <= 8; dy += 4)
                for (int dx = -8; dx <= 8; dx += 4)
                {
                    AutomationElement current = AutomationElement.FromPoint(new Point(sx + dx, sy + dy));
                    for (int i = 0; current != null && i < 5; i++, current = SafeParent(current))
                    {
                        try
                        {
                            var toggle = (TogglePattern)current.GetCurrentPattern(TogglePattern.Pattern);
                            if (toggle.Current.ToggleState == ToggleState.Off)
                            {
                                log("EA UIA: „Nie wylogowuj mnie” było już wyłączone.");
                                return true;
                            }

                            if (toggle.Current.ToggleState == ToggleState.On)
                            {
                                NativeClickScreen(sx, sy, "checkbox „Nie wylogowuj mnie”");
                                Thread.Sleep(220);

                                if (!LooksLikeCheckedEABox(sx, sy))
                                {
                                    log("EA native: wyłączono „Nie wylogowuj mnie” (UIA + kliknięcie).");
                                    return true;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // When UIA does not expose the checkbox, use the visible blue fill
            // to distinguish checked from unchecked. Never blindly click it.
            if (LooksLikeCheckedEABox(sx, sy))
            {
                if (NativeClickScreen(sx, sy, "checkbox „Nie wylogowuj mnie”"))
                {
                    Thread.Sleep(250);

                    if (!LooksLikeCheckedEABox(sx, sy))
                    {
                        log("EA native: wykryto zaznaczony checkbox i wyłączono go.");
                        return true;
                    }

                    // Keyboard fallback based on the visible form order:
                    // focus e-mail -> Tab -> checkbox -> Space.
                    if (NativeClickRelative(hwnd, 0.50, 0.49, "pole e-mail (fallback checkbox)"))
                    {
                        Thread.Sleep(100);
                        if (NativeKeyboardInput.SendTab(log))
                        {
                            Thread.Sleep(80);
                            NativeKeyboardInput.Key(0x20, log); // VK_SPACE
                            Thread.Sleep(250);

                            if (!LooksLikeCheckedEABox(sx, sy))
                            {
                                log("EA keyboard: wyłączono „Nie wylogowuj mnie” przez Tab + Spacja.");
                                return true;
                            }
                        }
                    }
                }
            }

            log("EA: nie udało się potwierdzić ani wyłączyć „Nie wylogowuj mnie”.");
            return false;
        }

        private static bool LooksLikeCheckedEABox(int centerX, int centerY)
        {
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc == IntPtr.Zero)
                    return false;

                const int radius = 10;
                int blueCount = 0;
                int samples = 0;

                for (int y = -radius; y <= radius; y += 2)
                for (int x = -radius; x <= radius; x += 2)
                {
                    uint rgb = GetPixel(dc, centerX + x, centerY + y);
                    byte r = (byte)(rgb & 0xFF);
                    byte g = (byte)((rgb >> 8) & 0xFF);
                    byte b = (byte)((rgb >> 16) & 0xFF);
                    samples++;

                    if (b > 135 && g > 55 && r < 95 && b > r * 1.45 && b > g * 1.15)
                        blueCount++;
                }

                ReleaseDC(IntPtr.Zero, dc);
                return samples > 0 && (blueCount / (double)samples) > 0.10;
            }
            catch
            {
                return false;
            }
        }

        private bool NativeClickMenuLogoutAt(IntPtr hwnd, double menuX, double menuY)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect))
                return false;

            // These are screen pixels relative to the top-left of the EA
            // window. Do not apply DPI scaling a second time.
            int x = rect.Left + (int)Math.Round(menuX);
            int y = rect.Top + (int)Math.Round(menuY);
            return NativeClickScreen(x, y, "pozycję „Wyloguj się”");
        }

        private bool NativeClickMenuLogout(IntPtr hwnd)
        {
            return NativeClickMenuLogoutAt(hwnd, 44.0, 255.0);
        }

        private bool NativeClickScreen(int x, int y, string what)
        {
            try
            {
                if (!SetCursorPos(x, y))
                {
                    log("EA native: SetCursorPos nie powiódł się dla " + what + ".");
                    return false;
                }

                Thread.Sleep(80);

                INPUT[] inputs =
                {
                    CreateMouseInput(MOUSEEVENTF_LEFTDOWN),
                    CreateMouseInput(MOUSEEVENTF_LEFTUP)
                };

                uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
                if (sent != inputs.Length)
                {
                    log("EA native: SendInput myszy zwrócił " + sent + "/" + inputs.Length +
                        " dla " + what + ", Win32=" + Marshal.GetLastWin32Error() +
                        ". Próbuję mouse_event.");

                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(60);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                }

                Thread.Sleep(220);
                log("EA native: wykonano kliknięcie " + what + " w (" + x + "," + y + ").");
                return true;
            }
            catch (Exception ex)
            {
                log("EA native: błąd kliknięcia " + what + ": " + ex.Message);
                return false;
            }
        }

        private static AutomationElement SafeParent(AutomationElement element)
        {
            try { return element == null ? null : TreeWalker.RawViewWalker.GetParent(element); }
            catch { return null; }
        }

        private static bool Invoke(AutomationElement element)
        {
            try
            {
                var pattern = (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern);
                pattern.Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool FocusElement(AutomationElement element)
        {
            try
            {
                if (!element.Current.IsEnabled || element.Current.IsOffscreen)
                    return false;

                element.SetFocus();
                Thread.Sleep(120);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetVisibleText(IntPtr hwnd)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return string.Empty;

                var names = new List<string>();
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, AutomationCondition.TrueCondition))
                {
                    try
                    {
                        if (!e.Current.IsOffscreen && !string.IsNullOrWhiteSpace(e.Current.Name))
                            names.Add(e.Current.Name);
                    }
                    catch { }
                }

                return string.Join(" ", names);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeName(AutomationElement e)
        {
            try { return e.Current.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static IntPtr WaitForMainWindow(int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                IntPtr hwnd = FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                    return hwnd;

                Thread.Sleep(120);
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindMainWindowHandle()
        {
            foreach (Process p in SafeGetProcesses("EADesktop"))
            {
                try
                {
                    if (p.HasExited)
                        continue;

                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                        return hwnd;
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            return FindWindow(null, "EA");
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); }
            catch { yield break; }

            foreach (var p in ps)
                yield return p;
        }

        private static bool EnsureWindowForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                ShowWindow(hwnd, SW_RESTORE);

                IntPtr foreground = GetForegroundWindow();
                uint currentThread = GetCurrentThreadId();
                uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, IntPtr.Zero);
                uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);

                bool attached = false;
                try
                {
                    if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
                    {
                        attached = AttachThreadInput(foregroundThread, currentThread, true);
                        if (attached)
                            AttachThreadInput(currentThread, targetThread, true);
                    }

                    for (int i = 0; i < 8; i++)
                    {
                        if (GetForegroundWindow() == hwnd)
                            return true;

                        BringWindowToTop(hwnd);
                        SetForegroundWindow(hwnd);
                        Thread.Sleep(100);
                    }

                    return GetForegroundWindow() == hwnd;
                }
                finally
                {
                    if (attached)
                    {
                        if (targetThread != 0)
                            AttachThreadInput(currentThread, targetThread, false);
                        if (foregroundThread != 0)
                            AttachThreadInput(foregroundThread, currentThread, false);
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private const uint INPUT_MOUSE = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        private static INPUT CreateMouseInput(uint flags)
        {
            return new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hdc, int x, int y);
    }
}