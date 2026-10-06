using System;
using System.Collections.Generic;
using System.IO;

namespace PlayniteAccountManager.Services
{
    internal sealed class RockstarGamesLauncherSessionStore
    {
        private readonly Action<string> log;

        public RockstarGamesLauncherSessionStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        public bool ClearLiveState(out string error)
        {
            error = null;

            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                // Rockstar documents these two folders as local launcher/profile data.
                ClearDirectoryContents(Path.Combine(documents, "Rockstar Games", "Social Club"));
                ClearDirectoryContents(Path.Combine(documents, "Rockstar Games", "Launcher"));

                // Current launcher state/cache and older per-user locations.
                ClearDirectoryContents(Path.Combine(localAppData, "Rockstar Games", "Launcher"));
                ClearDirectoryContents(Path.Combine(localAppData, "Rockstar Games", "Social Club"));

                if (Directory.Exists(Path.Combine(roamingAppData, "Rockstar Games", "Launcher")))
                    ClearDirectoryContents(Path.Combine(roamingAppData, "Rockstar Games", "Launcher"));

                log("Rockstar Games Launcher: current-user local profile/session state was cleared without touching installed game folders.");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "Brak uprawnień do wyczyszczenia danych sesji Rockstar Games Launcher: " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się wyczyścić sesji Rockstar Games Launcher: " + ex.Message;
                return false;
            }
        }

        private static void ClearDirectoryContents(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string file in SafeEnumerateFiles(directory))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                File.Delete(file);
            }

            foreach (string child in SafeEnumerateDirectories(directory))
            {
                ClearReadOnlyAttributes(child);
                Directory.Delete(child, true);
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string directory)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    yield return file;
            }
            catch
            {
                yield break;
            }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string directory)
        {
            try
            {
                foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                    yield return child;
            }
            catch
            {
                yield break;
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
