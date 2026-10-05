namespace PlayniteAccountManager.Models
{
    public static class LauncherTypeExtensions
    {
        public static string GetDisplayName(this LauncherType launcher)
        {
            switch (launcher)
            {
                case LauncherType.UbisoftConnect: return "Ubisoft Connect";
                case LauncherType.Steam: return "Steam";
                case LauncherType.RockstarGamesLauncher: return "Rockstar Games Launcher";
                case LauncherType.BattleNet: return "Battle.net";
                case LauncherType.GOGGalaxy: return "GOG Galaxy";
                case LauncherType.XboxApp: return "Xbox App";
                default: return "Inny launcher";
            }
        }
    }
}
