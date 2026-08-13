using System.Windows;

namespace FluxConnect.Desktop.UI.Dialogs;

public partial class AcceptConnectionDialog : Window
{
    public bool Accepted { get; private set; }

    public AcceptConnectionDialog(
        string fromId,
        string fromDisplayName,
        string sessionId,
        bool requiresPassword)
    {
        InitializeComponent();

        TxtFromName.Text = fromDisplayName;
        TxtFromId.Text = fromId == "LAN" ? "Yerel Ağ" : $"ID: {FormatId(fromId)}";

        if (requiresPassword)
        {
            PasswordPanel.Visibility = Visibility.Visible;
        }
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void BtnReject_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }

    private static string FormatId(string id)
    {
        if (id.Length != 9) return id;
        return $"{id[..3]} {id[3..6]} {id[6..]}";
    }
}
