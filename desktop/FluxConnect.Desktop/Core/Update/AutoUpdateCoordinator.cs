namespace FluxConnect.Desktop.Core.Update;

public sealed class AutoUpdateResult
{
    public bool UpdateFound { get; init; }
    public string Version { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Açılışta güncellemeyi arka planda indirir. Uygulama oturum bitince kapanmaz;
/// yeni sürüm bir sonraki açılışta uygulanır, bildirim o zaman yapılır.
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

    public static Task<AutoUpdateResult> EnsureStartedAsync(string githubRepo, bool restartMinimized = false)
    {
        _ = restartMinimized;
        lock (Gate)
        {
            if (_inFlight is { IsCompleted: false })
                return _inFlight;

            _inFlight = RunCoreAsync(githubRepo);
            return _inFlight;
        }
    }

    public static bool TryApplyPendingOnStartup(bool restartMinimized)
    {
        var pending = UpdateService.FindPendingUpdate();
        if (pending == null)
            return false;

        UpdateService.ApplyUpdateAndRestart(pending.Value.Path, pending.Value.Version, restartMinimized);
        return true;
    }

    private static async Task<AutoUpdateResult> RunCoreAsync(string githubRepo)
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

            await UpdateService.DownloadUpdateAsync(info);
            return new AutoUpdateResult
            {
                UpdateFound = true,
                Version = info.Version,
                Message = $"v{info.Version} indirildi. Sonraki açılışta uygulanacak."
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
}
