using System;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;

namespace PlayniteAccountManager.Views
{
    public partial class ManualLauncherLoginView : UserControl
    {
        private const string Mask = "••••••••••••";

        private readonly Action copyLogin;
        private readonly Action copyPassword;
        private readonly Action<bool> close;

        public ManualLauncherLoginView(
            string launcherName,
            string gameName,
            string username,
            string password,
            Action copyLogin,
            Action copyPassword,
            Action<bool> close)
        {
            InitializeComponent();

            TitleText.Text = string.Format(ResourceProvider.GetString("LOCPlayniteAccountManagerLoginTitle"), launcherName);
            GameText.Text = string.Format(ResourceProvider.GetString("LOCPlayniteAccountManagerLoginGameInstruction"), gameName, launcherName);
            LoginBox.Text = username ?? string.Empty;
            PasswordBox.Text = string.IsNullOrEmpty(password) ? string.Empty : Mask;

            this.copyLogin = copyLogin;
            this.copyPassword = copyPassword;
            this.close = close;
        }

        private void LoginBox_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            copyLogin?.Invoke();
            StatusText.Text = ResourceProvider.GetString("LOCPlayniteAccountManagerLoginCopied");
            LoginBox.SelectAll();
            e.Handled = true;
        }

        private void PasswordBox_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            copyPassword?.Invoke();
            StatusText.Text = ResourceProvider.GetString("LOCPlayniteAccountManagerPasswordCopied");
            PasswordBox.SelectAll();
            e.Handled = true;
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            close?.Invoke(true);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            close?.Invoke(false);
        }
    }
}
