using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Stores EA App login state per Playnite account.
    /// The model follows TcNo Account Switcher's EA Desktop setup:
    /// close the launcher, swap its local login files, then launch it again.
    /// </summary>
    internal sealed class EAAppSessionStore
    {
        private readonly string rootPath;
        private readonly Action<string> log;
        private readonly string liveLocal;
        private readonly string liveProgram;

        public EAAppSessionStore(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });
            rootPath = Path.Combine(pluginUserDataPath, "EAAccountSessions");
            liveLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Electronic Arts", "EA Desktop");
            liveProgram = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EA Desktop");

            Directory.CreateDirectory(rootPath);
        }

        public bool HasSnapshot(Guid accountId)
        {
            string accountRoot = GetAccountRoot(accountId);
            return Directory.Exists(Path.Combine(accountRoot, "LocalAppData")) ||
                   Directory.Exists(Path.Combine(accountRoot, "ProgramData"));
        }

        public bool Restore(Guid accountId, out string error)
        {
            error = null;
            if (!HasSnapshot(accountId))
            {
                error = "Brak zapisanej sesji EA dla tego konta.";
                return false;
            }

            if (!ClearLiveState(out error))
                return false;

            try
            {
                string accountRoot = GetAccountRoot(accountId);
                string cachedLocal = Path.Combine(accountRoot, "LocalAppData");
                string cachedProgram = Path.Combine(accountRoot, "ProgramData");

                if (Directory.Exists(cachedLocal))
                    CopyDirectoryFiltered(cachedLocal, liveLocal, false);

                if (Directory.Exists(cachedProgram))
                    CopyDirectoryFiltered(cachedProgram, liveProgram, true);

                log("EA App: przywrócono zapisany stan sesji konta.");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Brak uprawnień do przywrócenia sesji EA. ProgramData wymaga uprawnień administratora.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się przywrócić zapisanej sesji EA: " + ex.Message;
                return false;
            }
        }

        public bool ClearLiveState(out string error)
        {
            error = null;
            try
            {
                ClearDirectoryContents(liveLocal, false);
                ClearDirectoryContents(liveProgram, true);

                if (HasNonSharedEntries(liveLocal) || HasNonSharedEntries(liveProgram))
                {
                    error = "Nie udało się wyczyścić stanu logowania EA. Uruchom Playnite jako administrator i upewnij się, że EABackgroundService jest zatrzymany.";
                    return false;
                }

                log("EA App: bieżący stan logowania został wyczyszczony.");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Brak uprawnień do plików EA App. Uruchom Playnite jako administrator.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Błąd czyszczenia stanu EA App: " + ex.Message;
                return false;
            }
        }

        public bool SaveCurrent(Guid accountId, out string error)
        {
            error = null;
            try
            {
                string accountRoot = GetAccountRoot(accountId);
                string cachedLocal = Path.Combine(accountRoot, "LocalAppData");
                string cachedProgram = Path.Combine(accountRoot, "ProgramData");

                DeleteIfExists(cachedLocal);
                DeleteIfExists(cachedProgram);
                Directory.CreateDirectory(accountRoot);

                if (Directory.Exists(liveLocal))
                    CopyDirectoryFiltered(liveLocal, cachedLocal, false);

                if (Directory.Exists(liveProgram))
                    CopyDirectoryFiltered(liveProgram, cachedProgram, true);

                log("EA App: zapisano stan sesji konta.");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Brak uprawnień do zapisania sesji EA. ProgramData wymaga uprawnień administratora.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Nie udało się zapisać sesji EA: " + ex.Message;
                return false;
            }
        }

        public void Forget(Guid accountId)
        {
            DeleteIfExists(GetAccountRoot(accountId));
        }

        private string GetAccountRoot(Guid accountId)
        {
            return Path.Combine(rootPath, accountId.ToString("D"));
        }

        private static bool IsSharedTopLevel(string path)
        {
            string name = Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return name.Equals("InstallData", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Logs", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasNonSharedEntries(string directory)
        {
            if (!Directory.Exists(directory))
                return false;

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!IsSharedTopLevel(entry))
                    return true;
            }

            return false;
        }

        private static void ClearDirectoryContents(string directory, bool preserveShared)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string file in Directory.EnumerateFiles(
                directory, "*", SearchOption.TopDirectoryOnly).ToList())
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            foreach (string child in Directory.EnumerateDirectories(
                directory, "*", SearchOption.TopDirectoryOnly).ToList())
            {
                if (preserveShared && IsSharedTopLevel(child))
                    continue;

                ClearReadOnlyAttributes(child);
                Directory.Delete(child, true);
            }
        }

        private static void CopyDirectoryFiltered(string source, string destination, bool preserveShared)
        {
            Directory.CreateDirectory(destination);

            foreach (string file in Directory.EnumerateFiles(
                source, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase))
                    continue;

                string target = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, target, true);
            }

            foreach (string child in Directory.EnumerateDirectories(
                source, "*", SearchOption.TopDirectoryOnly))
            {
                if (preserveShared && IsSharedTopLevel(child))
                    continue;

                string target = Path.Combine(destination, Path.GetFileName(child));
                CopyDirectoryFiltered(child, target, false);
            }
        }

        private static void ClearReadOnlyAttributes(string directory)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(
                    directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
            }
            catch { }
        }

        private static void DeleteIfExists(string path)
        {
            if (!Directory.Exists(path))
                return;

            ClearReadOnlyAttributes(path);
            Directory.Delete(path, true);
        }
    }
}
