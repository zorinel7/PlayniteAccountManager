using System;
using System.IO;

namespace PlayniteAccountManager.Services
{
    internal sealed class EAAppSessionStore
    {
        private readonly EAAppElevatedHelper helper;

        public EAAppSessionStore(string pluginUserDataPath, Action<string> log)
        {
            helper = new EAAppElevatedHelper(pluginUserDataPath, log);
        }

        public bool ClearLiveState(out string error)
        {
            return helper.RunClear(out error);
        }

        public bool IsReady()
        {
            return helper.IsInstalled();
        }

        public bool EnsureReady(out string error)
        {
            return helper.EnsureInstalledInteractive(out error);
        }
    }
}
