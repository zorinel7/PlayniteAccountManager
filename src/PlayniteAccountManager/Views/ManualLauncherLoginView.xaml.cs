using System;
using System.Windows;
using System.Windows.Controls;

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

            TitleText.Text = "Logowanie — " + launcherName;
            GameText.Text = "Gra: " + gameName +
                            "\n\nZaloguj się ręcznie na poniższe konto w " +
                            launcherName + ".";
            LoginBox.Text = username ?? string.Empty;
            PasswordBox.Text = string.IsNullOrEmpty(password) ? string.Empty : Mask;

            this.copyLogin = copyLogin;
            this.copyPassword = copyPassword;
            this.close = close;
        }

        private void LoginBox_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            copyLogin?.Invoke();
            StatusText.Text = "Login skopiowany do schowka. Wklej go w launcherze przez Ctrl+V.";
            LoginBox.SelectAll();
            e.Handled = true;
        }

        private void PasswordBox_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            copyPassword?.Invoke();
            StatusText.Text = "Hasło skopiowane do schowka. Wklej je w launcherze przez Ctrl+V.";
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
