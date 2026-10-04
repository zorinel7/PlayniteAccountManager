using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Reproduces the supplied working AutoHotkey flow without an external
    /// AutoHotkey process. UI Automation is used only to find/activate the
    /// Ubisoft Connect top-level window. It is deliberately NOT used to set
    /// the value of the e-mail/password controls.
    /// </summary>
    internal sealed class UbisoftConnectUiAutomation
    {
        private const string MainProcessName = "upc";
        private const string MainWindowName = "Ubisoft Connect";
        private const string MainWindowClass = "Chrome_WidgetWin_1";

        private readonly Action<string> log;

        public UbisoftConnectUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool Login(string username, string password, int timeoutSeconds, out string error)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return LoginCore(username, password, timeoutSeconds, out error);

            bool result = false;
            string workerError = null;
            Exception workerException = null;

            Thread thread = new Thread(() =>
            {
                try
                {
                    result = LoginCore(username, password, timeoutSeconds, out workerError);
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
                error = "Błąd wątku automatyzacji Ubisoft Connect: " + workerException.Message;
                return false;
            }

            error = workerError;
            return result;
        }

        private bool LoginCore(string username, string password, int timeoutSeconds, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanych dla konta Ubisoft Connect.";
                return false;
            }

            DateTime windowDeadline = DateTime.UtcNow.AddSeconds(Math.Max(10, timeoutSeconds));
            IntPtr hwnd = IntPtr.Zero;

            log("Ubisoft INPUT: czekam na okno „Ubisoft Connect”, odpowiednik WinWait z AHK...");
            while (DateTime.UtcNow < windowDeadline)
            {
                hwnd = FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                    break;
                Thread.Sleep(250);
            }

            if (hwnd == IntPtr.Zero)
            {
                error = "Nie znaleziono okna „Ubisoft Connect” w czasie oczekiwania.";
                return false;
            }

            log("Ubisoft INPUT: znaleziono okno. HWND=0x" + hwnd.ToInt64().ToString("X") + ".");

            // Reproduce: WinActivate, then Sleep 15000.
            if (!EnsureWindowForeground(hwnd))
            {
                log("Ubisoft INPUT: WinActivate nie dał pełnej pewności o aktywnym oknie, ale kontynuuję jak AHK.");
            }

            log("Ubisoft INPUT: WinActivate wykonane. Czekam 15 s jak w działającym AHK...");
            Thread.Sleep(15000);

            // Reproduce the supplied AHK sequence literally.
            EnsureWindowForeground(hwnd);

            log("Ubisoft INPUT: [1/8] Enter");
            if (!NativeKeyboardInput.SendEnter(log))
            {
                error = "Nie udało się wysłać pierwszego Enter do Ubisoft Connect.";
                return false;
            }

            log("Ubisoft INPUT: Sleep 2000 ms");
            Thread.Sleep(2000);

            // Use the keyboard focus as AHK does. We do not search for or focus
            // the e-mail control here because the AHK example does not do that.
            hwnd = FindMainWindowHandle();
            if (hwnd != IntPtr.Zero)
                EnsureWindowForeground(hwnd);

            log("Ubisoft INPUT: [2/8] wpisuję login przez VkKeyScanEx + key events.");
            if (!NativeKeyboardInput.TypeText(username, hwnd, log))
            {
                error = "Nie udało się wysłać loginu jako zdarzeń klawiatury.";
                return false;
            }

            log("Ubisoft INPUT: Sleep 300 ms");
            Thread.Sleep(300);

            log("Ubisoft INPUT: [3/8] TAB");
            if (!NativeKeyboardInput.SendTab(log))
            {
                error = "Nie udało się wysłać TAB do Ubisoft Connect.";
                return false;
            }

            log("Ubisoft INPUT: Sleep 300 ms");
            Thread.Sleep(300);

            log("Ubisoft INPUT: [4/8] Enter po TAB");
            if (!NativeKeyboardInput.SendEnter(log))
            {
                error = "Nie udało się wysłać Enter po loginie.";
                return false;
            }

            log("Ubisoft INPUT: Sleep 300 ms");
            Thread.Sleep(300);

            hwnd = FindMainWindowHandle();
            if (hwnd != IntPtr.Zero)
                EnsureWindowForeground(hwnd);

            log("Ubisoft INPUT: [5/8] wpisuję hasło przez VkKeyScanEx + key events.");
            if (!NativeKeyboardInput.TypeText(password, hwnd, log))
            {
                error = "Nie udało się wysłać hasła jako zdarzeń klawiatury.";
                return false;
            }

            log("Ubisoft INPUT: Sleep 300 ms");
            Thread.Sleep(300);

            log("Ubisoft INPUT: [6/8] Enter po haśle");
            if (!NativeKeyboardInput.SendEnter(log))
            {
                error = "Nie udało się wysłać końcowego Enter do Ubisoft Connect.";
                return false;
            }

            // The remaining part is only verification; no credentials are read
            // back from UIA and no password value is logged.
            log("Ubisoft INPUT: [7/8] czekam na wynik logowania (20 s)...");
            DateTime verifyDeadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < verifyDeadline)
            {
                hwnd = FindMainWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    if (LooksAuthenticated(hwnd))
                    {
                        log("Ubisoft INPUT: [8/8] Ubisoft Connect wygląda na zalogowany.");
                        return true;
                    }
                }
                Thread.Sleep(500);
            }

            // Some versions can keep the login page visible while the submit is
            // still being processed. Do not claim success without a visible state.
            error = "Ubisoft Connect nie potwierdził zalogowania po wysłaniu danych.";
            return false;
        }

        private static IntPtr FindMainWindowHandle()
        {
            foreach (Process process in SafeGetProcesses(MainProcessName))
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    IntPtr hwnd = process.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                        continue;

                    string title = null;
                    try { title = process.MainWindowTitle; } catch { }
                    if (!string.IsNullOrWhiteSpace(title) &&
                        title.IndexOf(MainWindowName, StringComparison.OrdinalIgnoreCase) >= 0)
                        return hwnd;
                }
                catch { }
                finally { process.Dispose(); }
            }

            return FindWindow(MainWindowClass, MainWindowName);
        }

        private static bool LooksAuthenticated(IntPtr hwnd)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return false;

                AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                var names = new List<string>();
                foreach (AutomationElement element in all)
                {
                    try
                    {
                        if (element.Current.IsOffscreen)
                            continue;
                        string name = element.Current.Name;
                        if (!string.IsNullOrWhiteSpace(name))
                            names.Add(name);
                    }
                    catch { }
                }

                string text = string.Join(" ", names);
                // Presence of a login form means we definitely did not finish.
                if (text.IndexOf("Enter your email address", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("Enter your password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("Log in", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;

                return text.IndexOf("Library", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       text.IndexOf("Store", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       text.IndexOf("Log out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       text.IndexOf("Sign out", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<Process> SafeGetProcesses(string name)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { yield break; }
            foreach (Process process in processes)
                yield return process;
        }

        private bool EnsureWindowForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                ShowWindow(hwnd, SW_RESTORE);

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (GetForegroundWindow() == hwnd)
                        return true;

                    IntPtr foreground = GetForegroundWindow();
                    uint currentThread = GetCurrentThreadId();
                    uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                    uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
                    bool attachedForeground = false;
                    bool attachedTarget = false;

                    try
                    {
                        if (foregroundThread != 0 && foregroundThread != currentThread)
                            attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
                        if (targetThread != 0 && targetThread != currentThread)
                            attachedTarget = AttachThreadInput(currentThread, targetThread, true);

                        BringWindowToTop(hwnd);
                        SetForegroundWindow(hwnd);
                        SetActiveWindow(hwnd);
                    }
                    finally
                    {
                        if (targetThread != 0 && targetThread != currentThread && attachedTarget)
                            AttachThreadInput(currentThread, targetThread, false);
                        if (foregroundThread != 0 && foregroundThread != currentThread && attachedForeground)
                            AttachThreadInput(currentThread, foregroundThread, false);
                    }

                    Thread.Sleep(120);
                }

                return GetForegroundWindow() == hwnd;
            }
            catch (Exception ex)
            {
                log("Ubisoft INPUT: błąd aktywacji okna: " + ex.Message);
                return false;
            }
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
