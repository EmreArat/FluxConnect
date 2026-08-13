using System.Windows;
using FluxConnect.Desktop.Core.Security;

namespace FluxConnect.Desktop.UI.Dialogs;

public partial class ConnectPasswordDialog : Window
{
    public string? SubmittedPasswordHash { get; private set; }

    public ConnectPasswordDialog()
    {
        InitializeComponent();
    }

    public void SetStatus(string message, bool isError = true)
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "DangerBrush" : "AccentBrush");
    }

    public void CloseSuccess()
    {
        if (!IsVisible) return;
        Close();
    }

    public void CloseRejected(string reason)
    {
        if (!IsVisible) return;
        SetStatus(reason, true);
        Close();
    }

    private void BtnSubmit_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtPassword.Password))
        {
            SetStatus("Şifre girin veya Kapat'a basın.");
            return;
        }

        SubmittedPasswordHash = PasswordHelper.Hash(TxtPassword.Password);
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
