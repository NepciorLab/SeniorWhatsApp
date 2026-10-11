using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace SeniorWhatsApp;

public partial class SettingsWindow : Window
{
    static readonly Contact Sample = new() { Name = "Przykładowa osoba" };
    readonly AppSettings _settings;
    Point _dragLast;
    bool _dragging;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Display.FitToScreen(this);
        DataContext = settings;
        List.ItemsSource = settings.Contacts;
        if (settings.Contacts.Count > 0) List.SelectedIndex = 0;
        OnContactChanged(null!, null!);

        VersionText.Text = $"Wersja programu: {AppInfo.VersionText}";
        DataPath.Text = SettingsStore.DataDir;
        var um = UpdateManager.Instance;
        um.PropertyChanged += OnUpdateChanged;
        Closed += (_, _) => um.PropertyChanged -= OnUpdateChanged;
        RefreshUpdateUi();
    }

    Contact? Current => List.SelectedItem as Contact;

    // ---------- osoby ----------

    void OnContactChanged(object sender, SelectionChangedEventArgs e)
    {
        EditCard.IsEnabled = Current != null;
        Preview.Content = Current ?? Sample;
        UpdateInitialVisibility();
    }

    void UpdateInitialVisibility() =>
        PhotoInitial.Visibility = Current is { Image: null } || Current == null ? Visibility.Visible : Visibility.Collapsed;

    void OnAdd(object sender, RoutedEventArgs e)
    {
        var c = new Contact { Name = "Nowa osoba" };
        _settings.Contacts.Add(c);
        List.SelectedItem = c;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void OnDelete(object sender, RoutedEventArgs e)
    {
        var c = Current;
        if (c == null) return;
        if (MessageBox.Show(this, $"Czy usunąć osobę „{c.Name}”?", "Usuwanie", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = List.SelectedIndex;
        DeleteImageFile(c.ImageFile);
        _settings.Contacts.Remove(c);
        if (_settings.Contacts.Count > 0) List.SelectedIndex = Math.Min(index, _settings.Contacts.Count - 1);
    }

    void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    void Move(int delta)
    {
        var i = List.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _settings.Contacts.Count) return;
        _settings.Contacts.Move(i, j);
        List.SelectedIndex = j;
    }

    void OnImport(object sender, RoutedEventArgs e)
    {
        var before = _settings.Contacts.Count;
        new ImportWindow(_settings) { Owner = this }.ShowDialog();
        if (_settings.Contacts.Count > before) List.SelectedIndex = before;
    }

    /// <summary>Opiekun jednorazowo potwierdza, że numer prowadzi do właściwej osoby; nazwa czatu jest zapisywana i potem sprawdzana przy każdym połączeniu.</summary>
    async void OnVerifyChat(object sender, RoutedEventArgs e)
    {
        var c = Current;
        if (c == null) return;
        if (string.IsNullOrWhiteSpace(c.Phone))
        {
            MessageBox.Show(this, "Najpierw wpisz numer telefonu tej osoby.", "Sprawdź w WhatsApp", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!WhatsAppController.IsInstalled())
        {
            MessageBox.Show(this, "Program WhatsApp nie jest zainstalowany.", "Sprawdź w WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        VerifyButton.IsEnabled = false;
        VerifyStatus.Text = "Otwieram WhatsApp…";
        string? title = null;
        try
        {
            title = await Task.Run(async () =>
            {
                var cold = WhatsAppController.MainWindow() == null;
                WhatsAppController.Launch(c.Phone);
                FlaUI.Core.AutomationElements.Window? main = null;
                for (var i = 0; i < 60 && main == null; i++) { await Task.Delay(500); main = WhatsAppController.MainWindow(); }
                if (main == null) return null;
                await Task.Delay(cold ? 6000 : 2500);
                string? last = null, t = null;
                var stable = 0;
                for (var i = 0; i < 20 && stable < 2; i++)
                {
                    t = WhatsAppController.ChatTitle(main);
                    stable = t != null && t == last ? stable + 1 : 0;
                    last = t;
                    await Task.Delay(500);
                }
                return t;
            });
        }
        catch (Exception ex) { Log.Write("Sprawdź w WhatsApp: " + ex.Message); }

        Activate();
        VerifyButton.IsEnabled = true;
        VerifyStatus.Text = "";
        if (title == null)
        {
            MessageBox.Show(this, "Nie udało się odczytać rozmowy w WhatsApp. Sprawdź, czy WhatsApp jest zalogowany, i spróbuj jeszcze raz.",
                "Sprawdź w WhatsApp", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var nl = Environment.NewLine;
        var answer = MessageBox.Show(this,
            $"WhatsApp pokazuje rozmowę z: „{title}”.{nl}{nl}Czy to właściwa osoba (numer {c.Phone})?{nl}{nl}" +
            "Po potwierdzeniu program będzie przed każdym połączeniem sprawdzał, czy otwarta jest ta rozmowa.",
            "Sprawdź w WhatsApp", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) { c.WaName = title; VerifyStatus.Text = "Zapisano ✔"; }
    }

    // ---------- zdjęcie ----------

    void OnPickImage(object sender, RoutedEventArgs e)
    {
        var c = Current;
        if (c == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Wybierz zdjęcie osoby",
            Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|Wszystkie pliki|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var newFile = SettingsStore.ImportImage(dlg.FileName);
            var old = c.ImageFile;
            c.SetImage(newFile);
            DeleteImageFile(old);
            UpdateInitialVisibility();
        }
        catch (Exception ex)
        {
            Log.Write("Import obrazu: " + ex.Message);
            MessageBox.Show(this, "Nie udało się wczytać tego zdjęcia.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnRemoveImage(object sender, RoutedEventArgs e)
    {
        var c = Current;
        if (c == null) return;
        var old = c.ImageFile;
        c.SetImage("");
        DeleteImageFile(old);
        UpdateInitialVisibility();
    }

    void OnResetCrop(object sender, RoutedEventArgs e) => Current?.ResetCrop();

    void OnRotateLeft(object sender, RoutedEventArgs e) { if (Current is { Image: not null } c) c.Rotate(clockwise: false); }
    void OnRotateRight(object sender, RoutedEventArgs e) { if (Current is { Image: not null } c) c.Rotate(clockwise: true); }

    // Przeciąganie zdjęcia w ramce: zdjęcie "jedzie" za myszą. Kółko myszy zmienia powiększenie.
    void OnPhotoDown(object sender, MouseButtonEventArgs e)
    {
        if (Current?.Image == null) return;
        _dragging = true;
        _dragLast = e.GetPosition(PhotoFrame);
        PhotoFrame.CaptureMouse();
    }

    void OnPhotoMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Current is not { } c) return;
        var p = e.GetPosition(PhotoFrame);
        c.Pan((p.X - _dragLast.X) / PhotoFrame.ActualWidth, (p.Y - _dragLast.Y) / PhotoFrame.ActualHeight);
        _dragLast = p;
    }

    void OnPhotoUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        PhotoFrame.ReleaseMouseCapture();
    }

    void OnPhotoWheel(object sender, MouseWheelEventArgs e)
    {
        if (Current is not { } c || c.Image == null) return;
        c.ImageZoom *= Math.Pow(1.1, e.Delta / 120.0);
        e.Handled = true;
    }

    static void DeleteImageFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        try { File.Delete(Path.Combine(SettingsStore.ImagesDir, file)); } catch { }
    }

    // ---------- aktualizacje ----------

    void OnUpdateChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshUpdateUi);

    void RefreshUpdateUi()
    {
        var um = UpdateManager.Instance;
        UpdateStatus.Text = um.Status;
        CheckUpdateButton.IsEnabled = !um.Busy;
        InstallUpdateButton.Visibility = um.ReadyToInstall ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnCheckUpdate(object sender, RoutedEventArgs e) => await UpdateManager.Instance.CheckAndDownloadAsync(manual: true);

    void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_settings);
        if (UpdateService.ApplyPendingAndRestart()) Application.Current.Shutdown();
        else MessageBox.Show(this, "Nie udało się zainstalować aktualizacji. Szczegóły są w pliku log.txt.", "Aktualizacja", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SettingsStore.DataDir) { UseShellExecute = true }); } catch { }
    }

    void OnDone(object sender, RoutedEventArgs e) => Close();

    void OnQuit(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_settings);
        Application.Current.Shutdown();
    }
}
