using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SeniorWhatsApp;

public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d * double.Parse((string)parameter, CultureInfo.InvariantCulture) : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1);
    static readonly TimeSpan IdleBeforeUpdate = TimeSpan.FromMinutes(5);

    readonly AppSettings _settings;
    readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly DispatcherTimer _hold = new() { Interval = HoldTime };
    readonly DispatcherTimer _holdHintTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    bool _holdActive;
    bool _inCall;
    DateTime _lastInput = DateTime.Now;

    /// <summary>Tylko do testów układu (--size 1920x1080): okno o stałym rozmiarze zamiast zmaksymalizowanego.</summary>
    public static Size? ForcedSize { get; set; }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        if (ForcedSize is { } fs)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Width = fs.Width; Height = fs.Height;
            Left = 0; Top = 0;
        }
        DataContext = settings;
        _settings.Contacts.CollectionChanged += (_, _) => UpdateDetail();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.TileWidth) or nameof(AppSettings.NameFontSize)) UpdateScales();
        };
        _noticeTimer.Tick += (_, _) => { NoticeText.Visibility = Visibility.Collapsed; _noticeTimer.Stop(); };
        _holdHintTimer.Tick += (_, _) => { _holdHintTimer.Stop(); HoldHint.Visibility = Visibility.Collapsed; };
        _hold.Tick += (_, _) => { _hold.Stop(); _holdActive = false; ResetHoldBar(); HoldHint.Visibility = Visibility.Collapsed; OpenSettings(); };
        _idleTimer.Tick += (_, _) => InstallUpdateWhenIdle();
        _idleTimer.Start();
        UpdateDetail();
    }

    Contact? Selected => ContactList.SelectedItem as Contact;

    // Program ma się zawsze uruchamiać (i pozostawać) zmaksymalizowany.
    void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && ForcedSize == null) WindowState = WindowState.Maximized;
    }

    void OnUserInput(object sender, EventArgs e) => _lastInput = DateTime.Now;

    void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateScales();

    void OnRightPaneSizeChanged(object sender, SizeChangedEventArgs e) => UpdateScales();

    // Prawa część (duży obraz, przycisk rozmowy) rośnie razem z wysokością okna, ale nie szerzej niż mieści się w prawej kolumnie
    // – dzięki temu układ jest czytelny zarówno na dużym monitorze, jak i przy 1920x1080 ze skalowaniem Windows 125–150%.
    void UpdateScales()
    {
        // Kafelki szersze niż 45% okna zmniejszamy proporcjonalnie (np. zapisane na dużym monitorze, uruchomione na małym).
        var listNatural = _settings.TileWidth + 34 /* pasek przewijania */ + 32 /* marginesy */;
        var ls = Math.Min(1.0, ActualWidth * 0.45 / listNatural);
        ContactList.LayoutTransform = ls < 1 ? new ScaleTransform(ls, ls) : Transform.Identity;

        var paneWidth = RightPane.ActualWidth > 0 ? RightPane.ActualWidth : ActualWidth - listNatural * ls;
        // Skala prawej części: wg wysokości okna, szerokości kolumny i tego, ile miejsca zajmuje stos (zdjęcie, nazwa, przycisk).
        var nameHeight = _settings.NameFontSize * 1.5 * 1.35;
        var kHeight = Math.Clamp(ActualHeight / 900.0, 0.6, 2.2);
        var kFit = (ActualHeight - (40 + 22 + nameHeight + 26 + 18 + 70)) / (340 + 190 + 36);
        var k = Math.Max(0.5, Math.Min(Math.Min(kHeight, kFit), paneWidth / 640.0));

        BigAvatar.Width = BigAvatar.Height = 340 * k;
        BigInitial.FontSize = 160 * k;
        BigName.MaxWidth = Math.Max(200, paneWidth - 60);
        CallButton.Width = 520 * k;
        CallButton.Height = 190 * k;
        VideoIcon.FontSize = 110 * k;
        PhoneIcon.FontSize = 96 * k;
        PhoneIcon.Margin = new Thickness(36 * k, 0, 0, 0);
        CallHint.FontSize = NoticeText.FontSize = 28 * k;
        NoticeText.MaxWidth = Math.Max(200, paneWidth - 60);
        // Przycisk ustawień ma połowę dawnej wielkości (na małych ekranach nie schodzimy poniżej tej wielkości).
        var sb = 0.5 * Math.Max(k, 1.0);
        SettingsButton.LayoutTransform = new ScaleTransform(sb, sb);
        // Panel "brak kamery" ma naturalną szerokość ok. 1130 px – zmieszczamy go w prawej kolumnie.
        var cam = Math.Min(k, (paneWidth - 40) / 1130.0);
        NoCameraPanel.LayoutTransform = new ScaleTransform(cam, cam);
    }

    void OnContactSelected(object sender, SelectionChangedEventArgs e) => UpdateDetail();

    void UpdateDetail()
    {
        var c = Selected;
        NoCameraPanel.Visibility = Visibility.Collapsed;
        DetailPanel.DataContext = c;
        DetailPanel.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        NoticeText.Visibility = Visibility.Collapsed;
        BigInitial.Visibility = c != null && c.Image == null ? Visibility.Visible : Visibility.Collapsed;
        if (c != null) { EmptyHint.Text = ""; return; }
        EmptyHint.Text = _settings.Contacts.Count == 0
            ? "Nie ma jeszcze żadnych osób.\nPrzytrzymaj przycisk „Ustawienia” na dole, aby dodać pierwszą."
            : "Wybierz osobę z listy po lewej stronie.";
    }

    void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeText.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ---------- ustawienia (przytrzymanie 1 s) i wyjście ----------

    // Krótkie kliknięcie tylko pokazuje podpowiedź na 3 sekundy – tak samo jak w SeniorDesktop.
    void Settings_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _holdActive = true;
        _holdHintTimer.Stop();
        HoldHint.Visibility = Visibility.Collapsed;
        _hold.Start();
        HoldScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, new Duration(HoldTime)));
    }

    void Settings_Up(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_holdActive) return;
        _holdActive = false;
        if (_hold.IsEnabled)
        {
            _hold.Stop();
            ResetHoldBar();
            HoldHint.Visibility = Visibility.Visible;
            _holdHintTimer.Stop();
            _holdHintTimer.Start();
        }
    }

    void ResetHoldBar()
    {
        HoldScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HoldScale.ScaleX = 0;
    }

    void OpenSettings()
    {
        var selectedId = Selected?.Id;
        new SettingsWindow(_settings) { Owner = this }.ShowDialog();
        SettingsStore.Save(_settings);
        ContactList.SelectedItem = _settings.Contacts.FirstOrDefault(c => c.Id == selectedId);
        UpdateDetail();
    }

    void OnExit(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_settings);
        Application.Current.Shutdown();
    }

    // ---------- automatyczna aktualizacja: restart, gdy nikt nie korzysta z programu ----------

    void InstallUpdateWhenIdle()
    {
        if (_inCall || !UpdateManager.Instance.ReadyToInstall || DateTime.Now - _lastInput < IdleBeforeUpdate) return;
        if (Application.Current.Windows.OfType<SettingsWindow>().Any()) return;
        SettingsStore.Save(_settings);
        if (UpdateService.ApplyPendingAndRestart()) Application.Current.Shutdown();
    }

    // ---------- rozmowa ----------

    async void OnCall(object sender, RoutedEventArgs e)
    {
        var contact = Selected;
        if (contact == null || _inCall) return;
        if (!ValidateCallable(contact)) return;

        if (!await CameraService.HasCameraAsync())
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            NoCameraPanel.Visibility = Visibility.Visible;
            return;
        }
        await StartCallAsync(contact, video: true);
    }

    async void OnVoiceCall(object sender, RoutedEventArgs e)
    {
        var contact = Selected;
        NoCameraPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = contact == null ? Visibility.Collapsed : Visibility.Visible;
        if (contact == null || _inCall) return;
        await StartCallAsync(contact, video: false);
    }

    void OnNoCameraBack(object sender, RoutedEventArgs e) => UpdateDetail();

    bool ValidateCallable(Contact contact)
    {
        if (string.IsNullOrWhiteSpace(contact.Phone) && string.IsNullOrWhiteSpace(contact.WaName))
        {
            ShowNotice("Ta osoba nie ma numeru telefonu. Dodaj go w ustawieniach.");
            return false;
        }
        if (!WhatsAppController.IsInstalled())
        {
            ShowNotice("Program WhatsApp nie jest zainstalowany na tym komputerze.");
            return false;
        }
        return true;
    }

    async Task StartCallAsync(Contact contact, bool video)
    {
        _inCall = true;
        WindowState = WindowState.Minimized;           // odsłania WhatsApp; czerwony przycisk zostaje na wierzchu
        string? problem;
        try { problem = await new CallSession(contact, video).RunAsync(); }
        catch (Exception ex) { Log.Write("Błąd rozmowy: " + ex); problem = "Nie udało się zadzwonić. Naciśnij zielony przycisk jeszcze raz."; }
        _inCall = false;
        _lastInput = DateTime.Now;
        SettingsStore.Save(_settings);                 // m.in. zapamiętana nazwa czatu

        WindowState = WindowState.Maximized;
        Activate();
        if (problem != null) ShowNotice(problem);
    }
}
