using System.Windows;
using FluxConnect.Desktop.Core.Config;
using FluxConnect.Desktop.Core.Network;
using FluxConnect.Desktop.Core.Session;
using FluxConnect.Desktop.Core.Capture;
// WinForms + WPF birlikte kullanıldığında çakışmayı gidermek için alias
using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;
using ExitEventArgs = System.Windows.ExitEventArgs;

namespace FluxConnect.Desktop;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = null!;
    public static RelayClient Relay { get; private set; } = null!;
    public static SessionManager Session { get; private set; } = null!;
    public static LocalServer LanServer { get; private set; } = null!;
    public static LanDiscoveryHost LanDiscovery { get; private set; } = null!;
    public static StreamQualityController StreamQuality { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Config = ConfigManager.Load();
        Relay = new RelayClient();
        Session = new SessionManager(Relay, Config);

        // LAN sunucusunu başlat (port 9090)
        LanServer = new LocalServer(9090);
        LanServer.Start();

        // LAN keşif yanıtlayıcısı (port 9091)
        LanDiscovery = new LanDiscoveryHost(
            Config.HardwareId,
            Config.DisplayName,
            Config.MachineId,
            () => LanServer.GetLocalIpAddress());
        LanDiscovery.Start();

        // LAN event'lerini SessionManager'a bağla
        Session.BindLanServer(LanServer);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        LanDiscovery?.Dispose();
        LanServer?.Dispose();
        await Relay.DisconnectAsync();
        Relay.Dispose();
        base.OnExit(e);
    }
}
