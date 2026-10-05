using System;
using System.IO;
using System.Linq;

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

        private static void ClearDirectoryContents(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToList())
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                File.Delete(file);
            }

            foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToList())
            {
                ClearReadOnlyAttributes(child);
                Directory.Delete(child, true);
            }
        }

        private static void ClearReadOnlyAttributes(string directory)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(child, FileAttributes.Normal); } catch { }
                }

                try { File.SetAttributes(directory, FileAttributes.Normal); } catch { }
            }
            catch
            {
            }
        }
    }
}
