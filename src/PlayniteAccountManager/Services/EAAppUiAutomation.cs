using AutomationCondition = System.Windows.Automation.Condition;
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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
        private const ushort VK_HOME = 0x0024;
        private const ushort VK_ESCAPE = 0x001B;

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

            // Give EA's WebView a short settling period only when its state is
            // genuinely unknown. Do not wait when the authenticated navigation
            // or login surface is already positively visible.
            Thread.Sleep(120);

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

            // If a previous attempt accidentally opened the offline confirmation,
            // cancel it before trying logout again.
            if (CancelOfflineConfirmationIfPresent(hwnd))
                Thread.Sleep(120);

            if (!EnsureWindowForeground(hwnd))
                log("EA App: nie uzyskano pełnej pewności aktywnego okna, ale kontynuuję.");

            if (!NativeClickRelative(hwnd, 0.010, 0.017, "hamburger menu"))
            {
                AutomationElement button = FindClickableAtRelativeRegion(hwnd, 0.0, 0.0, 0.055, 0.055, true);
                if (button == null || !Invoke(button))
                {
                    error = "Nie udało się otworzyć menu EA App.";
                    return false;
                }
            }

            // The user's EA menu screenshot shows:
            // Widok
            // Ustawienia
            // Pomoc
            // Informacje
            // Tryb offline
            // Wyloguj się
            // Wyjdź
            //
            // The center of "Wyloguj się" is about (95,590) px relative to
            // the window. The old (56,255) coordinate was the wrong row and
            // opened "Tryb offline".
            Thread.Sleep(160);

            // Prefer a control whose OWN NAME is logout.
            AutomationElement logoutElement = FindVisibleNamedInvokable(
                hwnd, new[] { "Wyloguj się", "Wyloguj", "Sign out", "Log out" });

            if (logoutElement != null)
            {
                log("EA UIA: znaleziono właściwą pozycję „" + SafeName(logoutElement) + "”.");
                if (Invoke(logoutElement))
                {
                    if (WaitForLoginScreen(hwnd, 2))
                        return true;

                    if (CancelOfflineConfirmationIfPresent(hwnd))
                    {
                        log("EA UIA: właściwy element wywołał dialog offline — anuluję.");
                    }
                }
            }

            // Native click at the corrected row. Use several x/y points inside
            // the same row in case the menu width/font changes slightly.
            double[,] points =
            {
                { 95.0, 590.0 },
                { 75.0, 590.0 },
                { 120.0, 590.0 },
                { 95.0, 575.0 },
                { 95.0, 605.0 }
            };

            for (int i = 0; i < points.GetLength(0); i++)
            {
                if (!NativeClickMenuLogoutAt(hwnd, points[i, 0], points[i, 1]))
                    continue;

                log("EA native: kliknięto przewidywany wiersz „Wyloguj się” (" +
                    points[i, 0].ToString("0") + "," +
                    points[i, 1].ToString("0") + ").");

                Thread.Sleep(100);

                if (CancelOfflineConfirmationIfPresent(hwnd))
                {
                    // This point hit "Tryb offline". Cancel immediately and try
                    // the next candidate rather than waiting several seconds.
                    log("EA: kliknięcie trafiło w „Tryb offline” — anuluję i próbuję ponownie.");
                    Thread.Sleep(100);
                    continue;
                }

                if (WaitForLoginScreen(hwnd, 2))
                {
                    log("EA: wylogowanie potwierdzone po kliknięciu.");
                    return true;
                }
            }

            error = "EA App nie wykonała polecenia „Wyloguj się”.";
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

            Thread.Sleep(180);

            // UIA can report a false ToggleState on EA's Qt/Cef login page.
            // We therefore do not trust the checkbox state. The current form
            // order is: e-mail -> "Nie wylogowuj mnie". Focus the e-mail again,
            // press Tab once and Space once to explicitly switch the checkbox
            // off before pressing "Dalej".
            if (!ForceRememberMeUnchecked(hwnd))
                log("EA: nie udało się wymusić stanu „Nie wylogowuj mnie”.");

            Thread.Sleep(120);

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
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return EAUiState.Unknown;

                bool hasLibrary = false;
                bool hasHome = false;
                bool hasInstalled = false;
                bool hasSearch = false;
                bool hasLoginHeader = false;
                bool hasEmailHint = false;
                bool hasPasswordHint = false;
                int visibleEdits = 0;

                // Read the accessibility tree once and classify it from the
                // actual visible element names. The previous implementation
                // joined every visible name into one huge string, which can
                // contain stale WebView/login text and falsely classify an
                // already-authenticated EA window as the login screen.
                var all = root.FindAll(TreeScope.Descendants, AutomationCondition.TrueCondition);
                foreach (AutomationElement e in all)
                {
                    try
                    {
                        if (e.Current.IsOffscreen || !e.Current.IsEnabled)
                            continue;

                        string name = (e.Current.Name ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            if (name.Equals("Biblioteka", StringComparison.OrdinalIgnoreCase))
                                hasLibrary = true;
                            else if (name.Equals("Strona główna", StringComparison.OrdinalIgnoreCase))
                                hasHome = true;
                            else if (name.Equals("Zainstalowane gry", StringComparison.OrdinalIgnoreCase))
                                hasInstalled = true;
                            else if (name.Equals("Szukaj", StringComparison.OrdinalIgnoreCase))
                                hasSearch = true;

                            if (name.IndexOf("Zaloguj się na swoje konto EA", StringComparison.OrdinalIgnoreCase) >= 0)
                                hasLoginHeader = true;
                            if (name.IndexOf("TWÓJ E-MAIL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("TWOJ E-MAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                                hasEmailHint = true;
                            if (name.IndexOf("Podaj hasło", StringComparison.OrdinalIgnoreCase) >= 0)
                                hasPasswordHint = true;
                        }

                        if (e.Current.ControlType == ControlType.Edit)
                            visibleEdits++;
                    }
                    catch { }
                }

                // Strong authenticated signals from the EA diagnostic:
                // visible "Strona główna", "Biblioteka" and "Zainstalowane gry".
                // These must win over any generic edit/login hints.
                if ((hasLibrary && hasHome) ||
                    (hasLibrary && hasInstalled) ||
                    (hasHome && hasSearch))
                {
                    return EAUiState.Authenticated;
                }

                // The login header/field hints are positive login signals only
                // when the authenticated navigation is not present.
                if (hasLoginHeader || hasEmailHint || hasPasswordHint)
                    return EAUiState.Login;

                // A login page normally exposes an Edit, while the authenticated
                // screen's search field is already handled by the strong auth
                // checks above.
                if (visibleEdits >= 1)
                    return EAUiState.Login;

                return EAUiState.Unknown;
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

        private bool TryInvokePointedMenuItem(IntPtr hwnd, double menuX, double menuY)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect))
                return false;

            int x = rect.Left + (int)Math.Round(menuX);
            int y = rect.Top + (int)Math.Round(menuY);

            try
            {
                AutomationElement current = AutomationElement.FromPoint(new Point(x, y));
                for (int i = 0; current != null && i < 8; i++, current = SafeParent(current))
                {
                    try
                    {
                        if (current.Current.IsOffscreen || !current.Current.IsEnabled)
                            continue;

                        string name = current.Current.Name ?? string.Empty;
                        log("EA UIA: punkt menu (" + menuX.ToString("0") + "," + menuY.ToString("0") +
                            ") -> „" + name + "” / " + current.Current.ControlType.ProgrammaticName + ".");

                        // Never invoke a generic Custom ancestor just because it
                        // happens to expose InvokePattern. It may represent
                        // another menu row ("Tryb offline") and cause the wrong
                        // action.
                        if (name.IndexOf("Wyloguj", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("Sign out", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("Log out", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        try
                        {
                            var invoke = (InvokePattern)current.GetCurrentPattern(InvokePattern.Pattern);
                            invoke.Invoke();
                            return true;
                        }
                        catch { }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private bool TryKeyboardMenuLogout()
        {
            // Disabled as a primary fallback because EA may not give keyboard
            // focus to the popup. Blind DOWN/ENTER previously selected
            // "Tryb offline".
            return false;
        }

        private bool ForceRememberMeUnchecked(IntPtr hwnd)
        {
            try
            {
                // Put focus on the actual e-mail edit using the same stable
                // native click already used for login.
                if (!NativeClickRelative(hwnd, 0.50, 0.49, "pole e-mail przed odznaczeniem"))
                    return false;

                Thread.Sleep(70);

                // On the current EA login page the next focusable control is
                // "Nie wylogowuj mnie".
                if (!NativeKeyboardInput.SendTab(log))
                    return false;

                Thread.Sleep(70);

                if (!NativeKeyboardInput.Key(0x20, log)) // VK_SPACE
                    return false;

                Thread.Sleep(180);
                log("EA keyboard: przełączono „Nie wylogowuj mnie” przez E-mail -> Tab -> Spacja.");
                return true;
            }
            catch (Exception ex)
            {
                log("EA keyboard: błąd przełączania „Nie wylogowuj mnie”: " + ex.Message);
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

                Thread.Sleep(80);
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
                    if (IsUsefulEAMainWindow(hwnd))
                        return hwnd;
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            // MainWindowHandle can stay zero during EA's startup. Find the
            // actual Qt/Cef top-level window as soon as Windows creates it.
            IntPtr found = FindEADesktopWindowByEnumeration();
            if (found != IntPtr.Zero)
                return found;

            return FindWindow(null, "EA");
        }

        private static bool IsUsefulEAMainWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                if (!IsWindowVisible(hwnd))
                    return false;

                StringBuilder title = new StringBuilder(128);
                GetWindowText(hwnd, title, title.Capacity);
                string windowTitle = title.ToString();

                string className = GetWindowClassName(hwnd);

                return windowTitle.Equals("EA", StringComparison.OrdinalIgnoreCase) ||
                       className.Equals("Qt5152QWindowOwnDCIcon", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr FindEADesktopWindowByEnumeration()
        {
            IntPtr result = IntPtr.Zero;

            EnumWindows((hwnd, lParam) =>
            {
                if (!IsWindowVisible(hwnd))
                    return true;

                uint pid = 0;
                GetWindowThreadProcessId(hwnd, out pid);

                try
                {
                    using (var p = Process.GetProcessById((int)pid))
                    {
                        if (!p.ProcessName.Equals("EADesktop", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch
                {
                    return true;
                }

                string className = GetWindowClassName(hwnd);
                if (className.Equals("Qt5152QWindowOwnDCIcon", StringComparison.OrdinalIgnoreCase))
                {
                    result = hwnd;
                    return false;
                }

                StringBuilder title = new StringBuilder(128);
                GetWindowText(hwnd, title, title.Capacity);
                if (title.ToString().Equals("EA", StringComparison.OrdinalIgnoreCase))
                {
                    result = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            try
            {
                GetClassName(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
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
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const int MK_LBUTTON = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

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

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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