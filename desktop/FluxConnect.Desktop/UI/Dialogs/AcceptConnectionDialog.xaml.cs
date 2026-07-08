using System.Windows;

namespace FluxConnect.Desktop.UI.Dialogs;

public partial class AcceptConnectionDialog : Window
{
    private readonly string _sessionId;

    public AcceptConnectionDialog(
        string fromId,
        string fromDisplayName,
        string sessionId,
        bool requiresPassword)
    {
        InitializeComponent();

        _sessionId = sessionId;

        TxtFromName.Text = fromDisplayName;
        TxtFromId.Text = $"ID: {FormatId(fromId)}";

        if (requiresPassword)
        {
            PasswordPanel.Visibility = Visibility.Visible;
        }

        // Otomatik kabul ayarı kontrolü
        if (App.Config.AutoAccept)
        {
            DialogResult = true;
            Close();
        }
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnReject_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string FormatId(string id)
    {
        if (id.Length != 9) return id;
        return $"{id[..3]} {id[3..6]} {id[6..]}";
    }
}
