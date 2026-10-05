using System;

namespace PlayniteAccountManager.Models
{
    public class AccountRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public LauncherType Launcher { get; set; }
        public string UserName { get; set; }
        public bool IsPrimary { get; set; }
        public EpicLoginMode EpicLoginMode { get; set; }
        public EALoginMode EALoginMode { get; set; }

        public AccountRecord()
        {
            Id = Guid.NewGuid();
            Name = string.Empty;
            UserName = string.Empty;
            IsPrimary = false;
            EpicLoginMode = EpicLoginMode.Manual;
            EALoginMode = EALoginMode.Manual;
            Launcher = LauncherType.UbisoftConnect;
        }
    }
}
