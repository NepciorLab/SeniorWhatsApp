using System.Collections.ObjectModel;
using System.Windows;

namespace SeniorWhatsApp;

public sealed class ImportItem : Observable
{
    bool _checked = true;
    public string Name { get; init; } = "";
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

public partial class ImportWindow : Window
{
    readonly AppSettings _settings;
    readonly ObservableCollection<ImportItem> _items = new();
    readonly CancellationTokenSource _cts = new();

    public ImportWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Display.FitToScreen(this);
        ItemsList.ItemsSource = _items;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    async Task LoadAsync()
    {
        if (!WhatsAppController.IsInstalled()) { StatusText.Text = "Program WhatsApp nie jest zainstalowany."; return; }
        StatusText.Text = "Otwieram WhatsApp i odczytuję listę rozmów… (to może potrwać kilkanaście sekund)";
        try
        {
            var names = await Task.Run(async () =>
            {
                if (WhatsAppController.MainWindow() == null)
                {
                    WhatsAppController.Launch(null);
                    for (var i = 0; i < 60 && WhatsAppController.MainWindow() == null; i++) await Task.Delay(500, _cts.Token);
                    await Task.Delay(4000, _cts.Token);
                }
                else
                {
                    WhatsAppController.Launch(null);   // wyciąga okno na wierzch
                    await Task.Delay(1000, _cts.Token);
                }
                return WhatsAppController.ReadChatNames(_cts.Token);
            }, _cts.Token);

            var known = _settings.Contacts.SelectMany(c => new[] { c.Name, c.WaName })
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names.Where(n => !known.Contains(n)))
                _items.Add(new ImportItem { Name = n, IsChecked = false });

            StatusText.Text = _items.Count == 0
                ? (names.Count == 0 ? "Nie udało się odczytać rozmów. Sprawdź, czy WhatsApp jest zalogowany i otwarty na zakładce „Czaty”."
                                    : "Wszystkie rozmowy z WhatsApp są już na liście.")
                : $"Zaznacz osoby, które mają się pojawić w programie ({_items.Count} do wyboru).";
            ImportButton.IsEnabled = _items.Count > 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("Import z WhatsApp: " + ex);
            StatusText.Text = "Nie udało się odczytać listy rozmów z WhatsApp.";
        }
    }

    void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var i in _items) i.IsChecked = true; }
    void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var i in _items) i.IsChecked = false; }

    void OnImport(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items.Where(i => i.IsChecked))
            _settings.Contacts.Add(new Contact { Name = item.Name, WaName = item.Name });
        Close();
    }

    void OnCancel(object sender, RoutedEventArgs e) => Close();
}
