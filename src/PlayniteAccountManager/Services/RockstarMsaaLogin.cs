using System;
using System.Text;
using System.Runtime.InteropServices;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Rockstar Games Launcher automatic login through the accessibility tree of its
    /// sign-in page. The launcher opens a separate window of class
    /// "Rockstar Games Authentication" (SocialClubHelper.exe, Chromium) with e-mail,
    /// password and a "Sign in" button on one page; the window closes after a
    /// successful login.
    /// </summary>
    internal sealed class RockstarMsaaLogin
    {
        private const string AuthWindowClass = "Rockstar Games Authentication";

        private readonly ChromiumLoginAutomation automation;

        public RockstarMsaaLogin(Action<string> log)
        {
            automation = new ChromiumLoginAutomation(new ChromiumLoginOptions
            {
                LogPrefix = "Rockstar MSAA",
                LauncherName = "Rockstar Games Launcher",
                ProcessNames = new[] { "SocialClubHelper", "Launcher" },
                IsLoginWindow = IsAuthWindow,
                RequireLoginWindowMatch = true,
                GoBackFromRememberedAccount = false,
                SuccessWhenWindowCloses = true,
                // The launcher updates itself and starts several helper processes
                // before the sign-in window appears.
                WindowTimeout = TimeSpan.FromSeconds(90),
                SlowAccessibility = true,
                SubmitDelay = TimeSpan.FromSeconds(1),
                // The first request is sometimes answered with "we cannot process your
                // request right now"; pressing "Sign in" again on the same form works.
                ResubmitAfter = TimeSpan.FromSeconds(8),
                MaxResubmits = 1,
                // The page rejects the accessibility "click"; a real click works.
                ClickSubmitWithMouse = true
            }, log);
        }

        public MsaaLoginResult Login(string username, string password, out string error)
        {
            return automation.Login(username, password, out error);
        }

        internal string Probe(int timeoutSeconds) => automation.Probe(timeoutSeconds);

        internal string ProbeFocus(int timeoutSeconds) => automation.ProbeFocus(timeoutSeconds);

        private static bool IsAuthWindow(IntPtr hwnd)
        {
            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            return className.ToString() == AuthWindowClass;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    }
}
