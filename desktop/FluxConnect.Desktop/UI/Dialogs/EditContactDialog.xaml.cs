using System.Windows;

namespace FluxConnect.Desktop.UI.Dialogs;

public partial class EditContactDialog : Window
{
    public EditContactDialog(string currentDisplayName)
    {
        InitializeComponent();
        TxtDisplayName.Text = currentDisplayName;
        Loaded += (_, _) =>
        {
            TxtDisplayName.Focus();
            TxtDisplayName.SelectAll();
        };
    }

    public string ResultDisplayName => TxtDisplayName.Text.Trim();

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtDisplayName.Text))
        {
            MessageBox.Show("Görünen ad boş olamaz.", "Uyarı",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
