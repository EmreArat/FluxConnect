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
        // Diyalog / izleyici kapanınca süreç bitmesin; çıkış yalnızca tepsi menüsünden.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Config = ConfigManager.Load();

        var launchedMinimized = e.Args.Any(arg =>
            arg.Equals(StartupHelper.MinimizedArg, StringComparison.OrdinalIgnoreCase));
        var updatedVersion = ParseUpdatedVersion(e.Args);
        if (updatedVersion == null && AutoUpdateCoordinator.TryApplyPendingOnStartup(launchedMinimized))
            return;

        StartupHelper.ConfigureStartup(Config.StartWithWindows, Config.StartMinimizedToTray);

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
        MainWindow = MainWindowInstance;
        Tray.Attach(MainWindowInstance);
        MainWindowInstance.Closed += (_, _) => { MainWindowInstance = null; };

        // Tepsiye küçültme yalnızca Windows başlangıcında (--minimized) geçerli;
        // exe'ye çift tıklayınca ana pencere açılır.
        // Hide() öncesi Show() gerekir; aksi halde pencere Application.Windows'a girmez.
        MainWindowInstance.Show();
        if (Config.StartMinimizedToTray && launchedMinimized)
        {
            MainWindowInstance.Hide();
            if (updatedVersion == null)
            {
                Tray.ShowBalloon(
                    "FluxConnect",
                    "Uygulama tepside çalışıyor. Açmak için simgeye çift tıklayın.");
            }
        }

        if (updatedVersion != null)
        {
            Tray.ShowBalloon(
                "FluxConnect",
                $"v{updatedVersion} sürümüne güncellendi.",
                8000);
        }

        _ = CheckAndApplyUpdatesSilentlyAsync(launchedMinimized);
    }

    private static string? ParseUpdatedVersion(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(UpdateService.UpdatedArg, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                return args[i + 1].TrimStart('v', 'V');

            return UpdateService.CurrentVersion;
        }

        return null;
    }

    private static async Task CheckAndApplyUpdatesSilentlyAsync(bool restartMinimized)
    {
        try
        {
            await Task.Delay(3000);
            await AutoUpdateCoordinator.EnsureStartedAsync(GitHubRepo, restartMinimized);
        }
        catch
        {
            // Kontrol/indirme hataları sessiz; bildirim yalnızca başarılı güncelleme sonrası.
        }
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
