using System.Windows;
using FluxConnect.Desktop.Core.Config;
using FluxConnect.Desktop.Core.Network;
using FluxConnect.Desktop.Core.Session;
using FluxConnect.Desktop.Core.Capture;
using FluxConnect.Desktop.Core.Platform;
using FluxConnect.Desktop.Core.Update;
using FluxConnect.Desktop.UI;

namespace FluxConnect.Desktop;

public partial class App : Application
{
    public const string GitHubRepo = "EmreArat/FluxConnect";

    public static AppConfig Config { get; private set; } = null!;
    public static RelayClient Relay { get; private set; } = null!;
    public static SessionManager Session { get; private set; } = null!;
    public static LocalServer LanServer { get; private set; } = null!;
    public static LanDiscoveryHost LanDiscovery { get; private set; } = null!;
    public static StreamQualityController StreamQuality { get; } = new();
    public static TrayService Tray { get; private set; } = null!;
    public static MainWindow? MainWindowInstance { get; private set; }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        Config = ConfigManager.Load();
        StartupHelper.SetEnabled(Config.StartWithWindows);

        Relay = new RelayClient();
        Session = new SessionManager(Relay, Config);

        LanServer = new LocalServer(9090);
        LanServer.Start();

        LanDiscovery = new LanDiscoveryHost(
            Config.HardwareId,
            Config.DisplayName,
            Config.MachineId,
            () => LanServer.GetLocalIpAddress());
        LanDiscovery.Start();

        Session.BindLanServer(LanServer);

        Tray = new TrayService();
        MainWindowInstance = new MainWindow();
        Tray.Attach(MainWindowInstance);
        MainWindowInstance.Closed += (_, _) => { MainWindowInstance = null; };

        if (Config.StartMinimizedToTray)
        {
            MainWindowInstance.Hide();
        }
        else
        {
            MainWindowInstance.Show();
        }

        _ = CheckForUpdatesSilentlyAsync();
    }

    private static async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            await Task.Delay(3000);
            var info = await UpdateService.CheckForUpdateAsync(GitHubRepo);
            if (info == null) return;

            await Current.Dispatcher.InvokeAsync(() =>
            {
                Tray.ShowBalloon("FluxConnect Güncelleme", $"v{info.Version} mevcut. Ayarlar'dan güncelleyebilirsiniz.");
            });
        }
        catch { }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        LanDiscovery?.Dispose();
        LanServer?.Dispose();
        await Relay.DisconnectAsync();
        Relay.Dispose();
        base.OnExit(e);
    }
}
