using System;
using System.Runtime.InteropServices;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Battle.net automatic login through the accessibility tree of its sign-in page.
    /// The launcher opens a small Qt window with a Chromium (CEF) page:
    ///   remembered account: password field and a "Switch account" link above it
    ///   e-mail step:        e-mail field → "Continue"
    ///   password step:      password field → "Log in"
    /// The sign-in window closes after a successful login and the main client opens.
    /// </summary>
    internal sealed class BattleNetMsaaLogin
    {
        // The sign-in window is ~350x610; the main client window is much larger.
        private const int MaxLoginWindowWidth = 700;

        private readonly ChromiumLoginAutomation automation;

        public BattleNetMsaaLogin(Action<string> log)
        {
            automation = new ChromiumLoginAutomation(new ChromiumLoginOptions
            {
                LogPrefix = "Battle.net MSAA",
                LauncherName = "Battle.net",
                ProcessNames = new[] { "Battle.net" },
                IsLoginWindow = IsLoginWindow,
                RequireLoginWindowMatch = true,
                GoBackFromRememberedAccount = true,
                BackControlIsLink = true,
                SuccessWhenWindowCloses = true,
                WindowTimeout = TimeSpan.FromSeconds(90),
                StepSettleTime = TimeSpan.FromSeconds(1.5),
                ClickFieldToFocus = true
            }, log);
        }

        public MsaaLoginResult Login(string username, string password, out string error)
        {
            return automation.Login(username, password, out error);
        }

        internal string Probe(int timeoutSeconds) => automation.Probe(timeoutSeconds);

        internal string ProbeFocus(int timeoutSeconds) => automation.ProbeFocus(timeoutSeconds);

        private static bool IsLoginWindow(IntPtr hwnd)
        {
            RECT rect;
            return GetWindowRect(hwnd, out rect) &&
                   rect.Right - rect.Left > 0 &&
                   rect.Right - rect.Left <= MaxLoginWindowWidth;
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
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    }
}
