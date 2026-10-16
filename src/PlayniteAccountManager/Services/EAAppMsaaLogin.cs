using System;
using System.Windows.Automation;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// EA App automatic login through the accessibility tree of its sign-in page.
    /// The sign-in window (UIA AutomationId "Auth") hosts a CEF page with two
    /// steps: e-mail -> "Next", then password -> "Sign in"; the window closes
    /// after a successful login.
    /// </summary>
    internal sealed class EAAppMsaaLogin
    {
        private const string AuthAutomationId = "Auth";

        private readonly ChromiumLoginAutomation automation;

        public EAAppMsaaLogin(Action<string> log)
        {
            automation = new ChromiumLoginAutomation(new ChromiumLoginOptions
            {
                LogPrefix = "EA MSAA",
                LauncherName = "EA App",
                ProcessNames = new[] { "EADesktop" },
                IsLoginWindow = IsAuthWindow,
                RequireLoginWindowMatch = true,
                GoBackFromRememberedAccount = true,
                SuccessWhenWindowCloses = true
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
            return AutomationElement.FromHandle(hwnd).Current.AutomationId == AuthAutomationId;
        }
    }
}
