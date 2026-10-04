using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace PlayniteAccountManager.EAHelper
{
    internal static class Program
    {
        private const string ServiceName = "EABackgroundService";

        private static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0 || args[0] != "--run-task")
                    return 2;

                string requestFile = GetArg(args, "--request");
                if (string.IsNullOrWhiteSpace(requestFile))
                    return WriteResult(null, false, "Brak ścieżki pliku żądania.");

                return RunOperation(requestFile);
            }
            catch (Exception ex)
            {
                string request = GetArg(args, "--request");
                return WriteResult(request, false, ex.Message);
            }
        }

        private static int RunOperation(string requestFile)
        {
            if (!File.Exists(requestFile))
                return WriteResult(requestFile, false, "Nie znaleziono pliku żądania.");

            string[] lines = File.ReadAllLines(requestFile);
            string operation = lines.Length > 0 ? (lines[0] ?? string.Empty).Trim().ToLowerInvariant() : string.Empty;
            string accountId = lines.Length > 1 ? (lines[1] ?? string.Empty).Trim() : string.Empty;
            string cacheRoot = lines.Length > 2 ? (lines[2] ?? string.Empty).Trim() : string.Empty;

            Guid id;
            if (!Guid.TryParse(accountId, out id) || id == Guid.Empty)
                return WriteResult(requestFile, false, "Nieprawidłowy identyfikator konta EA.");

            if (string.IsNullOrWhiteSpace(cacheRoot))
                return WriteResult(requestFile, false, "Brak katalogu cache EA.");

            try
            {
                StopEAService();

                if (operation == "clear")
                {
                    ClearLiveState();
                }
                else if (operation == "save")
                {
                    SaveCurrent(id, cacheRoot);
                }
                else if (operation == "restore")
                {
                    Restore(id, cacheRoot);
                }
                else
                {
                    return WriteResult(requestFile, false, "Nieznana operacja: " + operation);
                }

                return WriteResult(requestFile, true, null);
            }
            catch (UnauthorizedAccessException ex)
            {
                return WriteResult(requestFile, false, "Brak uprawnień: " + ex.Message);
            }
            catch (Exception ex)
            {
                return WriteResult(requestFile, false, ex.ToString());
            }
        }

        private static void StopEAService()
        {
            ExecuteSc("stop " + ServiceName);
            Thread.Sleep(350);
        }

        private static void ClearLiveState()
        {
            ClearDirectoryContents(GetLiveLocal(), false);
            ClearDirectoryContents(GetLiveProgram(), true);
        }

        private static void SaveCurrent(Guid accountId, string cacheRoot)
        {
            string accountRoot = Path.Combine(cacheRoot, accountId.ToString("D"));
            string cachedLocal = Path.Combine(accountRoot, "LocalAppData");
            string cachedProgram = Path.Combine(accountRoot, "ProgramData");

            DeleteIfExists(cachedLocal);
            DeleteIfExists(cachedProgram);
            Directory.CreateDirectory(accountRoot);

            if (Directory.Exists(GetLiveLocal()))
                CopyDirectoryFiltered(GetLiveLocal(), cachedLocal, false);

            if (Directory.Exists(GetLiveProgram()))
                CopyDirectoryFiltered(GetLiveProgram(), cachedProgram, true);
        }

        private static void Restore(Guid accountId, string cacheRoot)
        {
            string accountRoot = Path.Combine(cacheRoot, accountId.ToString("D"));
            if (!Directory.Exists(accountRoot))
                throw new InvalidOperationException("Brak zapisanej sesji EA.");

            ClearLiveState();

            string cachedLocal = Path.Combine(accountRoot, "LocalAppData");
            string cachedProgram = Path.Combine(accountRoot, "ProgramData");

            if (Directory.Exists(cachedLocal))
                CopyDirectoryFiltered(cachedLocal, GetLiveLocal(), false);

            if (Directory.Exists(cachedProgram))
                CopyDirectoryFiltered(cachedProgram, GetLiveProgram(), true);
        }

        private static string GetLiveLocal()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Electronic Arts", "EA Desktop");
        }

        private static string GetLiveProgram()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EA Desktop");
        }

        private static bool IsSharedTopLevel(string path)
        {
            string name = Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return name.Equals("InstallData", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Logs", StringComparison.OrdinalIgnoreCase);
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

        private static void ExecuteSc(string arguments)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    if (process == null)
                        return;

                    process.WaitForExit(3000);
                }
            }
            catch { }
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        private static int WriteResult(string requestFile, bool ok, string error)
        {
            if (string.IsNullOrWhiteSpace(requestFile))
                return ok ? 0 : 1;

            try
            {
                string resultFile = requestFile + ".result";
                string content = ok ? "OK" : "ERROR
" + (error ?? "Nieznany błąd.");
                File.WriteAllText(resultFile, content);

                return ok ? 0 : 1;
            }
            catch
            {
                return ok ? 0 : 1;
            }
        }
    }
}
