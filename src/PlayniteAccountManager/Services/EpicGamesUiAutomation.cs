using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using DrawingSize = System.Drawing.Size;
using DrawingPoint = System.Drawing.Point;
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
            Bitmap beforePasswordTransition = CaptureWindow(hwnd);

            if (!NativeKeyboardInput.SendEnter(log))
            {
                DisposeBitmap(beforePasswordTransition);
                return Fail(out error, "Nie udało się zatwierdzić adresu e-mail Epic Games.");
            }

            // Wait for the password page rather than assuming a fixed delay.
            if (!WaitForPasswordSurfaceReady(hwnd, beforePasswordTransition, 15))
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

        private static Bitmap CaptureWindow(IntPtr hwnd)
        {
            try
            {
                RECT rect;
                if (!GetWindowRect(hwnd, out rect))
                    return null;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                if (width <= 0 || height <= 0)
                    return null;

                Bitmap bitmap = new Bitmap(
                    width, height, PixelFormat.Format24bppRgb);

                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        rect.Left, rect.Top, 0, 0,
                        new DrawingSize(width, height),
                        CopyPixelOperation.SourceCopy);
                }

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static void DisposeBitmap(Bitmap bitmap)
        {
            if (bitmap == null)
                return;

            try { bitmap.Dispose(); } catch { }
        }

        private static bool HasVisualTransition(IntPtr hwnd, Bitmap before)
        {
            using (Bitmap after = CaptureWindow(hwnd))
            {
                if (after == null || before == null)
                    return false;

                int width = Math.Min(before.Width, after.Width);
                int height = Math.Min(before.Height, after.Height);

                if (width < 100 || height < 100)
                    return false;

                const int step = 12;
                int different = 0;
                int total = 0;

                for (int y = 0; y < height; y += step)
                {
                    for (int x = 0; x < width; x += step)
                    {
                        Color a = before.GetPixel(x, y);
                        Color b = after.GetPixel(x, y);

                        int delta =
                            Math.Abs(a.R - b.R) +
                            Math.Abs(a.G - b.G) +
                            Math.Abs(a.B - b.B);

                        if (delta > 45)
                            different++;

                        total++;
                    }
                }

                return total > 0 && ((double)different / total) >= 0.02;
            }
        }

        private static bool HasLargeEpicBlueButton(IntPtr hwnd)
        {
            using (Bitmap bitmap = CaptureWindow(hwnd))
            {
                return bitmap != null &&
                       FindLargeEpicBlueButton(bitmap) != Rectangle.Empty;
            }
        }

        private static Rectangle FindLargeEpicBlueButton(Bitmap bitmap)
        {
            const int sample = 4;
            int sw = bitmap.Width / sample;
            int sh = bitmap.Height / sample;

            if (sw <= 0 || sh <= 0)
                return Rectangle.Empty;

            bool[,] mask = new bool[sw, sh];
            bool[,] visited = new bool[sw, sh];

            for (int y = 0; y < sh; y++)
            {
                for (int x = 0; x < sw; x++)
                {
                    Color color = bitmap.GetPixel(x * sample, y * sample);

                    mask[x, y] =
                        color.R < 120 &&
                        color.G > 120 &&
                        color.B > 165 &&
                        color.B > color.G &&
                        color.G > color.R + 40;
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

                    int minX = x, maxX = x;
                    int minY = y, maxY = y;
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

                    int width = maxX - minX + 1;
                    int height = maxY - minY + 1;
                    double ratio = height > 0
                        ? (double)width / height
                        : 0;

                    int area = width * height;

                    if (pixels < 180 ||
                        width < 70 ||
                        height < 10 ||
                        ratio < 3 ||
                        ratio > 15)
                        continue;

                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = new Rectangle(
                            minX * sample,
                            minY * sample,
                            width * sample,
                            height * sample);
                    }
                }
            }

            return best;
        }

        private bool WaitForLoginSurfaceReady(IntPtr hwnd, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, seconds));
            DateTime minReady = DateTime.UtcNow.AddMilliseconds(900);

            while (DateTime.UtcNow < deadline)
            {
                hwnd = FindMainWindowHandle();

                if (hwnd != IntPtr.Zero)
                {
                    // UIA is only an optional readiness signal. Epic's WebView
                    // can expose no useful text at all while the page is ready.
                    if (IsTextVisible(hwnd, "Zaloguj się do Epic Games") ||
                        IsTextVisible(hwnd, "Adres e-mail") ||
                        IsTextVisible(hwnd, "Kontynuuj"))
                    {
                        EnsureForeground(hwnd);
                        return true;
                    }

                    // DPI/resolution independent visual readiness fallback.
                    // We detect the actual cyan primary button in the current
                    // window image; no fixed coordinates are used.
                    if (DateTime.UtcNow >= minReady && HasLargeEpicBlueButton(hwnd))
                    {
                        EnsureForeground(hwnd);
                        log("Epic Games visual: potwierdzono gotowy ekran logowania.");
                        return true;
                    }
                }

                Thread.Sleep(160);
            }

            // The screenshot may be fully rendered even when both UIA and the
            // visual detector are temporarily unavailable. Do not block a
            // confirmed visible EA/Epic window forever; give it a final
            // stabilization delay and allow the user-confirmed keyboard flow.
            if (hwnd != IntPtr.Zero)
            {
                EnsureForeground(hwnd);
                Thread.Sleep(500);
                log("Epic Games: okno logowania jest widoczne. Przechodzę do potwierdzonej sekwencji klawiatury.");
                return true;
            }

            return false;
        }

        private bool WaitForPasswordSurfaceReady(
            IntPtr hwnd, Bitmap beforeTransition, int seconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, seconds));
            DateTime minimumWait = DateTime.UtcNow.AddMilliseconds(500);

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
                        DisposeBitmap(beforeTransition);
                        return true;
                    }

                    if (DateTime.UtcNow >= minimumWait &&
                        beforeTransition != null &&
                        HasVisualTransition(hwnd, beforeTransition))
                    {
                        EnsureForeground(hwnd);
                        Thread.Sleep(180);
                        DisposeBitmap(beforeTransition);
                        log("Epic Games visual: wykryto zmianę strony po zatwierdzeniu adresu e-mail.");
                        return true;
                    }
                }

                Thread.Sleep(160);
            }

            // The password screen can also be fully usable while UIA does not
            // expose any text and the visual delta is small. Give it one final
            // stabilization pause instead of aborting the login.
            Thread.Sleep(350);
            DisposeBitmap(beforeTransition);
            return FindMainWindowHandle() != IntPtr.Zero;
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
        private static extern bool GetWindowRect(
            IntPtr hwnd, out RECT rect);

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
