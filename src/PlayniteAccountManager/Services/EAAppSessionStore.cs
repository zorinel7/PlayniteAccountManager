using System;
using System.IO;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// EA session cache. The actual read/write of EA's protected ProgramData
    /// and user AppData is delegated to the per-user elevated helper. Playnite
    /// itself never runs elevated.
    /// </summary>
    internal sealed class EAAppSessionStore
    {
        private readonly string rootPath;
        private readonly Action<string> log;
        private readonly EAAppElevatedHelper elevatedHelper;

        public EAAppSessionStore(string pluginUserDataPath, Action<string> log)
        {
            this.log = log ?? (_ => { });

            rootPath = Path.Combine(pluginUserDataPath, "EAAccountSessions");
            Directory.CreateDirectory(rootPath);

            elevatedHelper = new EAAppElevatedHelper(pluginUserDataPath, this.log);
        }

        public bool IsReady()
        {
            return elevatedHelper.IsInstalled();
        }

        public bool EnsureReady(out string error)
        {
            return elevatedHelper.EnsureInstalledInteractive(out error);
        }

        public bool HasSnapshot(Guid accountId)
        {
            if (accountId == Guid.Empty)
                return false;

            string accountRoot = GetAccountRoot(accountId);

            return Directory.Exists(Path.Combine(accountRoot, "LocalAppData")) ||
                   Directory.Exists(Path.Combine(accountRoot, "ProgramData"));
        }

        public bool Restore(Guid accountId, out string error)
        {
            return elevatedHelper.Run("restore", accountId, out error);
        }

        public bool ClearLiveState(out string error)
        {
            return elevatedHelper.Run("clear", Guid.Empty, out error);
        }

        public bool SaveCurrent(Guid accountId, out string error)
        {
            return elevatedHelper.Run("save", accountId, out error);
        }

        public void Forget(Guid accountId)
        {
            if (accountId == Guid.Empty)
                return;

            string accountRoot = GetAccountRoot(accountId);

            try
            {
                if (Directory.Exists(accountRoot))
                    Directory.Delete(accountRoot, true);
            }
            catch (Exception ex)
            {
                log("EA App: nie udało się usunąć snapshotu konta: " + ex.Message);
            }
        }

        private string GetAccountRoot(Guid accountId)
        {
            return Path.Combine(rootPath, accountId.ToString("D"));
        }
    }
}
