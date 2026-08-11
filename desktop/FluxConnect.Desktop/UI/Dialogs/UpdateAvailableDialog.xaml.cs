using System.Windows;
using FluxConnect.Desktop.Core.Update;

namespace FluxConnect.Desktop.UI.Dialogs;

public partial class UpdateAvailableDialog : Window
{
    private readonly UpdateInfo _info;
    public string ResultMessage { get; private set; } = string.Empty;

    public UpdateAvailableDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        TxtHeadline.Text = $"Yeni sürüm: v{info.Version} (mevcut: v{UpdateService.CurrentVersion})";
        TxtReleaseNotes.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "Sürüm notu yok."
            : info.ReleaseNotes;
    }

    private void BtnLater_Click(object sender, RoutedEventArgs e)
    {
        ResultMessage = "Güncelleme ertelendi.";
        DialogResult = false;
        Close();
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        BtnLater.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;

        try
        {
            var path = await UpdateService.DownloadUpdateAsync(_info, new Progress<double>(p => Progress.Value = p * 100));
            ResultMessage = "Güncelleme indirildi, uygulama yeniden başlatılıyor...";
            UpdateService.ApplyUpdateAndRestart(path);
        }
        catch (Exception ex)
        {
            ResultMessage = $"Güncelleme başarısız: {ex.Message}";
            BtnUpdate.IsEnabled = true;
            BtnLater.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }
}
