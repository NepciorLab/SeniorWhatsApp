using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SeniorWhatsApp;

/// <summary>Mały panel zawsze na wierzchu: duża czerwona słuchawka w prawym dolnym rogu ekranu.</summary>
public partial class CallOverlayWindow : Window
{
    readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public event Action? HangUpRequested;

    public CallOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.MakeNoActivateToolWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => PlaceBottomRight();
        SizeChanged += (_, _) => PlaceBottomRight();
        _keepOnTop.Tick += (_, _) => Native.KeepOnTop(new WindowInteropHelper(this).Handle);
        _keepOnTop.Start();
        Closed += (_, _) => _keepOnTop.Stop();
    }

    public void SetStatus(string text) => StatusText.Text = text;

    void PlaceBottomRight()
    {
        var area = Display.WorkArea;
        Left = area.Right - ActualWidth - 24;
        Top = area.Bottom - ActualHeight - 24;
    }

    void OnHangUp(object sender, RoutedEventArgs e)
    {
        HangUpButton.IsEnabled = false;
        SetStatus("Kończę rozmowę…");
        HangUpRequested?.Invoke();
    }
}
