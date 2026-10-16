using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PlayniteAccountManager.Services
{
    /// <summary>
    /// Copies credentials to the Windows clipboard without leaving them there.
    /// The value is excluded from Windows clipboard history (Win+V), cloud
    /// clipboard sync and clipboard monitors, and is removed automatically
    /// after a short time if the clipboard still contains it.
    /// Must be called on the WPF UI thread.
    /// </summary>
    internal static class SecureClipboard
    {
        public static readonly TimeSpan DefaultClearDelay = TimeSpan.FromSeconds(60);

        private static DispatcherTimer clearTimer;
        private static string lastCopiedValue;

        public static void SetSensitiveText(string text)
        {
            SetSensitiveText(text, DefaultClearDelay);
        }

        public static void SetSensitiveText(string text, TimeSpan clearAfter)
        {
            text = text ?? string.Empty;

            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Text, text);

            // https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));

            Clipboard.SetDataObject(data, true);

            lastCopiedValue = text;
            RestartTimer(clearAfter);
        }

        /// <summary>
        /// Clears the clipboard only when it still holds the value copied by
        /// this class, so user content copied later is never removed.
        /// </summary>
        public static void ClearIfOwned()
        {
            StopTimer();

            string expected = lastCopiedValue;
            lastCopiedValue = null;

            if (string.IsNullOrEmpty(expected))
                return;

            try
            {
                if (Clipboard.ContainsText() && string.Equals(Clipboard.GetText(), expected, StringComparison.Ordinal))
                    Clipboard.Clear();
            }
            catch
            {
                // Clipboard can be temporarily locked by another process.
            }
        }

        private static void RestartTimer(TimeSpan clearAfter)
        {
            StopTimer();

            clearTimer = new DispatcherTimer { Interval = clearAfter };
            clearTimer.Tick += (sender, args) => ClearIfOwned();
            clearTimer.Start();
        }

        private static void StopTimer()
        {
            if (clearTimer == null)
                return;

            clearTimer.Stop();
            clearTimer = null;
        }
    }
}
