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

            // The exact keyboard sequence was verified manually on the current
            // Epic login UI. We reproduce that sequence directly instead of
            // relying on UIA focus order, which Epic's WebView does not expose
            // reliably.
            if (!WaitForLoginSurfaceReady(hwnd, 20))
            {
                error = "Epic Games Launcher został uruchomiony, ale ekran logowania nie był gotowy w ciągu 20 sekund.";
                log("Epic Games: ekran logowania nie osiągnął stanu gotowego.");
                return false;
            }

            log("Epic Games: ekran logowania gotowy. Używam potwierdzonej sekwencji klawiatury.");

            // [Tab] -> e-mail
            if (!PressTab("fokus e-mail"))
                return Fail(out error, "Nie udało się ustawić fokusu pola e-mail.");

            Thread.Sleep(120);

            if (!NativeKeyboardInput.TypeText(username, hwnd, log))
                return Fail(out error, "Nie udało się wpisać e-maila Epic Games.");

            // [Tab] [Tab] [Enter] -> continue
            if (!PressTabs(2, "przejście do hasła"))
                return Fail(out error, "Nie udało się przejść do następnego etapu logowania Epic Games.");

            Thread.Sleep(100);
            if (!NativeKeyboardInput.SendEnter(log))
                return Fail(out error, "Nie udało się zatwierdzić adresu e-mail Epic Games.");

            // Do not assume a fixed load time; wait for the password surface.
            if (!WaitForPasswordSurfaceReady(hwnd, 15))
            {
                error = "Epic Games nie załadował ekranu hasła w wyznaczonym czasie.";
                return false;
            }

            log("Epic Games: ekran hasła gotowy.");

            // [Tab] [Tab] -> password
            if (!PressTabs(2, "fokus hasła"))
                return Fail(out error, "Nie udało się ustawić fokusu pola hasła Epic Games.");

            Thread.Sleep(100);

            if (!NativeKeyboardInput.TypeText(password, hwnd, log))
                return Fail(out error, "Nie udało się wpisać hasła Epic Games.");

            // [Tab] [Tab] [Tab] [Tab] [Enter] -> login
            if (!PressTabs(4, "przejście do przycisku logowania"))
                return Fail(out error, "Nie udało się przejść do przycisku logowania Epic Games.");

            Thread.Sleep(100);
            if (!NativeKeyboardInput.SendEnter(log))
                return Fail(out error, "Nie udało się zatwierdzić logowania Epic Games.");

            // Epic may display 2FA setup after successful authentication.
            // Always choose "Ustaw później" semantically if it appears.
            WaitAndInvokeLater2FA(hwnd, 15);

            return true;
        }

        private bool PressTab(string description)
        {
            if (!NativeKeyboardInput.SendTab(log))
                return false;

            log("Epic Games keyboard: [TAB] -> " + description + ".");
            Thread.Sleep(100);
            return true;
        }

        private bool PressTabs(int count, string description)
        {
            for (int i = 0; i < count; i++)
            {
                if (!NativeKeyboardInput.SendTab(log))
                    return false;

                Thread.Sleep(90);
            }

            log("Epic Games keyboard: wysłano " + count + "x [TAB] -> " + description + ".");
            return true;
        }

        private static bool Fail(out string error, string message)
        {
            error = message;
            return false;
        }

        private bool WaitForLoginSurfaceReady(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, seconds));

            while (DateTime.UtcNow < deadline)
            {
                hwnd = FindMainWindowHandle();

                if (hwnd != IntPtr.Zero &&
                    (IsTextVisible(hwnd, "Zaloguj się do Epic Games") ||
                     IsTextVisible(hwnd, "Adres e-mail") ||
                     IsTextVisible(hwnd, "Kontynuuj")))
                {
                    EnsureForeground(hwnd);
                    return true;
                }

                // The WebView may temporarily expose no text to UIA. Keep
                // polling the window instead of sending Tab prematurely.
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

                if (hwnd != IntPtr.Zero &&
                    (IsTextVisible(hwnd, "Hasło") ||
                     IsTextVisible(hwnd, "Wprowadź hasło") ||
                     IsTextVisible(hwnd, "Password")))
                {
                    EnsureForeground(hwnd);
                    return true;
                }

                Thread.Sleep(180);
            }

            return false;
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

                if (ClickElementCenter(element, description))
                    return true;
            }

            // No Tab traversal. The WebView's focus order is not reliable.
            // If UIA cannot expose the button, find the current button visually.
            if (TryClickVisualBlueButton(hwnd, description))
                return true;

            return false;
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

        private bool TryClickVisualInputField(
            IntPtr hwnd, bool password, string description)
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

                using (var bitmap = new Bitmap(
                    width, height, PixelFormat.Format24bppRgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        windowRect.Left,
                        windowRect.Top,
                        0,
                        0,
                        new DrawingSize(width, height),
                        CopyPixelOperation.SourceCopy);

                    Rectangle candidate = FindLargeEpicInputField(bitmap, password);

                    if (candidate == Rectangle.Empty)
                    {
                        log("Epic Games visual: nie znaleziono prostokąta pola " +
                            (password ? "hasła" : "e-mail") + ".");
                        return false;
                    }

                    int x = windowRect.Left +
                            candidate.Left + candidate.Width / 2;
                    int y = windowRect.Top +
                            candidate.Top + candidate.Height / 2;

                    if (!NativeClickScreen(x, y, description))
                        return false;

                    Thread.Sleep(100);
                    return true;
                }
            }
            catch (Exception ex)
            {
                log("Epic Games visual input fallback: " + ex.Message);
                return false;
            }
        }

        private static Rectangle FindLargeEpicInputField(
            Bitmap bitmap, bool password)
        {
            const int sample = 3;

            int sw = bitmap.Width / sample;
            int sh = bitmap.Height / sample;

            if (sw <= 0 || sh <= 0)
                return Rectangle.Empty;

            // Epic's input surface is a large dark rounded rectangle whose
            // interior is visually distinct from the card background. We find
            // that shape from the current screenshot instead of assuming a
            // location or resolution.
            var mask = new bool[sw, sh];
            var visited = new bool[sw, sh];

            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    Color color = bitmap.GetPixel(x * sample, y * sample);

                    // Current Epic login field interior is approximately
                    // #242428. Allow a range so theme/anti-aliasing changes
                    // do not break detection.
                    mask[x, y] =
                        color.R >= 28 && color.R <= 55 &&
                        Math.Abs(color.R - color.G) <= 3 &&
                        Math.Abs(color.G - color.B) <= 6;
                }
            }

            Rectangle best = Rectangle.Empty;
            double bestScore = double.MinValue;

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

                    int minX = x, maxX = x;
                    int minY = y, maxY = y;
                    int pixels = 0;

                    while (queue.Count > 0)
                    {
                        DrawingPoint p = queue.Dequeue();
                        pixels++;

                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;

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
                            queue.Enqueue(new DrawingPoint(nx, ny));
                        }
                    }

                    int rw = maxX - minX + 1;
                    int rh = maxY - minY + 1;

                    if (rw < 180 || rh < 20 || rh > 100)
                        continue;

                    double ratio = (double)rw / rh;
                    if (ratio < 5.0 || ratio > 15.0)
                        continue;

                    double centerX = (minX + maxX) / 2.0;
                    double centerY = (minY + maxY) / 2.0;

                    // Prefer horizontal input-shaped regions near the vertical
                    // center of the login card. Password and email inputs have
                    // the same geometry, so this remains launcher-layout driven
                    // rather than screen-coordinate driven.
                    double centerBias =
                        Math.Abs(centerX - sw / 2.0) / Math.Max(1.0, sw / 2.0);

                    double heightScore =
                        1.0 - Math.Abs(rh - 15.0) / 20.0;

                    double score =
                        pixels * 0.002 +
                        ratio * 2.0 -
                        centerBias * 25.0 +
                        heightScore * 10.0;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new Rectangle(
                            minX * sample,
                            minY * sample,
                            rw * sample,
                            rh * sample);
                    }
                }
            }

            if (best == Rectangle.Empty)
                return best;

            // For the password step, ignore a candidate that is suspiciously
            // low/high relative to the button if another input-like region
            // can be found. The current screenshot has exactly one such field.
            return best;
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

                using (var bitmap = new Bitmap(
                    width, height, PixelFormat.Format24bppRgb))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        windowRect.Left,
                        windowRect.Top,
                        0,
                        0,
                        new DrawingSize(width, height),
                        CopyPixelOperation.SourceCopy);

                    Rectangle candidate = FindLargeEpicBlueButton(bitmap);
                    if (candidate == Rectangle.Empty)
                        return false;

                    int x = windowRect.Left +
                            candidate.Left + candidate.Width / 2;
                    int y = windowRect.Top +
                            candidate.Top + candidate.Height / 2;

                    if (!NativeClickScreen(x, y, description))
                        return false;

                    log("Epic Games visual: dynamicznie znaleziono " + description +
                        " @ (" + candidate.Left + "," + candidate.Top + "," +
                        candidate.Width + "," + candidate.Height + ").");
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
                            queue.Enqueue(new DrawingPoint(nx, ny));
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
