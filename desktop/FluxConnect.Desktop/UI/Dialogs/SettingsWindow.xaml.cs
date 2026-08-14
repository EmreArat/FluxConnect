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
        TxtRelayUrl.Text = config.RelayUrl ?? string.Empty;
        TxtRelayFingerprint.Text = CertificatePinning.ToDisplay(config.RelayCertFingerprint ?? string.Empty);
        TxtVersion.Text = $"Sürüm: {UpdateService.CurrentVersion}";
        TxtUpdateStatus.Text = string.Empty;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var config = App.Config;

        config.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        config.StartMinimizedToTray = ChkStartMinimizedToTray.IsChecked == true;

        if (!ConfigManager.TryNormalizeRelayUrl(TxtRelayUrl.Text, out var relayUrl, out var relayError))
        {
            MessageBox.Show(relayError, "Ayarlar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!CertificatePinning.TryNormalize(TxtRelayFingerprint.Text, out var fingerprint, out var fingerprintError))
        {
            MessageBox.Show(fingerprintError, "Ayarlar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var storedFingerprint = string.IsNullOrEmpty(fingerprint) ? null : fingerprint;
        var relayChanged =
            !string.Equals(config.RelayUrl, relayUrl, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(config.RelayCertFingerprint, storedFingerprint, StringComparison.OrdinalIgnoreCase);

        config.RelayUrl = relayUrl;
        config.RelayCertFingerprint = storedFingerprint;

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
        StartupHelper.ConfigureStartup(config.StartWithWindows, config.StartMinimizedToTray);

        if (relayChanged || !App.Relay.IsConnected)
        {
            App.MainWindowInstance?.NotifyRelayReconnecting();
            _ = App.Session.ReconnectRelayAsync();
        }
        else
            _ = App.Session.RefreshRegistrationAsync();

        DialogResult = true;
        Close();
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = AutoUpdateCoordinator.IsRunning
            ? "Güncelleme arka planda sürüyor..."
            : "Kontrol ediliyor...";
        try
        {
            var result = await AutoUpdateCoordinator.EnsureStartedAsync(App.GitHubRepo, restartMinimized: false);
            TxtUpdateStatus.Text = result.Message;
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
