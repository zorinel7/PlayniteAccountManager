using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Reads the Epic Games Launcher log to find out whether the launcher signed
    /// in and with which Epic account. The launcher writes, with UTC timestamps:
    ///   [2026.10.16-01.51.04:141][ 99]FAccountService::OnLoginComplete - 0 TRUE &lt;AccountId&gt;
    /// "FALSE" means the sign-in (e.g. with an expired remembered session) failed.
    /// </summary>
    internal sealed class EpicLauncherLog
    {
        private static readonly Regex TimestampPattern = new Regex(
            @"^\[(\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}:\d{3})\]", RegexOptions.Compiled);

        private static readonly Regex LoginCompletePattern = new Regex(
            @"FAccountService::OnLoginComplete - \d+ (TRUE|FALSE)\s*([0-9A-Fa-f]{32})?", RegexOptions.Compiled);

        private readonly string logPath;

        public EpicLauncherLog()
        {
            logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EpicGamesLauncher", "Saved", "Logs", "EpicGamesLauncher.log");
        }

        /// <summary>
        /// Waits for the result of a sign-in that started after <paramref name="sinceUtc"/>.
        /// Returns true with the Epic AccountId on success.
        /// </summary>
        public bool WaitForLogin(DateTime sinceUtc, TimeSpan timeout, out string accountId, out string error)
        {
            accountId = null;
            error = null;
            // Lines of the previous launcher session (shutdown, sign-out) are older
            // than the start of this one and are ignored.
            DateTime threshold = sinceUtc;
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                string[] lines = ReadLog();
                for (int i = 0; i < lines.Length; i++)
                {
                    DateTime timestamp;
                    if (!TryGetTimestamp(lines[i], out timestamp) || timestamp < threshold)
                        continue;

                    Match login = LoginCompletePattern.Match(lines[i]);
                    if (login.Success)
                    {
                        if (login.Groups[1].Value == "TRUE")
                        {
                            accountId = login.Groups[2].Success ? login.Groups[2].Value : null;
                            return true;
                        }

                        error = "Epic Games odrzucił logowanie (zapisana sesja wygasła lub jest nieprawidłowa).";
                        return false;
                    }
                }

                Thread.Sleep(500);
            }

            error = "Epic Games nie potwierdził logowania w ciągu " + (int)timeout.TotalSeconds + " s.";
            return false;
        }

        private static bool TryGetTimestamp(string line, out DateTime timestamp)
        {
            timestamp = DateTime.MinValue;
            Match match = TimestampPattern.Match(line);
            return match.Success && DateTime.TryParseExact(
                match.Groups[1].Value,
                "yyyy.MM.dd-HH.mm.ss:fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }

        private string[] ReadLog()
        {
            try
            {
                if (!File.Exists(logPath))
                    return new string[0];

                // The launcher keeps the file open and rotates it on start.
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    return reader.ReadToEnd().Split('\n');
            }
            catch (IOException)
            {
                return new string[0];
            }
            catch (UnauthorizedAccessException)
            {
                return new string[0];
            }
        }
    }
}
