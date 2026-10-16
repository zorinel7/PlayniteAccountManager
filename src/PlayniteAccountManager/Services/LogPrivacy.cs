namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Helpers that keep account identifiers out of the Playnite log in readable form.
    /// </summary>
    internal static class LogPrivacy
    {
        /// <summary>
        /// "player@example.com" -> "pl***@ex***", "MyLogin" -> "My***".
        /// </summary>
        public static string MaskAccount(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            int at = value.IndexOf('@');
            if (at > 0)
                return MaskPart(value.Substring(0, at)) + "@" + MaskPart(value.Substring(at + 1));

            return MaskPart(value);
        }

        private static string MaskPart(string part)
        {
            if (string.IsNullOrEmpty(part))
                return string.Empty;

            int visible = part.Length <= 3 ? 1 : 2;
            return part.Substring(0, visible) + "***";
        }
    }
}
