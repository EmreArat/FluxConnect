using System.IO;
using System.Windows;
using System.Windows.Input;

namespace FluxConnect.Desktop.UI;

/// <summary>
/// Ekranın sağ üst köşesinde konumlanan, sürüklenebilir, Topmost webcam overlay penceresi.
/// Hem Target hem Requester tarafında karşı tarafın kamera görüntüsünü gösterir.
/// </summary>
public partial class FloatingWebcamWindow : Window
{
    private bool _hasFrame = false;

    /// <summary>Kullanıcı kamera penceresindeki kapat (✕) işaretine bastığında tetiklenir.</summary>
    public event Action? OnUserClosed;

    public FloatingWebcamWindow(string peerName)
    {
        InitializeComponent();
        TxtTitle.Text = $"{peerName}'in Kamerası";
        PositionToTopRight();
    }

    /// <summary>Ekranın sağ üst köşesine konumla (biraz içeride)</summary>
    private void PositionToTopRight()
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 16;
        Top = screen.Top + 16;
    }

    /// <summary>Yeni webcam karesi geldi — göster</summary>
    public void UpdateFrame(System.Windows.Media.Imaging.BitmapImage frame)
    {
        ImgWebcam.Source = frame;

        if (!_hasFrame)
        {
            _hasFrame = true;
            WaitingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ---- Sürükleme ----
    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            DragMove();
    }

    // ---- Kapat ----
    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        OnUserClosed?.Invoke();
        Hide();
    }

    /// <summary>Pencereyi tamamen kapatmak yerine gizle (yeniden kullanılabilir)</summary>
    public new void Hide()
    {
        _hasFrame = false;
        WaitingOverlay.Visibility = Visibility.Visible;
        ImgWebcam.Source = null;
        base.Hide();
    }
}
