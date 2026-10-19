using System;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Ubisoft Connect automatic login through the accessibility tree of its
    /// sign-in page. The launcher window (upc.exe, Chromium) shows e-mail and
    /// password on one page with a "Log in" button; after a successful login
    /// the same window switches to the library, so success is detected when
    /// the login form disappears.
    /// </summary>
    internal sealed class UbisoftConnectMsaaLogin
    {
        private readonly ChromiumLoginAutomation automation;

        public UbisoftConnectMsaaLogin(Action<string> log)
        {
            automation = new ChromiumLoginAutomation(new ChromiumLoginOptions
            {
                LogPrefix = "Ubisoft MSAA",
                LauncherName = "Ubisoft Connect",
                ProcessNames = new[] { "upc", "UbisoftConnect" },
                GoBackFromRememberedAccount = false,
                SuccessWhenWindowCloses = false,
                SuccessWhenFormGoneFor = TimeSpan.FromSeconds(3),
                ActivateWithEnter = true
            }, log);
        }

        public MsaaLoginResult Login(string username, string password, out string error)
        {
            return automation.Login(username, password, out error);
        }

        internal string Probe(int timeoutSeconds) => automation.Probe(timeoutSeconds);

        internal string ProbeFocus(int timeoutSeconds) => automation.ProbeFocus(timeoutSeconds);

        internal string ProbeType(int unused) => automation.ProbeType("test");

        internal string ProbeTypeKeep(int unused) => automation.ProbeType("test", false);

        internal string ProbeTypePassword(int unused) => automation.ProbeType("test", true, true);
    }
}
