using System;

namespace PlayniteAccountManager.Models
{
    public class GameAccountAssignment
    {
        public Guid GameId { get; set; }
        public Guid AccountId { get; set; }
        public bool AutoLogin { get; set; }
        public bool LogoutAfterGame { get; set; }

        public GameAccountAssignment()
        {
            AutoLogin = true;
            LogoutAfterGame = true;
        }
    }
}
