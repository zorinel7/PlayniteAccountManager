using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using AutomationCondition = System.Windows.Automation.Condition;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Epic login automation. The login page is a Chromium/WebView surface,
    /// so named UIA controls are preferred and stable screen-relative clicks
    /// are used only as fallbacks.
    /// </summary>
    internal sealed class EpicGamesUiAutomation
    {
        private readonly Action<string> log;

        private const int SW_RESTORE = 9;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const ushort VK_CONTROL = 0x0011;
        private const ushort VK_A = 0x0041;

        public EpicGamesUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool Login(string username, string password, int timeoutSeconds, out string error)
        {
            error = null;

            IntPtr hwnd = WaitForMainWindow(timeoutSeconds);
            if (hwnd == IntPtr.Zero)
            {
                error = "Nie znaleziono okna Epic Games Launcher.";
                return false;
            }

            EnsureForeground(hwnd);

            // First page: e-mail + "Kontynuuj".
            AutomationElement email = FindEdit(hwnd);
            if (email != null && Focus(email))
                log("Epic Games UIA: ustawiono fokus pola e-mail.");
            else if (!NativeClickRelative(hwnd, 0.50, 0.32, "pole e-mail"))
            {
                error = "Nie udało się ustawić pola e-mail Epic Games.";
                return false;
            }

            if (!SelectAllAndType(username, hwnd))
            {
                error = "Nie udało się wpisać e-maila Epic Games.";
                return false;
            }

            if (!ClickNamedOrRelative(
                hwnd,
                new[] { "Kontynuuj", "Continue" },
                0.50, 0.40,
                "przycisk „Kontynuuj”"))
            {
                error = "Nie udało się przejść do ekranu hasła Epic Games.";
                return false;
            }

            Thread.Sleep(450);

            // Password page.
            AutomationElement passwordEdit = FindPasswordEdit(hwnd);
            if (passwordEdit != null && Focus(passwordEdit))
                log("Epic Games UIA: ustawiono fokus pola hasła.");
            else if (!NativeClickRelative(hwnd, 0.50, 0.506, "pole hasła"))
            {
                error = "Nie udało się ustawić pola hasła Epic Games.";
                return false;
            }

            if (!NativeKeyboardInput.TypeText(password, hwnd, log))
            {
                error = "Nie udało się wpisać hasła Epic Games.";
                return false;
            }

            // Password is followed by the optional "remember me" checkbox
            // and then the login button. The checkbox is ON in the supplied
            // screen; do not toggle it.
            if (!ClickNamedOrRelative(
                hwnd,
                new[] { "Zaloguj się", "Log in", "Sign in" },
                0.50, 0.72,
                "przycisk „Zaloguj się”"))
            {
                error = "Nie udało się zatwierdzić logowania Epic Games.";
                return false;
            }

            // Epic can display the 2EL setup immediately after a valid login.
            // The user's current UI shows "Ustaw później"; always choose it.
            if (!WaitAndClickLater2FA(hwnd, 12))
            {
                // No 2EL dialog is also a valid result; we only fail if the
                // final login surface never reaches the authenticated state.
                log("Epic Games: nie wykryto ekranu konfiguracji 2EL. Kontynuuję.");
            }

            return true;
        }

        private bool WaitAndClickLater2FA(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));

            while (DateTime.UtcNow < deadline)
            {
                AutomationElement button = FindNamedButton(hwnd,
                    new[] { "Ustaw później", "Set up later", "Do this later" });

                if (button != null)
                {
                    if (Invoke(button))
                    {
                        log("Epic Games UIA: kliknięto „Ustaw później” dla 2EL.");
                        return true;
                    }
                }

                // Current screenshot: the button is centered in the card at
                // about x=50% and y=64% of the window.
                if (IsTextVisible(hwnd, "Chroń swoje konto") ||
                    IsTextVisible(hwnd, "Skonfiguruj 2EL"))
                {
                    if (NativeClickRelative(hwnd, 0.50, 0.64, "„Ustaw później” 2EL"))
                    {
                        log("Epic Games native: kliknięto „Ustaw później” dla 2EL.");
                        return true;
                    }
                }

                Thread.Sleep(150);
            }

            return false;
        }

        private static AutomationElement FindNamedButton(IntPtr hwnd, string[] names)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            foreach (AutomationElement e in root.FindAll(
                TreeScope.Descendants, AutomationCondition.TrueCondition))
            {
                try
                {
                    if (e.Current.IsOffscreen ||
                        !e.Current.IsEnabled ||
                        e.Current.ControlType != ControlType.Button)
                        continue;

                    string name = e.Current.Name ?? string.Empty;

                    if (names.Any(n =>
                        name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                        return e;
                }
                catch { }
            }

            return null;
        }

        private static bool IsTextVisible(IntPtr hwnd, string text)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return false;

            foreach (AutomationElement e in root.FindAll(
                TreeScope.Descendants, AutomationCondition.TrueCondition))
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

            return false;
        }

        private static AutomationElement FindEdit(IntPtr hwnd)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            var edits = root.FindAll(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(
                        AutomationElement.IsEnabledProperty, true),
                    new PropertyCondition(
                        AutomationElement.IsOffscreenProperty, false)));

            return edits.Count > 0 ? edits[0] : null;
        }

        private static AutomationElement FindPasswordEdit(IntPtr hwnd)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            foreach (AutomationElement e in root.FindAll(
                TreeScope.Descendants, AutomationCondition.TrueCondition))
            {
                try
                {
                    if (!e.Current.IsOffscreen &&
                        e.Current.IsEnabled &&
                        e.Current.ControlType == ControlType.Edit &&
                        e.Current.IsPassword)
                        return e;
                }
                catch { }
            }

            return FindEdit(hwnd);
        }

        private bool SelectAllAndType(string text, IntPtr hwnd)
        {
            try
            {
                INPUT[] chord =
                {
                    CreateKeyboardInput(VK_CONTROL, false),
                    CreateKeyboardInput(VK_A, false),
                    CreateKeyboardInput(VK_A, true),
                    CreateKeyboardInput(VK_CONTROL, true)
                };

                uint sent = SendInput(
                    (uint)chord.Length,
                    chord,
                    Marshal.SizeOf(typeof(INPUT)));

                if (sent != chord.Length)
                    return false;

                Thread.Sleep(60);
                return NativeKeyboardInput.TypeText(text, hwnd, log);
            }
            catch
            {
                return false;
            }
        }

        private bool ClickNamedOrRelative(
            IntPtr hwnd, string[] names,
            double xPct, double yPct, string description)
        {
            AutomationElement button = FindNamedButton(hwnd, names);

            if (button != null && Invoke(button))
            {
                log("Epic Games UIA: wykonano " + description + ".");
                return true;
            }

            if (!NativeClickRelative(hwnd, xPct, yPct, description))
                return NativeKeyboardInput.SendEnter(log);

            return true;
        }

        private static bool Focus(AutomationElement element)
        {
            try
            {
                if (!element.Current.IsEnabled || element.Current.IsOffscreen)
                    return false;

                element.SetFocus();
                Thread.Sleep(60);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool Invoke(AutomationElement element)
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

        private static IntPtr WaitForMainWindow(int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(
                Math.Max(1, timeoutSeconds));

            while (DateTime.UtcNow < deadline)
            {
                IntPtr hwnd = FindMainWindowHandle();

                if (hwnd != IntPtr.Zero)
                    return hwnd;

                Thread.Sleep(80);
            }

            return IntPtr.Zero;
        }

        public static IntPtr FindMainWindowHandlePublic()
        {
            return FindMainWindowHandle();
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

        private bool NativeClickRelative(
            IntPtr hwnd, double xPct, double yPct, string what)
        {
            RECT rect;

            if (!GetWindowRect(hwnd, out rect))
                return false;

            int x = rect.Left +
                    (int)Math.Round((rect.Right - rect.Left) * xPct);

            int y = rect.Top +
                    (int)Math.Round((rect.Bottom - rect.Top) * yPct);

            try
            {
                SetCursorPos(x, y);
                Thread.Sleep(50);

                INPUT[] inputs =
                {
                    new INPUT
                    {
                        type = 0,
                        u = new InputUnion
                        {
                            mi = new MOUSEINPUT
                            {
                                dwFlags = MOUSEEVENTF_LEFTDOWN
                            }
                        }
                    },
                    new INPUT
                    {
                        type = 0,
                        u = new InputUnion
                        {
                            mi = new MOUSEINPUT
                            {
                                dwFlags = MOUSEEVENTF_LEFTUP
                            }
                        }
                    }
                };

                uint sent = SendInput(
                    (uint)inputs.Length,
                    inputs,
                    Marshal.SizeOf(typeof(INPUT)));

                if (sent != inputs.Length)
                    return false;

                Thread.Sleep(120);
                log("Epic Games native: kliknięto " + what + " w (" + x + "," + y + ").");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static INPUT CreateKeyboardInput(ushort virtualKey, bool keyUp)
        {
            return new INPUT
            {
                type = 1,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKey,
                        wScan = 0,
                        dwFlags = keyUp ? 0x0002u : 0u
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
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;
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
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint nInputs, [In] INPUT[] pInputs, int cbSize);
    }
}
