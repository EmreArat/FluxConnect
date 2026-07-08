using System;
using System.Windows;
using System.Windows.Input;

namespace FluxConnect.Desktop.UI;

public partial class FloatingTransferWindow : Window
{
    public FloatingTransferWindow()
    {
        InitializeComponent();
        
        // Sağ alt köşeye konumlandır
        this.Loaded += (s, e) =>
        {
            var desktopWorkingArea = SystemParameters.WorkArea;
            this.Left = desktopWorkingArea.Right - this.Width - 20;
            this.Top = desktopWorkingArea.Bottom - this.Height - 20;
        };
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    public void UpdateProgress(string title, string filename, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (!IsVisible) Show();
            
            TxtTitle.Text = title;
            TxtFileName.Text = filename;
            ProgressBar.Value = Math.Min(100, Math.Max(0, progress));
        });
    }

    public void Finish(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TxtTitle.Text = "Tamamlandı";
            TxtFileName.Text = message;
            ProgressBar.Value = 100;
            ProgressBar.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");

            // 3 saniye sonra kapat
            System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() => Hide());
            });
        });
    }

    public void Error(string message)
    {
        Dispatcher.Invoke(() =>
        {
            TxtTitle.Text = "Hata!";
            TxtFileName.Text = message;
            ProgressBar.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            
            System.Threading.Tasks.Task.Delay(4000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() => Hide());
            });
        });
    }
}
