using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using DrawingSize = System.Drawing.Size;
using DrawingPoint = System.Drawing.Point;
using System.Drawing.Imaging;
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
                log("Epic Games UIA: pole e-mail nie jest dostępne. Używam dynamicznego wykrycia niebieskiego przycisku.");

                AutomationElement continueButton = FindNamedElement(
                    hwnd, new[] { "Kontynuuj", "Continue" });

                bool clicked = continueButton != null &&
                               ClickElementCenter(continueButton, "przycisk „Kontynuuj”");

                if (!clicked)
                    clicked = TryClickVisualBlueButton(hwnd, "przycisk „Kontynuuj”");

                if (!clicked)
                {
                    error = "Nie udało się odnaleźć przycisku „Kontynuuj” Epic Games.";
                    return false;
                }

                // The clicked button is the actual current UI element, so
                // Shift+Tab moves to the form input regardless of DPI/resolution.
                if (!FocusPreviousEditable(hwnd, false, 8))
                {
                    error = "Nie udało się ustawić fokusu pola e-mail Epic Games.";
                    return false;
                }

                email = GetFocusedElement();
                if (email == null)
                {
                    error = "Nie udało się ustawić fokusu pola e-mail Epic Games.";
                    return false;
                }
            }
            else if (!TryFocusTarget(email))
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

            if (passwordEdit == null)
            {
                log("Epic Games UIA: pole hasła nie jest dostępne. Używam dynamicznego wykrycia niebieskiego przycisku.");

                AutomationElement loginButtonAnchor = FindNamedElement(
                    hwnd, new[] { "Zaloguj się", "Zaloguj", "Log in", "Sign in" });

                bool clicked = loginButtonAnchor != null &&
                               ClickElementCenter(loginButtonAnchor, "przycisk „Zaloguj się”");

                if (!clicked)
                    clicked = TryClickVisualBlueButton(hwnd, "przycisk „Zaloguj się”");

                if (!clicked)
                {
                    error = "Nie udało się odnaleźć przycisku „Zaloguj się” Epic Games.";
                    return false;
                }

                // Password form order: password -> recovery link -> remember-me
                // checkbox -> login button. Three reverse-tab steps return to it.
                if (!FocusPreviousEditable(hwnd, true, 8))
                {
                    error = "Nie udało się ustawić fokusu pola hasła Epic Games.";
                    return false;
                }

                passwordEdit = GetFocusedElement();
                if (passwordEdit == null)
                {
                    error = "Nie udało się ustawić fokusu pola hasła Epic Games.";
                    return false;
                }
            }
            else if (!TryFocusTarget(passwordEdit))
            {
                error = "Nie udało się ustawić fokusu pola hasła Epic Games.";
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
            AutomationElement element = FindNamedElement(hwnd, names);

            if (element != null)
            {
                if (TryInvoke(element))
                {
                    log("Epic Games UIA: wykonano " + description + ".");
                    return true;
                }

                // UIA found the exact semantic element but its WebView does not
                // expose InvokePattern. Use the element's CURRENT bounding
                // rectangle. This is dynamic screen geometry, not a hardcoded
                // coordinate, so DPI/resolution/window position do not matter.
                if (ClickElementCenter(element, description))
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
                    if (TryInvoke(focused))
                    {
                        log("Epic Games UIA/keyboard: wykonano " + description +
                            " po nawigacji Tab (" + (i + 1) + ").");
                        return true;
                    }

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

            element = FindNamedElement(hwnd, names);
            if (element != null && TryInvoke(element))
            {
                log("Epic Games UIA: wykonano " + description + " po ponownym skanowaniu.");
                return true;
            }

            return element != null && ClickElementCenter(element, description);
        }

        private static AutomationElement FindNamedElement(
            IntPtr hwnd, string[] names)
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

                    if (ElementNameMatches(e, names))
                        return e;
                }
                catch { }
            }

            return null;
        }

        private bool TryClickVisualBlueButton(IntPtr hwnd, string description)
        {
            try
            {
                RECT windowRect;
                if (!GetWindowRect(hwnd, out windowRect))
                    return false;

                int width = windowRect.Right - windowRect.Left;
                int height = windowRect.Bottom - windowRect.Top;

                if (width < 300 || height < 300)
                    return false;

                using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(
                            windowRect.Left,
                            windowRect.Top,
                            0,
                            0,
                            new DrawingSize(width, height),
                            CopyPixelOperation.SourceCopy);
                    }

                    Rectangle candidate = FindLargeEpicBlueButton(bitmap);
                    if (candidate == Rectangle.Empty)
                        return false;

                    int x = windowRect.Left + candidate.Left + candidate.Width / 2;
                    int y = windowRect.Top + candidate.Top + candidate.Height / 2;

                    if (!NativeClickScreen(x, y, description))
                        return false;

                    log("Epic Games hybrid visual click: " + description +
                        " @ dynamic rect (" + candidate.Left + "," +
                        candidate.Top + "," + candidate.Width + "," +
                        candidate.Height + ").");

                    return true;
                }
            }
            catch (Exception ex)
            {
                log("Epic Games visual fallback: " + ex.Message);
                return false;
            }
        }

        private static Rectangle FindLargeEpicBlueButton(Bitmap bitmap)
        {
            const int sample = 3;

            int sw = bitmap.Width / sample;
            int sh = bitmap.Height / sample;

            if (sw <= 0 || sh <= 0)
                return Rectangle.Empty;

            var mask = new bool[sw, sh];
            var visited = new bool[sw, sh];

            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    Color color = bitmap.GetPixel(x * sample, y * sample);

                    // Epic's primary action buttons are bright cyan/blue.
                    mask[x, y] =
                        color.R < 120 &&
                        color.G > 120 &&
                        color.B > 165 &&
                        color.B > color.G + 10 &&
                        color.G > color.R + 45;
                }
            }

            Rectangle best = Rectangle.Empty;
            int bestArea = 0;

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    if (!mask[x, y] || visited[x, y])
                        continue;

                    var queue = new Queue<DrawingPoint>();
                    queue.Enqueue(new DrawingPoint(x, y));
                    visited[x, y] = true;

                    int minX = x;
                    int maxX = x;
                    int minY = y;
                    int maxY = y;
                    int pixels = 0;

                    while (queue.Count > 0)
                    {
                        DrawingPoint p = queue.Dequeue();
                        pixels++;

                        minX = Math.Min(minX, p.X);
                        maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y);
                        maxY = Math.Max(maxY, p.Y);

                        for (int i = 0; i < 4; i++)
                        {
                            int nx = p.X + dx[i];
                            int ny = p.Y + dy[i];

                            if (nx < 0 || nx >= sw ||
                                ny < 0 || ny >= sh ||
                                visited[nx, ny] ||
                                !mask[nx, ny])
                                continue;

                            visited[nx, ny] = true;
                            queue.Enqueue(new Point(nx, ny));
                        }
                    }

                    int rw = maxX - minX + 1;
                    int rh = maxY - minY + 1;
                    double ratio = rh > 0 ? (double)rw / rh : 0;
                    int area = rw * rh;

                    if (pixels < 350 ||
                        rw < 70 ||
                        rh < 10 ||
                        ratio < 2.5 ||
                        ratio > 15.0)
                        continue;

                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = new Rectangle(
                            minX * sample,
                            minY * sample,
                            rw * sample,
                            rh * sample);
                    }
                }
            }

            return best;
        }

        private bool NativeClickScreen(int x, int y, string description)
        {
            try
            {
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

                Thread.Sleep(120);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static AutomationElement GetFocusedElement()
        {
            try
            {
                return AutomationElement.FocusedElement;
            }
            catch
            {
                return null;
            }
        }

        private bool ClickElementCenter(
            AutomationElement element, string description)
        {
            try
            {
                Rect rect = element.Current.BoundingRectangle;

                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    // The named element can be a text node with no geometry;
                    // walk up to the nearest visible ancestor with a real rect.
                    AutomationElement current = SafeParent(element);

                    for (int i = 0; current != null && i < 8; i++)
                    {
                        try
                        {
                            rect = current.Current.BoundingRectangle;
                            if (rect.Width > 0 && rect.Height > 0 &&
                                !current.Current.IsOffscreen &&
                                current.Current.IsEnabled)
                            {
                                element = current;
                                break;
                            }
                        }
                        catch { }

                        current = SafeParent(current);
                    }
                }

                if (rect.Width <= 0 || rect.Height <= 0)
                    return false;

                int x = (int)Math.Round(rect.Left + rect.Width / 2.0);
                int y = (int)Math.Round(rect.Top + rect.Height / 2.0);

                if (SetCursorPos(x, y) == false)
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

                Thread.Sleep(120);
                log("Epic Games hybrid click: " + description +
                    " @ UIA rect (" + Math.Round(rect.Left) + "," +
                    Math.Round(rect.Top) + "," +
                    Math.Round(rect.Width) + "," +
                    Math.Round(rect.Height) + ").");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool FocusPreviousEditable(
            IntPtr hwnd, bool password, int maxTabs)
        {
            for (int i = 0; i < maxTabs; i++)
            {
                if (!NativeKeyboardInput.SendShiftTab(log))
                    return false;

                Thread.Sleep(80);

                AutomationElement focused = null;
                try { focused = AutomationElement.FocusedElement; } catch { }

                if (focused != null && IsLikelyEditable(focused, password))
                {
                    try
                    {
                        focused.SetFocus();
                    }
                    catch { }

                    return true;
                }
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

        private static AutomationElement FindNamedInvokable(
            IntPtr hwnd, string[] names)
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

                    AutomationElement current = e;

                    // First try the named node itself, then its ancestors.
                    // This handles Epic WebView text nodes wrapped in clickable
                    // Custom/Button containers.
                    for (int i = 0; current != null && i < 8; i++)
                    {
                        if (TryInvoke(current))
                            return current;

                        current = SafeParent(current);
                    }
                }
                catch { }
            }

            return null;
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
            catch { }

            try
            {
                var toggle = (TogglePattern)element.GetCurrentPattern(
                    TogglePattern.Pattern);

                toggle.Toggle();
                return true;
            }
            catch { }

            return false;
        }

        private AutomationElement FindEditableElement(
            IntPtr hwnd, bool password, int maxTabs)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
                return null;

            string[] labels = password
                ? new[] { "Hasło", "Password", "Wprowadź hasło", "Enter password" }
                : new[] { "Adres e-mail", "E-mail", "Email", "Email address" };

            // First search by the visible semantic label, regardless of the
            // Chromium control type. This is important because Epic has used
            // Custom controls instead of ControlType.Edit in different builds.
            foreach (AutomationElement e in root.FindAll(
                TreeScope.Descendants, AutomationCondition.TrueCondition))
            {
                try
                {
                    if (e.Current.IsOffscreen || !e.Current.IsEnabled)
                        continue;

                    string name = e.Current.Name ?? string.Empty;

                    if (!labels.Any(label =>
                        name.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    if (TryFocusTarget(e))
                    {
                        log("Epic Games UIA: znaleziono pole " +
                            (password ? "hasła" : "e-mail") +
                            " po nazwie „" + name + "”.");
                        return e;
                    }
                }
                catch { }
            }

            // Second pass: look for an actual editable/value-capable control
            // without assuming a specific ControlType.
            foreach (AutomationElement e in root.FindAll(
                TreeScope.Descendants, AutomationCondition.TrueCondition))
            {
                try
                {
                    if (e.Current.IsOffscreen || !e.Current.IsEnabled)
                        continue;

                    if (!IsLikelyEditable(e, password))
                        continue;

                    if (TryFocusTarget(e))
                    {
                        log("Epic Games UIA: znaleziono edytowalny element pola " +
                            (password ? "hasła" : "e-mail") +
                            " bez polegania na ControlType.Edit.");
                        return e;
                    }
                }
                catch { }
            }

            // Final fallback: navigate focus semantically. No screen
            // coordinates are used. The focused WebView element is accepted
            // only if it is a likely editable/password control.
            for (int i = 0; i < maxTabs; i++)
            {
                AutomationElement focused = null;
                try { focused = AutomationElement.FocusedElement; } catch { }

                try
                {
                    if (focused != null && IsLikelyEditable(focused, password))
                    {
                        log("Epic Games UIA: pole " +
                            (password ? "hasła" : "e-mail") +
                            " znaleziono przez fokus klawiatury.");
                        return focused;
                    }
                }
                catch { }

                if (!NativeKeyboardInput.SendTab(log))
                    break;

                Thread.Sleep(80);
            }

            return null;
        }

        private static bool IsLikelyEditable(
            AutomationElement element, bool password)
        {
            try
            {
                if (element.Current.IsOffscreen || !element.Current.IsEnabled)
                    return false;

                if (password)
                {
                    try
                    {
                        if (element.Current.IsPassword)
                            return true;
                    }
                    catch { }
                }

                if (element.Current.ControlType == ControlType.Edit)
                    return true;

                try
                {
                    element.GetCurrentPattern(ValuePattern.Pattern);
                    return !password;
                }
                catch { }

                try
                {
                    element.GetCurrentPattern(TextPattern.Pattern);
                    return !password;
                }
                catch { }

                string name = element.Current.Name ?? string.Empty;
                if (password)
                    return name.IndexOf("Hasło", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           name.IndexOf("Password", StringComparison.OrdinalIgnoreCase) >= 0;

                return name.IndexOf("E-mail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       name.IndexOf("Email", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFocusTarget(AutomationElement element)
        {
            try
            {
                if (!element.Current.IsEnabled || element.Current.IsOffscreen)
                    return false;

                try
                {
                    if (!element.Current.IsKeyboardFocusable)
                        return TryFocusAncestorOrDescendant(element);
                }
                catch { }

                element.SetFocus();
                Thread.Sleep(70);

                AutomationElement focused = null;
                try { focused = AutomationElement.FocusedElement; } catch { }

                return focused != null && SameElement(focused, element);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFocusAncestorOrDescendant(AutomationElement element)
        {
            try
            {
                AutomationElement current = element;

                for (int i = 0; i < 5 && current != null; i++)
                {
                    try
                    {
                        if (current.Current.IsEnabled &&
                            !current.Current.IsOffscreen &&
                            current.Current.IsKeyboardFocusable)
                        {
                            current.SetFocus();
                            Thread.Sleep(70);
                            return true;
                        }
                    }
                    catch { }

                    current = SafeParent(current);
                }

                var children = element.FindAll(
                    TreeScope.Descendants,
                    AutomationCondition.TrueCondition);

                foreach (AutomationElement child in children)
                {
                    try
                    {
                        if (child.Current.IsEnabled &&
                            !child.Current.IsOffscreen &&
                            child.Current.IsKeyboardFocusable)
                        {
                            child.SetFocus();
                            Thread.Sleep(70);
                            return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private static bool SameElement(
            AutomationElement a, AutomationElement b)
        {
            try
            {
                return a != null && b != null &&
                       a.Equals(b);
            }
            catch
            {
                return false;
            }
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

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

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
