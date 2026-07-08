using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FluxConnect.Desktop.Core.Native;

namespace FluxConnect.Desktop.UI.Helpers;

/// <summary>Webcam indirme sırasında ikon butonunda animasyon gösterir.</summary>
public static class WebcamDownloadUiHelper
{
    private static readonly Dictionary<Button, Storyboard> ActiveAnimations = new();

    /// <summary>Kullanıcı onayı + indirme + animasyon. Başarılıysa webcam açılabilir.</summary>
    public static async Task<bool> EnsureOpenCvWithUiAsync(Button webcamButton, Window owner)
    {
        if (OpenCvNativeManager.IsInstalled)
        {
            OpenCvNativeManager.RegisterSearchPath();
            return true;
        }

        if (!OpenCvNativeManager.RequestUserConsent(owner))
            return false;

        StartDownloadingAnimation(webcamButton);
        try
        {
            return await OpenCvNativeManager.DownloadAndInstallAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                owner,
                $"OpenCV bileşenleri indirilemedi.\n\n{ex.Message}",
                "FluxConnect",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            StopDownloadingAnimation(webcamButton);
        }
    }

    public static void StartDownloadingAnimation(Button btn)
    {
        StopDownloadingAnimation(btn);

        btn.IsEnabled = false;
        btn.Content = "📥";
        btn.ToolTip = "OpenCV bileşenleri indiriliyor...";
        btn.Opacity = 1.0;

        var rotate = new RotateTransform(0);
        btn.RenderTransform = rotate;
        btn.RenderTransformOrigin = new Point(0.5, 0.5);

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(spin, btn);
        Storyboard.SetTargetProperty(spin, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));

        var pulse = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
        Storyboard.SetTarget(pulse, btn);
        Storyboard.SetTargetProperty(pulse, new PropertyPath(UIElement.OpacityProperty));

        var board = new Storyboard();
        board.Children.Add(spin);
        board.Children.Add(pulse);
        board.Begin(btn, true);

        ActiveAnimations[btn] = board;
    }

    public static void StopDownloadingAnimation(Button btn)
    {
        if (ActiveAnimations.TryGetValue(btn, out var board))
        {
            board.Stop(btn);
            ActiveAnimations.Remove(btn);
        }

        btn.BeginAnimation(UIElement.OpacityProperty, null);
        btn.RenderTransform = null;
        btn.IsEnabled = true;
        btn.Content = "📷";
        btn.ToolTip = "Webcam (Kapalı)";
        btn.Opacity = 1.0;
    }
}
