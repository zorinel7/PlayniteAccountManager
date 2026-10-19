using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// GOG Galaxy automatic login.
    ///
    /// The sign-in window (Qt class "galaxy::client_application::LoginWindow",
    /// fixed size) hosts the login.gog.com page in QtWebEngine, which exposes no
    /// accessibility tree, so the fields cannot be located like in EA App or
    /// Ubisoft Connect. Instead the flow relies on signals that are reliable:
    ///   - GalaxyClient.log reports when the login page has been loaded and when
    ///     the login succeeded (redirect to embed.gog.com/on_login_success);
    ///   - the fields are clicked at fixed positions of the fixed-size window,
    ///     and keyboard input is only sent while that window has focus;
    ///   - success means the sign-in window closes or the log reports success.
    /// Credentials are never logged.
    /// </summary>
    internal sealed class GOGGalaxyUiAutomation
    {
        private const string MainProcessName = "GalaxyClient";
        private const string LoginWindowClass = "galaxy::client_application::LoginWindow";

        // Field centres relative to the login window (measured on the 390x685 window).
        private const double FieldX = 0.50;
        private const double EmailFieldY = 141.0 / 685.0;
        private const double PasswordFieldY = 188.0 / 685.0;

        private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(30);

        private readonly Action<string> log;
        private readonly string logPath;

        public GOGGalaxyUiAutomation(Action<string> log)
        {
            this.log = log ?? (_ => { });
            logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GOG.com", "Galaxy", "logs", "GalaxyClient.log");
        }

        /// <summary>
        /// Signs in after GOG Galaxy was started at <paramref name="startedLocal"/>.
        /// Returns FieldsNotFound (nothing typed) when the sign-in form did not appear.
        /// </summary>
        public MsaaLoginResult Login(string username, string password, DateTime startedLocal, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                error = "Brak loginu lub hasła zapisanych dla konta GOG Galaxy.";
                return MsaaLoginResult.Failed;
            }

            log("GOG INPUT: czekam na okno logowania GOG Galaxy.");
            IntPtr window = WaitForLoginWindow(WindowTimeout);
            if (window == IntPtr.Zero)
            {
                error = "GOG Galaxy nie pokazał okna logowania.";
                return MsaaLoginResult.FieldsNotFound;
            }

            if (WaitForLogLine(startedLocal, "login.gog.com/auth", PageTimeout))
                log("GOG INPUT: formularz logowania załadowany (GalaxyClient.log).");
            else
                log("GOG INPUT: brak potwierdzenia formularza w logu, kontynuuję po oknie logowania.");

            Thread.Sleep(1500); // let the page finish rendering its fields

            if (!IsWindow(window) || !IsWindowVisible(window))
            {
                error = "Okno logowania GOG Galaxy zostało zamknięte przed wpisaniem danych.";
                return MsaaLoginResult.FieldsNotFound;
            }

            log("GOG INPUT: wpisuję e-mail.");
            if (!FillField(window, EmailFieldY, username, out error))
                return MsaaLoginResult.FieldsNotFound;

            log("GOG INPUT: wpisuję hasło.");
            if (!FillField(window, PasswordFieldY, password, out error))
                return MsaaLoginResult.Failed;

            NativeKeyboardInput.SendEnter(log);
            log("GOG INPUT: wysłano formularz (ENTER).");

            return WaitForResult(window, startedLocal, out error);
        }

        private bool FillField(IntPtr window, double relativeY, string value, out string error)
        {
            error = null;

            if (!EAAppUiAutomation.EnsureWindowForeground(window))
            {
                error = "Nie udało się aktywować okna logowania GOG Galaxy.";
                return false;
            }

            RECT rect;
            if (!GetWindowRect(window, out rect) || rect.Right - rect.Left < 100 || rect.Bottom - rect.Top < 100)
            {
                error = "Nie udało się odczytać położenia okna logowania GOG Galaxy.";
                return false;
            }

            int x = rect.Left + (int)((rect.Right - rect.Left) * FieldX);
            int y = rect.Top + (int)((rect.Bottom - rect.Top) * relativeY);
            ClickAt(x, y);
            Thread.Sleep(250);

            if (!NativeKeyboardInput.SendSelectAll(log))
            {
                error = "Nie udało się zaznaczyć zawartości pola w GOG Galaxy.";
                return false;
            }

            Thread.Sleep(100);
            if (!NativeKeyboardInput.TypeText(value, window, log))
            {
                error = "Nie udało się wpisać danych w oknie GOG Galaxy (okno straciło fokus).";
                return false;
            }

            Thread.Sleep(300);
            return true;
        }

        private MsaaLoginResult WaitForResult(IntPtr window, DateTime startedLocal, out string error)
        {
            error = null;
            DateTime deadline = DateTime.UtcNow + ResultTimeout;

            while (DateTime.UtcNow < deadline)
            {
                if (!IsWindow(window) || !IsWindowVisible(window))
                {
                    log("GOG INPUT: okno logowania zostało zamknięte, GOG Galaxy jest zalogowany.");
                    return MsaaLoginResult.Success;
                }

                if (WaitForLogLine(startedLocal, "on_login_success", TimeSpan.Zero))
                {
                    log("GOG INPUT: GalaxyClient.log potwierdza zalogowanie.");
                    return MsaaLoginResult.Success;
                }

                Thread.Sleep(500);
            }

            error = "GOG Galaxy nie potwierdził zalogowania (sprawdź zapisane hasło albo dokończ weryfikację w oknie GOG Galaxy).";
            return MsaaLoginResult.Failed;
        }

        // ------------------------------------------------------------------ log

        /// <summary>
        /// Looks for <paramref name="marker"/> in GalaxyClient.log lines written after
        /// <paramref name="sinceLocal"/> (the log uses local "yyyy-MM-dd HH:mm:ss.fff").
        /// </summary>
        private bool WaitForLogLine(DateTime sinceLocal, string marker, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            do
            {
                foreach (string line in ReadLogTail())
                {
                    if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0 || line.Length < 23)
                        continue;

                    DateTime timestamp;
                    if (DateTime.TryParseExact(line.Substring(0, 23), "yyyy-MM-dd HH:mm:ss.fff",
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out timestamp) &&
                        timestamp >= sinceLocal)
                        return true;
                }

                if (DateTime.UtcNow >= deadline)
                    break;

                Thread.Sleep(500);
            }
            while (true);

            return false;
        }

        private string[] ReadLogTail()
        {
            try
            {
                if (!File.Exists(logPath))
                    return new string[0];

                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    const long tail = 1024 * 1024;
                    if (stream.Length > tail)
                        stream.Seek(-tail, SeekOrigin.End);

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd().Split('\n');
                }
            }
            catch (IOException)
            {
                return new string[0];
            }
            catch (UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        // -------------------------------------------------------------- windows

        private static IntPtr WaitForLoginWindow(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                IntPtr hwnd = FindLoginWindow();
                if (hwnd != IntPtr.Zero)
                    return hwnd;

                Thread.Sleep(300);
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindLoginWindow()
        {
            var ids = new System.Collections.Generic.HashSet<uint>();
            Process[] processes;
            try { processes = Process.GetProcessesByName(MainProcessName); }
            catch { return IntPtr.Zero; }

            foreach (Process process in processes)
            {
                try { ids.Add((uint)process.Id); }
                catch { }
                finally { process.Dispose(); }
            }

            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, lParam) =>
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (!ids.Contains(pid) || !IsWindowVisible(hwnd))
                    return true;

                // The Win32 class is a generic Qt one ("Qt672QWindowIcon"); the Qt
                // object class that identifies the sign-in window is exposed through
                // UI Automation.
                try
                {
                    if (AutomationElement.FromHandle(hwnd).Current.ClassName == LoginWindowClass)
                    {
                        found = hwnd;
                        return false;
                    }
                }
                catch
                {
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>Clicks a screen point and puts the mouse cursor back.</summary>
        private static void ClickAt(int x, int y)
        {
            POINT previous;
            GetCursorPos(out previous);

            SetCursorPos(x, y);
            Thread.Sleep(80);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);

            SetCursorPos(previous.X, previous.Y);
        }

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);


        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    }
}
