using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using FluxConnect.Desktop.UI;

namespace FluxConnect.Desktop.Core.Platform;

/// <summary>Sistem tepsi simgesi ve menüsü.</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private MainWindow? _mainWindow;
    private bool _disposed;

    public TrayService()
    {
        _icon = new NotifyIcon
        {
            Text = "FluxConnect",
            Icon = LoadAppIcon(),
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Aç", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Ayarlar", null, (_, _) => ShowSettings());
        menu.Items.Add("-");
        menu.Items.Add("Çıkış", null, (_, _) => ExitApplication());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowMainWindow();
    }

    public void Attach(MainWindow mainWindow) => _mainWindow = mainWindow;

    public void ShowBalloon(string title, string message, int timeoutMs = 4000)
    {
        _icon.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Info);
    }

    public void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Dispatcher.Invoke(() =>
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    public void ShowSettings()
    {
        ShowMainWindow();
        _mainWindow?.Dispatcher.Invoke(() => _mainWindow.OpenSettings());
    }

    public void ExitApplication()
    {
        if (_mainWindow != null)
        {
            _mainWindow.Dispatcher.Invoke(() => _mainWindow.RequestExit());
            return;
        }

        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/app.ico");
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
                return new Icon(stream);
        }
        catch { }

        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null)
                    return extracted;
            }
        }
        catch { }

        return SystemIcons.Application;
    }
}
