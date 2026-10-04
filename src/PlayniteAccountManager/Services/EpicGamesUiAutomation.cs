using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Epic Games login automation.
    ///
    /// Epic's embedded WebView does not expose a reliable input/control tree
    /// through UI Automation on the user's current launcher build. Therefore
    /// Epic uses the exact keyboard sequence verified manually by the user.
    ///
    /// The important part is making Epic the real foreground/active window
    /// before each phase. This uses the same AttachThreadInput foreground
    /// technique already proven in the Steam and Ubisoft integrations.
    /// </summary>
    internal sealed class EpicGamesUiAutomation
    {
        private readonly Action<string> log;
        private const int SW_RESTORE = 9;

        private const ushort VK_TAB = 0x0009;

        public EpicGamesUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool Login(string username, string password, int timeoutSeconds, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(username))
                return Fail(out error, "Brak loginu/e-maila Epic Games.");

            if (string.IsNullOrEmpty(password))
                return Fail(out error, "Brak hasła Epic Games.");

            IntPtr hwnd = WaitForMainWindow(timeoutSeconds);
            if (hwnd == IntPtr.Zero)
                return Fail(out error, "Nie znaleziono okna Epic Games Launcher.");

            log("Epic Games: znaleziono okno.");

            if (!EnsureWindowForeground(hwnd))
                return Fail(out error,
                    "Nie udało się uaktywnić okna Epic Games. Klawiatura nie została wysłana, aby nie trafiła do innego programu.");

            // Allow the embedded WebView to create its initial keyboard focus.
            // This is intentionally small and fixed: it is stabilization, not
            // a guess about screen coordinates or UI element timing.
            Thread.Sleep(900);

            if (!EnsureWindowForeground(hwnd))
                return Fail(out error,
                    "Okno Epic Games utraciło fokus przed rozpoczęciem logowania.");

            log("Epic Games: okno aktywne. Rozpoczynam potwierdzoną sekwencję.");

            // User-verified sequence:
            // TAB -> email -> TAB TAB ENTER
            // TAB TAB -> password -> TAB TAB TAB TAB ENTER

            if (!SendTab(hwnd, "pierwszy TAB do pola e-mail"))
                return Fail(out error, "Nie udało się wysłać pierwszego TAB.");

            if (!TypeTextWithForeground(username, hwnd, "e-mail"))
                return Fail(out error, "Nie udało się wpisać e-maila Epic Games.");

            if (!SendTabs(hwnd, 2, "przejście do Kontynuuj"))
                return Fail(out error, "Nie udało się wykonać TAB TAB przed Kontynuuj.");

            if (!SendEnter(hwnd, "zatwierdzenie e-maila"))
                return Fail(out error, "Nie udało się zatwierdzić adresu e-mail Epic Games.");

            if (!WaitForWindowAfterTransition(hwnd, 700, 10000))
                return Fail(out error,
                    "Epic Games nie przeszedł do kolejnego etapu logowania.");

            if (!SendTabs(hwnd, 2, "przejście do pola hasła"))
                return Fail(out error, "Nie udało się wykonać TAB TAB do pola hasła.");

            if (!TypeTextWithForeground(password, hwnd, "hasło"))
                return Fail(out error, "Nie udało się wpisać hasła Epic Games.");

            if (!SendTabs(hwnd, 4, "przejście do Zaloguj się"))
                return Fail(out error, "Nie udało się wykonać czterech TAB przed logowaniem.");

            if (!SendEnter(hwnd, "zatwierdzenie logowania"))
                return Fail(out error, "Nie udało się zatwierdzić logowania Epic Games.");

            log("Epic Games: kompletna sekwencja klawiatury została wysłana.");
            return true;
        }

        private bool SendTab(IntPtr hwnd, string purpose)
        {
            if (!EnsureWindowForeground(hwnd))
                return false;

            if (!NativeKeyboardInput.SendTab(log))
                return false;

            log("Epic Games keyboard: [TAB] -> " + purpose + ".");
            Thread.Sleep(140);
            return true;
        }

        private bool SendTabs(IntPtr hwnd, int count, string purpose)
        {
            for (int i = 0; i < count; i++)
            {
                if (!SendTab(hwnd, "nawigacja " + (i + 1) + "/" + count))
                    return false;
            }

            log("Epic Games keyboard: wysłano " + count + "x [TAB] -> " + purpose + ".");
            Thread.Sleep(160);
            return true;
        }

        private bool SendEnter(IntPtr hwnd, string purpose)
        {
            if (!EnsureWindowForeground(hwnd))
                return false;

            if (!NativeKeyboardInput.SendEnter(log))
                return false;

            log("Epic Games keyboard: [ENTER] -> " + purpose + ".");
            Thread.Sleep(220);
            return true;
        }

        private bool TypeTextWithForeground(string text, IntPtr hwnd, string description)
        {
            if (!EnsureWindowForeground(hwnd))
                return false;

            Thread.Sleep(100);

            if (!NativeKeyboardInput.TypeText(text, hwnd, log))
                return false;

            log("Epic Games keyboard: wpisano " + description + ".");
            Thread.Sleep(150);
            return true;
        }

        private bool WaitForWindowAfterTransition(
            IntPtr hwnd, int initialDelayMs, int timeoutMs)
        {
            Thread.Sleep(initialDelayMs);

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(
                Math.Max(1000, timeoutMs));

            while (DateTime.UtcNow < deadline)
            {
                IntPtr current = FindMainWindowHandle();

                if (current != IntPtr.Zero &&
                    IsWindowVisible(current) &&
                    EnsureWindowForeground(current))
                {
                    Thread.Sleep(250);
                    return true;
                }

                Thread.Sleep(160);
            }

            return false;
        }

        public static IntPtr FindMainWindowHandlePublic()
        {
            return FindMainWindowHandle();
        }

        private static IntPtr WaitForMainWindow(int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(
                Math.Max(1, timeoutSeconds));

            while (DateTime.UtcNow < deadline)
            {
                IntPtr hwnd = FindMainWindowHandle();

                if (hwnd != IntPtr.Zero)
                    return hwnd;

                Thread.Sleep(100);
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindMainWindowHandle()
        {
            foreach (Process process in SafeGetProcesses("EpicGamesLauncher"))
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    IntPtr hwnd = process.MainWindowHandle;

                    if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
                        return hwnd;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            return IntPtr.Zero;
        }

        private bool EnsureWindowForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                ShowWindow(hwnd, SW_RESTORE);

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    if (GetForegroundWindow() == hwnd)
                    {
                        SetActiveWindow(hwnd);
                        return true;
                    }

                    IntPtr foreground = GetForegroundWindow();

                    uint currentThread = GetCurrentThreadId();
                    uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                    uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);

                    bool attachedForeground = false;
                    bool attachedTarget = false;

                    try
                    {
                        if (foregroundThread != 0 &&
                            foregroundThread != currentThread)
                        {
                            attachedForeground = AttachThreadInput(
                                currentThread, foregroundThread, true);
                        }

                        if (targetThread != 0 &&
                            targetThread != currentThread)
                        {
                            attachedTarget = AttachThreadInput(
                                currentThread, targetThread, true);
                        }

                        BringWindowToTop(hwnd);
                        SetForegroundWindow(hwnd);
                        SetActiveWindow(hwnd);
                        SetFocus(hwnd);
                    }
                    finally
                    {
                        if (targetThread != 0 &&
                            targetThread != currentThread &&
                            attachedTarget)
                        {
                            AttachThreadInput(
                                currentThread, targetThread, false);
                        }

                        if (foregroundThread != 0 &&
                            foregroundThread != currentThread &&
                            attachedForeground)
                        {
                            AttachThreadInput(
                                currentThread, foregroundThread, false);
                        }
                    }

                    Thread.Sleep(120);

                    if (GetForegroundWindow() == hwnd)
                    {
                        SetActiveWindow(hwnd);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                log("Epic Games INPUT: błąd aktywacji okna: " + ex.Message);
            }

            return GetForegroundWindow() == hwnd;
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

        private static bool Fail(out string error, string message)
        {
            error = message;
            return false;
        }

        [DllImport("user32.dll", SetLastError = true)]
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
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(
            IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(
            uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
