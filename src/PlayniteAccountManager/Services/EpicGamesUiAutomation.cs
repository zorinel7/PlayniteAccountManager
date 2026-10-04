using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using AutomationCondition = System.Windows.Automation.Condition;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Epic Games login automation.
    ///
    /// Epic's WebView does not expose a reliable HTML/UIA control tree on all
    /// launcher builds. The confirmed working manual keyboard sequence is
    /// therefore reproduced directly. No screen coordinates or blind UIA
    /// focus traversal are used for the login form.
    /// </summary>
    internal sealed class EpicGamesUiAutomation
    {
        private readonly Action<string> log;
        private const int SW_RESTORE = 9;

        public EpicGamesUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool Login(string username, string password, int timeoutSeconds, out string error)
        {
            error = null;

            IntPtr hwnd = WaitForMainWindow(timeoutSeconds);
            if (hwnd == IntPtr.Zero)
                return Fail(out error, "Nie znaleziono okna Epic Games Launcher.");

            EnsureForeground(hwnd);

            // Wait for the login WebView to actually finish loading. We do not
            // send the first TAB while the launcher is still rendering.
            if (!WaitForLoginSurfaceReady(hwnd, 20))
                return Fail(out error,
                    "Epic Games Launcher został uruchomiony, ale ekran logowania nie był gotowy w ciągu 20 sekund.");

            log("Epic Games: ekran logowania gotowy.");
            log("Epic Games keyboard: używam potwierdzonej sekwencji użytkownika.");

            // Confirmed sequence:
            // [TAB]
            // type email
            // [TAB] [TAB] [ENTER]
            // wait for password page
            // [TAB] [TAB]
            // type password
            // [TAB] [TAB] [TAB] [TAB] [ENTER]

            if (!SendTab("fokus pola e-mail"))
                return Fail(out error, "Nie udało się wysłać pierwszego TAB do pola e-mail.");

            if (!NativeKeyboardInput.TypeText(username, hwnd, log))
                return Fail(out error, "Nie udało się wpisać e-maila Epic Games.");

            if (!SendTabs(2, "przejście do „Kontynuuj”"))
                return Fail(out error, "Nie udało się przejść do przycisku „Kontynuuj”.");

            Thread.Sleep(120);
            if (!NativeKeyboardInput.SendEnter(log))
                return Fail(out error, "Nie udało się zatwierdzić adresu e-mail Epic Games.");

            // Wait for the password page rather than assuming a fixed delay.
            if (!WaitForPasswordSurfaceReady(hwnd, 15))
                return Fail(out error,
                    "Epic Games nie załadował ekranu hasła w wyznaczonym czasie.");

            log("Epic Games: ekran hasła gotowy.");

            if (!SendTabs(2, "fokus pola hasła"))
                return Fail(out error, "Nie udało się ustawić fokusu pola hasła.");

            if (!NativeKeyboardInput.TypeText(password, hwnd, log))
                return Fail(out error, "Nie udało się wpisać hasła Epic Games.");

            if (!SendTabs(4, "przejście do „Zaloguj się”"))
                return Fail(out error, "Nie udało się przejść do przycisku „Zaloguj się”.");

            Thread.Sleep(120);
            if (!NativeKeyboardInput.SendEnter(log))
                return Fail(out error, "Nie udało się zatwierdzić logowania Epic Games.");

            // Give the post-login WebView a moment to transition.
            Thread.Sleep(300);

            // If Epic displays the 2FA setup screen, always choose the
            // semantically named "Ustaw później" button. No fixed coordinates.
            WaitAndClickLater2FA(hwnd, 15);

            return true;
        }

        public static IntPtr FindMainWindowHandlePublic()
        {
            return FindMainWindowHandle();
        }

        private bool SendTab(string purpose)
        {
            if (!NativeKeyboardInput.SendTab(log))
                return false;

            log("Epic Games keyboard: [TAB] -> " + purpose + ".");
            Thread.Sleep(110);
            return true;
        }

        private bool SendTabs(int count, string purpose)
        {
            for (int i = 0; i < count; i++)
            {
                if (!NativeKeyboardInput.SendTab(log))
                    return false;

                Thread.Sleep(100);
            }

            log("Epic Games keyboard: wysłano " + count + "x [TAB] -> " + purpose + ".");
            return true;
        }

        private bool WaitForLoginSurfaceReady(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));

            while (DateTime.UtcNow < deadline)
            {
                hwnd = FindMainWindowHandle();

                if (hwnd != IntPtr.Zero)
                {
                    if (IsTextVisible(hwnd, "Zaloguj się do Epic Games") ||
                        IsTextVisible(hwnd, "Adres e-mail") ||
                        IsTextVisible(hwnd, "Kontynuuj"))
                    {
                        EnsureForeground(hwnd);
                        return true;
                    }
                }

                Thread.Sleep(180);
            }

            return false;
        }

        private bool WaitForPasswordSurfaceReady(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));

            while (DateTime.UtcNow < deadline)
            {
                hwnd = FindMainWindowHandle();

                if (hwnd != IntPtr.Zero)
                {
                    if (IsTextVisible(hwnd, "Hasło") ||
                        IsTextVisible(hwnd, "Wprowadź hasło") ||
                        IsTextVisible(hwnd, "Password"))
                    {
                        EnsureForeground(hwnd);
                        return true;
                    }
                }

                Thread.Sleep(180);
            }

            return false;
        }

        private bool WaitAndClickLater2FA(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));

            while (DateTime.UtcNow < deadline)
            {
                hwnd = FindMainWindowHandle();

                if (hwnd == IntPtr.Zero)
                {
                    Thread.Sleep(150);
                    continue;
                }

                AutomationElement button = FindNamedElement(
                    hwnd,
                    new[] { "Ustaw później", "Set up later", "Do this later" });

                if (button != null)
                {
                    if (TryInvoke(button))
                    {
                        log("Epic Games UIA: kliknięto „Ustaw później” dla 2EL.");
                        return true;
                    }

                    if (ClickElementCenter(button, "„Ustaw później” 2EL"))
                    {
                        log("Epic Games hybrid: kliknięto „Ustaw później” dla 2EL.");
                        return true;
                    }
                }

                Thread.Sleep(150);
            }

            log("Epic Games: ekran „Ustaw później” 2EL nie wystąpił.");
            return false;
        }

        private static AutomationElement FindNamedElement(
            IntPtr hwnd, string[] names)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return null;

                foreach (AutomationElement e in root.FindAll(
                    TreeScope.Descendants,
                    AutomationCondition.TrueCondition))
                {
                    try
                    {
                        if (e.Current.IsOffscreen || !e.Current.IsEnabled)
                            continue;

                        string name = e.Current.Name ?? string.Empty;

                        if (names.Any(n =>
                            name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                            return e;
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        private bool ClickElementCenter(
            AutomationElement element, string description)
        {
            try
            {
                Rect rect = element.Current.BoundingRectangle;

                if (rect.Width <= 0 || rect.Height <= 0)
                    return false;

                int x = (int)Math.Round(rect.Left + rect.Width / 2.0);
                int y = (int)Math.Round(rect.Top + rect.Height / 2.0);

                if (!SetCursorPos(x, y))
                    return false;

                Thread.Sleep(35);

                INPUT[] inputs =
                {
                    CreateMouseInput(0x0002),
                    CreateMouseInput(0x0004)
                };

                uint sent = SendInput(
                    (uint)inputs.Length,
                    inputs,
                    Marshal.SizeOf(typeof(INPUT)));

                if (sent != inputs.Length)
                    return false;

                Thread.Sleep(100);
                log("Epic Games hybrid click: " + description + ".");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInvoke(AutomationElement element)
        {
            try
            {
                var invoke = (InvokePattern)element.GetCurrentPattern(
                    InvokePattern.Pattern);

                invoke.Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTextVisible(IntPtr hwnd, string text)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                    return false;

                foreach (AutomationElement e in root.FindAll(
                    TreeScope.Descendants,
                    AutomationCondition.TrueCondition))
                {
                    try
                    {
                        if (e.Current.IsOffscreen)
                            continue;

                        string name = e.Current.Name ?? string.Empty;

                        if (name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                    catch { }
                }
            }
            catch { }

            return false;
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
                    if (!process.HasExited &&
                        IsWindowVisible(process.MainWindowHandle))
                        return process.MainWindowHandle;
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }

            return IntPtr.Zero;
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

        private static bool EnsureForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                ShowWindow(hwnd, SW_RESTORE);

                for (int i = 0; i < 5; i++)
                {
                    if (GetForegroundWindow() == hwnd)
                        return true;

                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(60);
                }

                return GetForegroundWindow() == hwnd;
            }
            catch
            {
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
                        dwFlags = flags
                    }
                }
            };
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
            public KEYBDINPUT ki;

            [FieldOffset(0)]
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint nInputs, [In] INPUT[] pInputs, int cbSize);

        private static bool Fail(out string error, string message)
        {
            error = message;
            return false;
        }
    }
}
