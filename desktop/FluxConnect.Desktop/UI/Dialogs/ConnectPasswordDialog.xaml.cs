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
        DialogResult = true;
        Close();
    }

    public void CloseRejected(string reason)
    {
        SetStatus(reason, true);
        DialogResult = false;
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
        DialogResult = true;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
