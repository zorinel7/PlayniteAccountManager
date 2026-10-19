using Playnite.SDK;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Localized user-facing messages (notifications, status bar, test results).
    /// Keys are given without the "LOCPlayniteAccountManager" prefix.
    /// </summary>
    internal static class Loc
    {
        private const string Prefix = "LOCPlayniteAccountManager";

        public static string Get(string key, params object[] args)
        {
            string text = ResourceProvider.GetString(Prefix + key);
            if (string.IsNullOrEmpty(text) || text.StartsWith("<!"))
                text = key;

            if (args == null || args.Length == 0)
                return text;

            try
            {
                return string.Format(text, args);
            }
            catch (System.FormatException)
            {
                return text;
            }
        }
    }
}
