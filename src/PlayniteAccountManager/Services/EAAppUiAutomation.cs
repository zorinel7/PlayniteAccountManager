using AutomationCondition = System.Windows.Automation.Condition;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// EA App UI automation used only for the first login of an account.
    /// Account switching itself is handled by EAAppSessionStore, which swaps
    /// the launcher session files instead of clicking EA's logout menu.
    /// </summary>
    internal sealed class EAAppUiAutomation
    {
        private readonly Action<string> log;
        private const int SW_RESTORE = 9;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        public EAAppUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public static IntPtr FindMainWindowHandlePublic()
        {
            return FindMainWindowHandle();
        }

        public bool PrepareAndLogin(string username, string password, int timeoutSeconds, out string error)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return PrepareAndLoginCore(username, password, timeoutSeconds, out error);

            bool result = false;
            string workerError = null;
            Exception workerException = null;

            var thread = new Thread(() =>
            {
                try
                {
                    result = PrepareAndLoginCore(username, password, timeoutSeconds, out workerError);
                }
                catch (Exception ex)
                {
                    workerException = ex;
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (workerException != null)
            {
                error = "Błąd wątku automatyzacji EA App: " + workerException.Message;
                return false;
            }

            error = workerError;
            return result;
        }

        public bool WaitUntilAuthenticated(int timeoutSeconds, out string error)
        {
            error = null;

            IntPtr hwnd = WaitForMainWindow(timeoutSeconds);
            if (hwnd == IntPtr.Zero)
            {
                error = "Nie znaleziono okna EA App.";
                return false;
            }

            EnsureWindowForeground(hwnd);

            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, timeoutSeconds));
            while (DateTime.UtcNow < deadline)
            {
                if (IsAuthenticatedScreen(hwnd))
                    return true;

                Thread.Sleep(140);
                hwnd = FindMainWindowHandle();
                if (hwnd == IntPtr.Zero)
                    continue;
            }

            error = "EA App nie potwierdziła zalogowania.";
            return false;
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
                error = "Nie znaleziono okna EA App po uruchomieniu.";
                return false;
            }

            EnsureWindowForeground(hwnd);

            // After session reset there should be a login page. We do not
            // perform GUI logout and we do not touch "Nie wylogowuj mnie".
            if (!WaitForLoginScreen(hwnd, 6))
                log("EA App: UIA nie potwierdziła ekranu logowania. Używam natywnego logowania.");

            if (!PerformLogin(hwnd, username, password, 10, out error))
                return false;

            return WaitUntilAuthenticated(15, out error);
        }

        private bool PerformLogin(IntPtr hwnd, string username, string password, int timeoutSeconds, out string error)
        {
            error = null;

            AutomationElement emailEdit = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Min(4, Math.Max(2, timeoutSeconds)));

            while (DateTime.UtcNow < deadline && emailEdit == null)
            {
                emailEdit = FindLoginEdit(hwnd, 0);
                if (emailEdit == null)
                    Thread.Sleep(150);
            }

            if (emailEdit != null && FocusElement(emailEdit))
            {
                log("EA UIA: ustawiono fokus pola e-mail.");
            }
            else
            {
                log("EA UIA: pole e-mail nie jest dostępne. Używam natywnego kliknięcia.");
                if (!NativeClickRelative(hwnd, 0.50, 0.49, "pole e-mail / EA ID"))
                {
                    error = "Nie udało się ustawić pola e-mail / EA ID.";
                    return false;
                }
            }

            if (!NativeKeyboardInput.TypeText(username, hwnd, log))
            {
                error = "Nie udało się wpisać e-maila / EA ID.";
                return false;
            }

            Thread.Sleep(100);

            AutomationElement next = FindButtonByNames(hwnd, "Dalej", "Continue", "Next");
            if (next != null && Invoke(next))
            {
                log("EA UIA: wykonano przycisk „Dalej”.");
            }
            else
            {
                log("EA UIA: przycisk „Dalej” nie jest dostępny. Używam natywnego kliknięcia / Enter.");
                if (!NativeClickRelative(hwnd, 0.50, 0.685, "przycisk „DALEJ”") &&
                    !NativeKeyboardInput.SendEnter(log))
                {
                    error = "Nie udało się przejść do pola hasła w EA App.";
                    return false;
                }
            }

            Thread.Sleep(400);

            AutomationElement passwordEdit = null;
            DateTime passDeadline = DateTime.UtcNow.AddSeconds(4);

            while (DateTime.UtcNow < passDeadline && passwordEdit == null)
            {
                passwordEdit = FindPasswordEdit(hwnd);
                if (passwordEdit == null)
                    Thread.Sleep(150);
            }

            if (passwordEdit != null && FocusElement(passwordEdit))
            {
                log("EA UIA: ustawiono fokus pola hasła.");
            }
            else
            {
                log("EA UIA: pole hasła nie jest dostępne. Używam natywnego kliknięcia.");
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

            Thread.Sleep(220);

            AutomationElement loginButton = FindButtonByNames(
                hwnd, "Zaloguj", "Zaloguj się", "Log in", "Sign in");

            if (loginButton != null && Invoke(loginButton))
            {
                log("EA UIA: wykonano przycisk logowania.");
            }
            else
            {
                log("EA UIA: przycisk logowania nie jest dostępny. Używam natywnego kliknięcia / Enter.");
                if (!NativeClickRelative(hwnd, 0.50, 0.56, "przycisk „ZALOGUJ”") &&
                    !NativeKeyboardInput.SendEnter(log))
                {
                    error = "Nie udało się zatwierdzić logowania EA App.";
                    return false;
                }
            }

            return true;
        }

        private static AutomationElement FindLoginEdit(IntPtr hwnd, int index)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            var edits = root.FindAll(
                TreeScope.Descendants,
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

            var edits = root.FindAll(
                TreeScope.Descendants,
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
                    var rect = e.Current.BoundingRectangle;
                    if (rect.Width > 0 && rect.Height > 0)
                        list.Add(Tuple.Create(rect.Top * 100000 + rect.Left, e));
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
                    if (!e.Current.IsEnabled ||
                        e.Current.IsOffscreen ||
                        e.Current.ControlType != ControlType.Button)
                        continue;

                    string name = e.Current.Name ?? string.Empty;

                    if (names.Any(n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                        return e;
                }
                catch { }
            }

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
                bool loginHeader = false;
                bool loginEmail = false;
                bool loginPassword = false;

                var all = root.FindAll(TreeScope.Descendants, AutomationCondition.TrueCondition);

                foreach (AutomationElement e in all)
                {
                    try
                    {
                        if (e.Current.IsOffscreen || !e.Current.IsEnabled)
                            continue;

                        string name = (e.Current.Name ?? string.Empty).Trim();

                        if (name.Equals("Biblioteka", StringComparison.OrdinalIgnoreCase))
                            hasLibrary = true;

                        if (name.Equals("Strona główna", StringComparison.OrdinalIgnoreCase))
                            hasHome = true;

                        if (name.Equals("Zainstalowane gry", StringComparison.OrdinalIgnoreCase))
                            hasInstalled = true;

                        if (name.IndexOf("Zaloguj się na swoje konto EA", StringComparison.OrdinalIgnoreCase) >= 0)
                            loginHeader = true;

                        if (name.IndexOf("TWÓJ E-MAIL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("TWOJ E-MAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                            loginEmail = true;

                        if (name.IndexOf("Podaj hasło", StringComparison.OrdinalIgnoreCase) >= 0)
                            loginPassword = true;
                    }
                    catch { }
                }

                if ((hasLibrary && hasHome) || (hasLibrary && hasInstalled))
                    return EAUiState.Authenticated;

                if (loginHeader || loginEmail || loginPassword)
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

        private bool NativeClickRelative(IntPtr hwnd, double xPct, double yPct, string what)
        {
            RECT rect;
            if (!GetWindowRect(hwnd, out rect))
                return false;

            int x = rect.Left + (int)Math.Round((rect.Right - rect.Left) * xPct);
            int y = rect.Top + (int)Math.Round((rect.Bottom - rect.Top) * yPct);

            return NativeClickScreen(x, y, what);
        }

        private bool NativeClickScreen(int x, int y, string what)
        {
            try
            {
                if (!SetCursorPos(x, y))
                    return false;

                Thread.Sleep(40);

                INPUT[] inputs =
                {
                    CreateMouseInput(MOUSEEVENTF_LEFTDOWN),
                    CreateMouseInput(MOUSEEVENTF_LEFTUP)
                };

                uint sent = SendInput(
                    (uint)inputs.Length,
                    inputs,
                    Marshal.SizeOf(typeof(INPUT)));

                if (sent != inputs.Length)
                {
                    log("EA native: SendInput nie wykonał pełnego kliknięcia " + what + ".");
                    return false;
                }

                Thread.Sleep(120);
                log("EA native: wykonano kliknięcie " + what + " w (" + x + "," + y + ").");
                return true;
            }
            catch (Exception ex)
            {
                log("EA native: błąd kliknięcia " + what + ": " + ex.Message);
                return false;
            }
        }

        private static INPUT CreateMouseInput(uint flags)
        {
            return new INPUT
            {
                type = 0,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            };
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
                Thread.Sleep(80);
                return true;
            }
            catch
            {
                return false;
            }
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
            foreach (Process process in SafeGetProcesses("EADesktop"))
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    IntPtr hwnd = process.MainWindowHandle;

                    if (IsUsefulEAMainWindow(hwnd))
                        return hwnd;
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }

            IntPtr enumerated = FindEADesktopWindowByEnumeration();
            if (enumerated != IntPtr.Zero)
                return enumerated;

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

                StringBuilder title = new StringBuilder(64);
                GetWindowText(hwnd, title, title.Capacity);

                return title.ToString().Equals("EA", StringComparison.OrdinalIgnoreCase) ||
                       GetWindowClassName(hwnd).Equals(
                           "Qt5152QWindowOwnDCIcon",
                           StringComparison.OrdinalIgnoreCase);
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

                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);

                try
                {
                    using (var process = Process.GetProcessById((int)pid))
                    {
                        if (!process.ProcessName.Equals("EADesktop", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch
                {
                    return true;
                }

                string className = GetWindowClassName(hwnd);

                if (className.Equals(
                        "Qt5152QWindowOwnDCIcon",
                        StringComparison.OrdinalIgnoreCase))
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
            StringBuilder sb = new StringBuilder(128);

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
            Process[] processes;

            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                yield break;
            }

            foreach (Process process in processes)
                yield return process;
        }

        private static bool EnsureWindowForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                ShowWindow(hwnd, SW_RESTORE);

                for (int i = 0; i < 4; i++)
                {
                    if (GetForegroundWindow() == hwnd)
                        return true;

                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(50);
                }

                return GetForegroundWindow() == hwnd;
            }
            catch
            {
                return false;
            }
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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(
            string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(
            IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint nInputs, [In] INPUT[] pInputs, int cbSize);
    }
}
