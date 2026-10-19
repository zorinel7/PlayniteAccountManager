using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Executable path of another process. Playnite runs as a 32-bit process, where
    /// Process.MainModule throws for 64-bit launchers; QueryFullProcessImageName works
    /// across bitness and needs only limited query rights.
    /// </summary>
    internal static class ProcessPaths
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;

        public static string Get(Process process)
        {
            if (process == null)
                return null;

            try
            {
                IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        var buffer = new StringBuilder(1024);
                        int size = buffer.Capacity;
                        if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                            return buffer.ToString(0, size);
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
            catch
            {
            }

            try { return process.MainModule.FileName; }
            catch { return null; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
