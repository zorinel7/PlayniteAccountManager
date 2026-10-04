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
    /// No mouse coordinates and no tab-navigation are used.
    /// For the profile/menu surface EA does not expose reliable named controls
    /// in the normal subtree, so the adapter uses AutomationElement.FromPoint()
    /// relative to the live EA window to discover the clickable hamburger control
    /// and the visible "Wyloguj się" command when the popup is displayed.
    /// </summary>
    internal sealed class EAAppUiAutomation
    {
        private readonly Action<string> log;
        private const int SW_RESTORE = 9;

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

            if (!IsLoginScreen(hwnd))
            {
                log("EA App: wykryto aktywną sesję. Otwieram menu główne i szukam „Wyloguj się”.");
                if (!OpenHamburgerAndLogout(hwnd, 12, out error))
                    return false;
            }

            hwnd = WaitForMainWindow(20);
            if (hwnd == IntPtr.Zero) { error = "Nie znaleziono okna logowania EA App."; return false; }

            if (!WaitForLoginScreen(hwnd, 20))
            {
                error = "EA App nie pokazała ekranu logowania po wylogowaniu.";
                return false;
            }

            if (!PerformLogin(hwnd, username, password, 35, out error))
                return false;

            if (!WaitForAuthenticated(hwnd, 30))
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
            if (hwnd == IntPtr.Zero) { error = "Nie znaleziono okna EA App."; return false; }
            EnsureWindowForeground(hwnd);
            return OpenHamburgerAndLogout(hwnd, 12, out error);
        }

        private bool OpenHamburgerAndLogout(IntPtr hwnd, int timeoutSeconds, out string error)
        {
            error = null;
            if (!EnsureWindowForeground(hwnd))
                log("EA App: nie uzyskano pełnej pewności aktywnego okna, ale kontynuuję UIA.");

            AutomationElement button = FindClickableAtRelativeRegion(hwnd, 0.0, 0.0, 0.07, 0.055, true);
            if (button == null)
            {
                error = "Nie znaleziono przycisku menu EA App przez UI Automation.";
                return false;
            }

            log("EA UIA: znaleziono kontrolkę menu głównego. ControlType=" + SafeControlType(button) + ".");
            if (!Invoke(button))
            {
                error = "Nie udało się otworzyć menu głównego EA App przez UI Automation.";
                return false;
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, timeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                AutomationElement logout = FindTextInPopup(hwnd, new[] { "Wyloguj się", "Wyloguj", "Sign out", "Log out" });
                if (logout != null)
                {
                    log("EA UIA: znaleziono „" + SafeName(logout) + "” w menu.");
                    if (!Invoke(logout))
                    {
                        error = "Nie udało się wykonać polecenia wylogowania EA App.";
                        return false;
                    }
                    log("EA UIA: wykonano wylogowanie.");
                    return true;
                }
                Thread.Sleep(250);
            }

            error = "Po otwarciu menu EA App nie znaleziono pozycji „Wyloguj się”.";
            return false;
        }

        private bool PerformLogin(IntPtr hwnd, string username, string password, int timeoutSeconds, out string error)
        {
            error = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, timeoutSeconds));

            AutomationElement emailEdit = null;
            while (DateTime.UtcNow < deadline && emailEdit == null)
            {
                emailEdit = FindLoginEdit(hwnd, 0);
                if (emailEdit == null) Thread.Sleep(250);
            }
            if (emailEdit == null) { error = "Nie znaleziono pola e-mail / EA ID."; return false; }

            if (!FocusElement(emailEdit)) { error = "Nie udało się ustawić fokusu na polu e-mail / EA ID."; return false; }
            if (!NativeKeyboardInput.TypeText(username, hwnd, log)) { error = "Nie udało się wpisać e-maila / EA ID."; return false; }
            Thread.Sleep(250);

            AutomationElement next = FindButtonByNames(hwnd, "Dalej", "Continue", "Next");
            if (next != null)
            {
                if (!Invoke(next)) { error = "Nie udało się nacisnąć „Dalej” w EA App."; return false; }
            }
            else
            {
                if (!NativeKeyboardInput.SendEnter(log)) { error = "Nie udało się przejść do pola hasła w EA App."; return false; }
            }

            Thread.Sleep(500);
            AutomationElement passwordEdit = null;
            DateTime passDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < passDeadline && passwordEdit == null)
            {
                passwordEdit = FindPasswordEdit(hwnd);
                if (passwordEdit == null) Thread.Sleep(250);
            }
            if (passwordEdit == null) { error = "Nie znaleziono pola hasła w EA App."; return false; }

            if (!FocusElement(passwordEdit)) { error = "Nie udało się ustawić fokusu na polu hasła EA App."; return false; }
            if (!NativeKeyboardInput.TypeText(password, hwnd, log)) { error = "Nie udało się wpisać hasła EA App."; return false; }
            Thread.Sleep(250);

            AutomationElement loginButton = FindButtonByNames(hwnd, "Zaloguj", "Zaloguj się", "Log in", "Sign in");
            if (loginButton != null)
            {
                if (!Invoke(loginButton)) { error = "Nie udało się nacisnąć przycisku logowania EA App."; return false; }
            }
            else if (!NativeKeyboardInput.SendEnter(log))
            {
                error = "Nie udało się zatwierdzić logowania EA App.";
                return false;
            }

            return true;
        }

        private static AutomationElement FindLoginEdit(IntPtr hwnd, int index)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;
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
            if (root == null) return null;
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
            if (root == null) return null;
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement e in all)
            {
                try
                {
                    if (!e.Current.IsEnabled || e.Current.IsOffscreen) continue;
                    if (e.Current.ControlType != ControlType.Button) continue;
                    string name = e.Current.Name ?? string.Empty;
                    if (names.Any(n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                        return e;
                }
                catch { }
            }
            return null;
        }

        private static bool IsLoginScreen(IntPtr hwnd)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null) return false;
                var names = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Select(SafeName)
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                string text = string.Join(" ", names);
                int edits = CountVisibleEdits(hwnd);
                bool loginHint = text.IndexOf("Zaloguj się na swoje konto EA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 text.IndexOf("TWOJ E-MAIL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 text.IndexOf("TWÓJ E-MAIL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 text.IndexOf("Podaj hasło", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 text.IndexOf("Log in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 text.IndexOf("Sign in", StringComparison.OrdinalIgnoreCase) >= 0;
                return loginHint || edits >= 1 && text.IndexOf("EA PC", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch { return false; }
        }

        private static int CountVisibleEdits(IntPtr hwnd)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null) return 0;
                return root.FindAll(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false))).Count;
            }
            catch { return 0; }
        }

        private static bool WaitForLoginScreen(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, seconds));
            while (DateTime.UtcNow < deadline)
            {
                if (IsLoginScreen(hwnd)) return true;
                Thread.Sleep(300);
                hwnd = FindMainWindowHandle();
                if (hwnd == IntPtr.Zero) continue;
            }
            return IsLoginScreen(hwnd);
        }

        private static bool WaitForAuthenticated(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, seconds));
            while (DateTime.UtcNow < deadline)
            {
                if (!IsLoginScreen(hwnd))
                {
                    try
                    {
                        string text = GetVisibleText(hwnd);
                        if (text.IndexOf("Biblioteka", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            text.IndexOf("Strona główna", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            text.IndexOf("EA Play", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                    catch { }
                }
                Thread.Sleep(500);
                hwnd = FindMainWindowHandle();
                if (hwnd == IntPtr.Zero) continue;
            }
            return false;
        }

        private static AutomationElement FindClickableAtRelativeRegion(IntPtr hwnd, double leftPct, double topPct, double rightPct, double bottomPct, bool preferButton)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return null;
            double left = rect.Left + (rect.Right - rect.Left) * leftPct;
            double top = rect.Top + (rect.Bottom - rect.Top) * topPct;
            double right = rect.Left + (rect.Right - rect.Left) * rightPct;
            double bottom = rect.Top + (rect.Bottom - rect.Top) * bottomPct;

            AutomationElement best = null;
            double bestScore = double.MaxValue;
            for (double y = top; y <= Math.Max(top, bottom); y += 8)
            for (double x = left; x <= Math.Max(left, right); x += 8)
            {
                try
                {
                    AutomationElement e = AutomationElement.FromPoint(new Point(x, y));
                    if (e == null || e.Current.IsOffscreen || !e.Current.IsEnabled) continue;
                    bool invokable = false;
                    try { e.GetCurrentPattern(InvokePattern.Pattern); invokable = true; } catch { }
                    if (!invokable) continue;
                    if (preferButton && e.Current.ControlType != ControlType.Button && e.Current.ControlType != ControlType.Custom) continue;
                    var r = e.Current.BoundingRectangle;
                    double score = Math.Abs(r.Left - left) + Math.Abs(r.Top - top);
                    if (score < bestScore) { bestScore = score; best = e; }
                }
                catch { }
            }
            return best;
        }

        private static AutomationElement FindTextInPopup(IntPtr hwnd, string[] needles)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return null;
            double left = rect.Left;
            double right = rect.Left + Math.Min(360, rect.Right - rect.Left * 0 + 360);
            double top = rect.Top + Math.Min(90, (rect.Bottom - rect.Top) * 0.12);
            double bottom = rect.Top + Math.Min(500, (rect.Bottom - rect.Top) * 0.60);

            var seen = new HashSet<int>();
            for (double y = top; y <= bottom; y += 8)
            for (double x = left + 5; x <= Math.Min(right, rect.Left + 360); x += 8)
            {
                try
                {
                    AutomationElement e = AutomationElement.FromPoint(new Point(x, y));
                    if (e == null || !seen.Add(e.GetHashCode())) continue;
                    AutomationElement current = e;
                    for (int level = 0; current != null && level < 6; level++)
                    {
                        string name = SafeName(current);
                        if (!string.IsNullOrWhiteSpace(name) && needles.Any(n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            try { if (!current.Current.IsEnabled || current.Current.IsOffscreen) { current = SafeParent(current); continue; } } catch { }
                            try { current.GetCurrentPattern(InvokePattern.Pattern); return current; } catch { }
                        }
                        current = SafeParent(current);
                    }
                }
                catch { }
            }
            return null;
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
            try { if (!element.Current.IsEnabled || element.Current.IsOffscreen) return false; element.SetFocus(); Thread.Sleep(120); return true; }
            catch { return false; }
        }

        private static string GetVisibleText(IntPtr hwnd)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null) return string.Empty;
                var names = new List<string>();
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    try { if (!e.Current.IsOffscreen && !string.IsNullOrWhiteSpace(e.Current.Name)) names.Add(e.Current.Name); } catch { }
                }
                return string.Join(" ", names);
            }
            catch { return string.Empty; }
        }

        private static string SafeName(AutomationElement e) { try { return e.Current.Name ?? string.Empty; } catch { return string.Empty; } }
        private static string SafeControlType(AutomationElement e) { try { return e.Current.ControlType.ProgrammaticName; } catch { return "unknown"; } }

        private static IntPtr WaitForMainWindow(int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSeconds));
            IntPtr hwnd;
            while (DateTime.UtcNow < deadline)
            {
                hwnd = FindMainWindowHandle();
                if (hwnd != IntPtr.Zero) return hwnd;
                Thread.Sleep(250);
            }
            return IntPtr.Zero;
        }

        private static IntPtr FindMainWindowHandle()
        {
            foreach (Process p in SafeGetProcesses("EADesktop"))
            {
                try
                {
                    if (p.HasExited) continue;
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd != IntPtr.Zero) return hwnd;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return FindWindow(null, "EA");
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); } catch { yield break; }
            foreach (var p in ps) yield return p;
        }

        private static bool EnsureWindowForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                ShowWindow(hwnd, SW_RESTORE);
                for (int i = 0; i < 5; i++)
                {
                    if (GetForegroundWindow() == hwnd) return true;
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(120);
                }
                return GetForegroundWindow() == hwnd;
            }
            catch { return false; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    }
}
