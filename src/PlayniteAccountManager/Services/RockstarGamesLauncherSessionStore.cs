using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

                // Rockstar keeps the remembered account/local profile under the user's
                // Documents tree. Documents may be redirected to OneDrive, so check all
                // relevant known-folder variants instead of relying on MyDocuments alone.
                foreach (string documentsRoot in GetDocumentsRoots(documents))
                {
                    string rockstarRoot = Path.Combine(documentsRoot, "Rockstar Games");

                    // The Profiles directories contain authentication/profile files such as
                    // autosignin.dat and signintransfer.dat. Clearing the parent launcher
                    // folders also removes these files without touching game folders.
                    ClearDirectoryContents(Path.Combine(rockstarRoot, "Social Club"));
                    ClearDirectoryContents(Path.Combine(rockstarRoot, "Launcher"));
                }

                // Current launcher state/cache and older per-user locations.
                ClearDirectoryContents(Path.Combine(localAppData, "Rockstar Games", "Launcher"));
                ClearDirectoryContents(Path.Combine(localAppData, "Rockstar Games", "Social Club"));

                if (Directory.Exists(Path.Combine(roamingAppData, "Rockstar Games", "Launcher")))
                    ClearDirectoryContents(Path.Combine(roamingAppData, "Rockstar Games", "Launcher"));

                log("Rockstar Games Launcher: current-user local profile/session state was cleared, including remembered-account profile files, without touching installed game folders.");
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

        private static IEnumerable<string> GetDocumentsRoots(string primaryDocuments)
        {
            var roots = new List<string>();

            AddDocumentRoot(roots, primaryDocuments);

            string userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            AddDocumentRoot(roots, Path.Combine(userProfile ?? string.Empty, "Documents"));

            string oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            AddDocumentRoot(roots, Path.Combine(oneDrive ?? string.Empty, "Documents"));

            string oneDriveConsumer = Environment.GetEnvironmentVariable("OneDriveConsumer");
            AddDocumentRoot(roots, Path.Combine(oneDriveConsumer ?? string.Empty, "Documents"));

            return roots;
        }

        private static void AddDocumentRoot(List<string> roots, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return; }

            if (!roots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                roots.Add(fullPath);
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
