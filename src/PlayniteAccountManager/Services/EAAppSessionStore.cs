using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace PlayniteAccountManager.Services
{
    internal sealed class EAAppSessionStore
    {
        private readonly Action<string> log;

        public EAAppSessionStore(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool ClearLiveState(out string error)
        {
            error = null;

            try
            {
                string localRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Electronic Arts", "EA Desktop");

                ClearDirectoryContents(localRoot);

                string roamingRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Electronic Arts", "EA Desktop");

                if (Directory.Exists(roamingRoot))
                    ClearDirectoryContents(roamingRoot);

                log("EA App: current-user login/cache state was cleared without an elevated helper.");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "Brak uprawnień do wyczyszczenia danych sesji EA App: " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się wyczyścić sesji EA App: " + ex.Message;
                return false;
            }
        }

        private void ClearDirectoryContents(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToList())
                DeleteFile(file);

            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToList())
            {
                ClearDirectoryContents(child);

                try { File.SetAttributes(child, FileAttributes.Normal); } catch { }
                try { Directory.Delete(child, false); }
                catch (IOException)
                {
                    // Not empty: it still holds a skipped, locked log file.
                }
            }
        }

        private void DeleteFile(string file)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    File.Delete(file);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    // A just-closed EA process can release its handles with a delay.
                    Thread.Sleep(250);
                }
                catch (IOException ex) when (IsLogFile(file))
                {
                    // Diagnostic logs are not session state; never block the game start on them.
                    log("EA App: pominięto zablokowany plik logu: " + Path.GetFileName(file) + " (" + ex.Message + ")");
                    return;
                }
            }
        }

        private static bool IsLogFile(string file)
        {
            string extension = Path.GetExtension(file);
            if (string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
                return true;

            string parent = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);
            return string.Equals(parent, "Logs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
