using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FluxConnect.Desktop.UI.Controls;

public partial class WindowTitleBar : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(WindowTitleBar),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowIconProperty =
        DependencyProperty.Register(nameof(ShowIcon), typeof(bool), typeof(WindowTitleBar),
            new PropertyMetadata(true, OnShowIconChanged));

    public static readonly DependencyProperty ShowMinimizeProperty =
        DependencyProperty.Register(nameof(ShowMinimize), typeof(bool), typeof(WindowTitleBar),
            new PropertyMetadata(true));

    public static readonly DependencyProperty ShowMaximizeProperty =
        DependencyProperty.Register(nameof(ShowMaximize), typeof(bool), typeof(WindowTitleBar),
            new PropertyMetadata(true));

    public static readonly DependencyProperty ShowCloseProperty =
        DependencyProperty.Register(nameof(ShowClose), typeof(bool), typeof(WindowTitleBar),
            new PropertyMetadata(true));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool ShowIcon
    {
        get => (bool)GetValue(ShowIconProperty);
        set => SetValue(ShowIconProperty, value);
    }

    public bool ShowMinimize
    {
        get => (bool)GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    public bool ShowMaximize
    {
        get => (bool)GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    public bool ShowClose
    {
        get => (bool)GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
    }

    public WindowTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window == null) return;

        if (string.IsNullOrEmpty(Title))
            Title = window.Title;

        window.StateChanged += (_, _) => UpdateMaximizeIcon();
        UpdateMaximizeIcon();
        UpdateIconVisibility();
    }

    private static void OnShowIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowTitleBar bar)
            bar.UpdateIconVisibility();
    }

    private void UpdateIconVisibility()
    {
        AppIcon.Visibility = ShowIcon ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMaximizeIcon()
    {
        var window = Window.GetWindow(this);
        if (window == null || !ShowMaximize) return;

        var maximized = window.WindowState == WindowState.Maximized;
        BtnMaximize.Content = maximized ? "\uE923" : "\uE922";
        BtnMaximize.ToolTip = maximized ? "Pencereyi geri yükle" : "Ekranı kapla";
    }

    private Window? HostWindow => Window.GetWindow(this);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var window = HostWindow;
        if (window == null) return;

        if (e.ClickCount == 2 && ShowMaximize &&
            window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            ToggleMaximize();
            return;
        }

        if (window.WindowState == WindowState.Maximized)
            return;

        try { window.DragMove(); } catch { /* DragMove sırasında tuş bırakılırsa */ }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        var window = HostWindow;
        if (window == null) return;
        window.WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        var window = HostWindow;
        if (window == null || !ShowMaximize) return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeIcon();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => HostWindow?.Close();
}
