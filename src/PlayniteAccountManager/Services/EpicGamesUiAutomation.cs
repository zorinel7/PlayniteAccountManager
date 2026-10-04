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
    /// so semantic UIA controls are preferred and keyboard focus navigation
    /// is used as the fallback. No screen coordinates are used.
    /// </summary>
    internal sealed class EpicGamesUiAutomation
    {
        private readonly Action<string> log;

        private const int SW_RESTORE = 9;
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

            // First page: locate the actual editable control through UIA.
            AutomationElement email = FindEditableElement(hwnd, false, 8);
            if (email == null)
            {
                error = "Nie znaleziono pola e-mail Epic Games.";
                return false;
            }

            if (!Focus(email))
            {
                error = "Nie udało się ustawić fokusu pola e-mail Epic Games.";
                return false;
            }

            log("Epic Games UIA: ustawiono fokus pola e-mail.");

            if (!SelectAllAndType(username, hwnd))
            {
                error = "Nie udało się wpisać e-maila Epic Games.";
                return false;
            }

            // No screen coordinates. If the button is exposed by UIA, invoke
            // it by its semantic name. Otherwise navigate keyboard focus until
            // the focused control itself is "Kontynuuj".
            if (!InvokeNamedOrTab(hwnd,
                new[] { "Kontynuuj", "Continue" }, 12,
                "przycisk „Kontynuuj”"))
            {
                error = "Nie udało się przejść do ekranu hasła Epic Games.";
                return false;
            }

            Thread.Sleep(450);

            AutomationElement passwordEdit = FindEditableElement(hwnd, true, 12);
            if (passwordEdit == null || !Focus(passwordEdit))
            {
                error = "Nie znaleziono pola hasła Epic Games.";
                return false;
            }

            log("Epic Games UIA: ustawiono fokus pola hasła.");

            if (!NativeKeyboardInput.TypeText(password, hwnd, log))
            {
                error = "Nie udało się wpisać hasła Epic Games.";
                return false;
            }

            if (!InvokeNamedOrTab(hwnd,
                new[] { "Zaloguj się", "Zaloguj", "Log in", "Sign in" }, 16,
                "przycisk „Zaloguj się”"))
            {
                error = "Nie udało się zatwierdzić logowania Epic Games.";
                return false;
            }

            // Epic may immediately ask to configure 2FA. When that screen is
            // shown, always choose "Ustaw później" by semantic name/focus,
            // never by screen coordinates.
            if (!WaitAndInvokeLater2FA(hwnd, 15))
                log("Epic Games: ekran konfiguracji 2EL nie został wykryty. Kontynuuję.");

            return true;
        }

        private bool WaitAndInvokeLater2FA(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));

            while (DateTime.UtcNow < deadline)
            {
                if (IsTextVisible(hwnd, "Chroń swoje konto") ||
                    IsTextVisible(hwnd, "Skonfiguruj 2EL") ||
                    FindNamedInvokable(hwnd, new[] { "Ustaw później", "Set up later", "Do this later" }) != null)
                {
                    if (InvokeNamedOrTab(hwnd,
                        new[] { "Ustaw później", "Set up later", "Do this later" }, 20,
                        "przycisk „Ustaw później” 2EL"))
                    {
                        log("Epic Games UIA/keyboard: wybrano „Ustaw później” dla 2EL.");
                        return true;
                    }

                    // Keep polling the semantic UI instead of clicking a guessed
                    // position. This also handles a delayed WebView accessibility
                    // tree after successful authentication.
                }

                Thread.Sleep(120);
            }

            return false;
        }

        private bool InvokeNamedOrTab(
            IntPtr hwnd, string[] names, int maxTabs, string description)
        {
            AutomationElement element = FindNamedInvokable(hwnd, names);
            if (element != null && Invoke(element))
            {
                log("Epic Games UIA: wykonano " + description + ".");
                return true;
            }

            for (int i = 0; i < maxTabs; i++)
            {
                if (GetForegroundWindow() != hwnd)
                    EnsureForeground(hwnd);

                AutomationElement focused = null;
                try { focused = AutomationElement.FocusedElement; } catch { }

                if (focused != null && ElementNameMatches(focused, names))
                {
                    if (Invoke(focused))
                    {
                        log("Epic Games UIA/keyboard: wykonano " + description +
                            " po nawigacji Tab (" + (i + 1) + ").");
                        return true;
                    }

                    // Some WebView controls do not expose InvokePattern but do
                    // respond to Enter when they have keyboard focus.
                    if (NativeKeyboardInput.SendEnter(log))
                    {
                        log("Epic Games keyboard: wykonano " + description +
                            " przez Enter.");
                        return true;
                    }
                }

                if (!NativeKeyboardInput.SendTab(log))
                    return false;

                Thread.Sleep(80);
            }

            // One final semantic UIA scan after the tab traversal.
            element = FindNamedInvokable(hwnd, names);
            if (element != null && Invoke(element))
            {
                log("Epic Games UIA: wykonano " + description + " po ponownym skanowaniu.");
                return true;
            }

            return false;
        }

        private static bool ElementNameMatches(AutomationElement element, string[] names)
        {
            try
            {
                if (element == null || element.Current.IsOffscreen || !element.Current.IsEnabled)
                    return false;

                string name = element.Current.Name ?? string.Empty;
                return names.Any(n =>
                    name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return false;
            }
        }

        private static AutomationElement FindNamedInvokable(IntPtr hwnd, string[] names)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            foreach (AutomationElement e in root.FindAll(
                TreeScope.Descendants, AutomationCondition.TrueCondition))
            {
                try
                {
                    if (e.Current.IsOffscreen || !e.Current.IsEnabled)
                        continue;

                    if (!ElementNameMatches(e, names))
                        continue;

                    // Prefer the named element itself, then walk up to a
                    // clickable/invokable parent. No geometry is involved.
                    AutomationElement current = e;
                    for (int i = 0; current != null && i < 6; i++)
                    {
                        if (Invoke(current))
                            return current;

                        current = SafeParent(current);
                    }
                }
                catch { }
            }

            return null;
        }

        private static AutomationElement FindEditableElement(
            IntPtr hwnd, bool password, int maxTabs)
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
                        e.Current.ControlType != ControlType.Edit)
                        continue;

                    if (password && !e.Current.IsPassword)
                        continue;

                    if (!password && e.Current.IsPassword)
                        continue;

                    return e;
                }
                catch { }
            }

            // Some Chromium inputs are exposed only when they receive focus.
            // Navigate through focusable elements instead of using coordinates.
            for (int i = 0; i < maxTabs; i++)
            {
                AutomationElement focused = null;
                try { focused = AutomationElement.FocusedElement; } catch { }

                try
                {
                    if (focused != null &&
                        focused.Current.IsEnabled &&
                        !focused.Current.IsOffscreen &&
                        focused.Current.ControlType == ControlType.Edit &&
                        (!password || focused.Current.IsPassword) &&
                        (password || !focused.Current.IsPassword))
                        return focused;
                }
                catch { }

                if (!NativeKeyboardInput.SendTab(log))
                    break;

                Thread.Sleep(80);
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

        private static AutomationElement SafeParent(AutomationElement element)
        {
            try
            {
                return TreeWalker.RawViewWalker.GetParent(element);
            }
            catch
            {
                return null;
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
    }
}
