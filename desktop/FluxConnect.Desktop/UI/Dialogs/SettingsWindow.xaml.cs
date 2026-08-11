using System.Windows;
using FluxConnect.Desktop.Core.Config;
using FluxConnect.Desktop.Core.Security;
using FluxConnect.Desktop.Core.Platform;
using FluxConnect.Desktop.Core.Update;

namespace FluxConnect.Desktop.UI.Dialogs;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var config = App.Config;
        ChkStartWithWindows.IsChecked = config.StartWithWindows;
        ChkStartMinimizedToTray.IsChecked = config.StartMinimizedToTray;
        TxtVersion.Text = $"Sürüm: {UpdateService.CurrentVersion}";
        TxtUpdateStatus.Text = string.Empty;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var config = App.Config;

        config.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        config.StartMinimizedToTray = ChkStartMinimizedToTray.IsChecked == true;

        if (ChkClearPassword.IsChecked == true)
        {
            config.SessionPasswordHash = null;
        }
        else
        {
            var pwd = TxtSessionPassword.Password;
            var confirm = TxtSessionPasswordConfirm.Password;
            if (!string.IsNullOrEmpty(pwd) || !string.IsNullOrEmpty(confirm))
            {
                if (pwd != confirm)
                {
                    MessageBox.Show("Şifreler eşleşmiyor.", "Ayarlar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (pwd.Length < 4)
                {
                    MessageBox.Show("Şifre en az 4 karakter olmalıdır.", "Ayarlar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                config.SessionPasswordHash = PasswordHelper.Hash(pwd);
            }
        }

        ConfigManager.Save(config);
        StartupHelper.SetEnabled(config.StartWithWindows);

        _ = App.Session.RefreshRegistrationAsync();

        DialogResult = true;
        Close();
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = "Kontrol ediliyor...";
        try
        {
            var info = await UpdateService.CheckForUpdateAsync(App.GitHubRepo);
            if (info == null)
            {
                TxtUpdateStatus.Text = "Güncel sürümdesiniz.";
                return;
            }

            var dialog = new UpdateAvailableDialog(info) { Owner = this };
            dialog.ShowDialog();
            TxtUpdateStatus.Text = dialog.ResultMessage;
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = $"Kontrol başarısız: {ex.Message}";
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
