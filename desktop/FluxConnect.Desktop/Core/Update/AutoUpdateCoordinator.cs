using FluxConnect.Desktop.UI;

namespace FluxConnect.Desktop.Core.Update;

public sealed class AutoUpdateResult
{
    public bool UpdateFound { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Açılışta güncellemeyi arka planda indirir; oturum yoksa uygular.
/// Bildirim yalnızca işlem bittikten sonra (yeniden açılışta) yapılır.
/// </summary>
public static class AutoUpdateCoordinator
{
    private static readonly object Gate = new();
    private static Task<AutoUpdateResult>? _inFlight;

    public static bool IsRunning
    {
        get
        {
            lock (Gate)
                return _inFlight is { IsCompleted: false };
        }
    }

    public static Task<AutoUpdateResult> EnsureStartedAsync(string githubRepo, bool restartMinimized)
    {
        lock (Gate)
        {
            if (_inFlight is { IsCompleted: false })
                return _inFlight;

            _inFlight = RunCoreAsync(githubRepo, restartMinimized);
            return _inFlight;
        }
    }

    private static async Task<AutoUpdateResult> RunCoreAsync(string githubRepo, bool restartMinimized)
    {
        try
        {
            var info = await UpdateService.CheckForUpdateAsync(githubRepo);
            if (info == null)
            {
                return new AutoUpdateResult
                {
                    Message = "Güncel sürümdesiniz."
                };
            }

            var path = await UpdateService.DownloadUpdateAsync(info);
            await WaitUntilNoActiveSessionAsync();

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateService.ApplyUpdateAndRestart(path, info.Version, restartMinimized);
            });

            return new AutoUpdateResult
            {
                UpdateFound = true,
                Version = info.Version,
                Message = $"v{info.Version} uygulanıyor, uygulama yeniden başlatılacak."
            };
        }
        catch (Exception ex)
        {
            return new AutoUpdateResult
            {
                Message = $"Güncelleme başarısız: {ex.Message}"
            };
        }
    }

    private static async Task WaitUntilNoActiveSessionAsync()
    {
        var app = System.Windows.Application.Current;
        while (true)
        {
            var sessionBusy = App.Session.CurrentSession != null;
            var viewerBusy = false;
            await app.Dispatcher.InvokeAsync(() =>
            {
                viewerBusy = app.Windows.OfType<ViewerWindow>().Any();
            });

            if (!sessionBusy && !viewerBusy)
                return;

            await Task.Delay(2000);
        }
    }
}
