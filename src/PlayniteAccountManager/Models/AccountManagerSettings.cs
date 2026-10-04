using System.Collections.Generic;
using Playnite.SDK;
using PlayniteAccountManager.Models;

namespace PlayniteAccountManager.Models
{
    public class AccountManagerSettings : ObservableObject, ISettings
    {
        private List<AccountRecord> accounts;
        private List<GameAccountAssignment> assignments;

        public List<AccountRecord> Accounts
        {
            get => accounts;
            set => SetValue(ref accounts, value);
        }

        public List<GameAccountAssignment> Assignments
        {
            get => assignments;
            set => SetValue(ref assignments, value);
        }

        public AccountManagerSettings()
        {
            Accounts = new List<AccountRecord>();
            Assignments = new List<GameAccountAssignment>();
        }

        public void BeginEdit()
        {
        }

        public void CancelEdit()
        {
        }

        public void EndEdit()
        {
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }
}
