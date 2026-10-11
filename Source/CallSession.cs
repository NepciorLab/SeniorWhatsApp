using FlaUI.Core.AutomationElements;

namespace SeniorWhatsApp;

/// <summary>
/// Jedna rozmowa: otwiera WhatsApp (także gdy trzeba go najpierw uruchomić), sprawdza z kim, startuje połączenie,
/// pilnuje końca i obsługuje czerwony przycisk. Osoba korzystająca z programu nie ma kogo poprosić o pomoc,
/// dlatego nieudane kroki są ponawiane automatycznie.
/// </summary>
internal sealed class CallSession
{
    const int MaxAttempts = 3;

    readonly Contact _contact;
    readonly bool _video;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public CallSession(Contact contact, bool video)
    {
        _contact = contact;
        _video = video;
    }

    void Step(string what) => Log.Write($"[{_clock.Elapsed.TotalSeconds:0.0}s] {what}");

    /// <returns>null gdy wszystko poszło dobrze, w przeciwnym razie komunikat dla użytkownika.</returns>
    public async Task<string?> RunAsync()
    {
        using var cts = new CancellationTokenSource();
        var overlay = new CallOverlayWindow();
        var hangUpPressed = false;
        var clicked = false;
        overlay.HangUpRequested += () => { hangUpPressed = true; cts.Cancel(); };
        overlay.SetStatus($"Łączę z:\n{_contact.Name}…");
        overlay.Show();

        void Status(string s) => overlay.Dispatcher.BeginInvoke(() => overlay.SetStatus(s));

        string? problem = null;
        try
        {
            var work = Task.Run(() => WorkAsync(Status, () => clicked = true, cts.Token));
            var cancelled = new TaskCompletionSource();
            cts.Token.Register(() => cancelled.TrySetResult());

            // Po naciśnięciu czerwonego przycisku nie czekamy na zakończenie pracy w tle – automatyzacja WhatsApp
            // potrafi się zawiesić na kilka sekund, a przycisk musi reagować od razu.
            if (await Task.WhenAny(work, cancelled.Task) == work)
                problem = await work;
            else
                _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("Błąd w CallSession: " + ex);
            problem = "Nie udało się zadzwonić. Naciśnij zielony przycisk jeszcze raz.";
        }

        if (hangUpPressed) await Task.Run(() => EndCallAsync(clicked));
        overlay.Close();
        Step("rozmowa zakończona" + (problem == null ? "" : ": " + problem));
        return problem;
    }

    // ---------- praca w tle ----------

    enum Outcome { Ended, Retry, Fatal }

    async Task<string?> WorkAsync(Action<string> status, Action markClicked, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_contact.Phone) && string.IsNullOrWhiteSpace(_contact.WaName))
            return "Ta osoba nie ma numeru telefonu. Dodaj go w ustawieniach.";

        string reason = "";
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                Step($"ponawiam – próba {attempt}: {reason}");
                status($"Próbuję jeszcze raz…\n{_contact.Name}");
                await Task.Delay(1500, ct);
            }
            var (outcome, message) = await TryOnceAsync(attempt, status, markClicked, ct);
            if (outcome == Outcome.Ended) return null;
            if (outcome == Outcome.Fatal) return message;
            reason = message;
        }
        return "Nie udało się zadzwonić. Naciśnij zielony przycisk jeszcze raz.";
    }

    async Task<(Outcome, string)> TryOnceAsync(int attempt, Action<string> status, Action markClicked, CancellationToken ct)
    {
        var hasPhone = !string.IsNullOrWhiteSpace(_contact.Phone);
        var expected = _contact.WaName.Trim();

        var before = WhatsAppController.MainWindow();
        var coldStart = before == null;                       // WhatsApp nie działa albo siedzi w zasobniku bez okna
        var previousTitle = before == null ? null : WhatsAppController.ChatTitle(before);
        Step($"próba {attempt}: WhatsApp {(coldStart ? "trzeba uruchomić" : "już działa")}");

        status($"Otwieram WhatsApp…\n{_contact.Name}");
        WhatsAppController.Launch(hasPhone ? _contact.Phone : null);

        // 1. Okno WhatsApp.
        Window? main = null;
        for (var i = 0; i < 90 && main == null; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
            main = WhatsAppController.MainWindow();
        }
        if (main == null) return (Outcome.Retry, "brak okna WhatsApp");
        Step("okno WhatsApp znalezione");
        WhatsAppController.Foreground(main);

        // 2. Świeżo uruchomiony WhatsApp musi się najpierw zalogować i wczytać czaty – bez tego przyciski jeszcze nie działają.
        if (coldStart)
        {
            var ready = false;
            for (var i = 0; i < 90 && !ready; i++)
            {
                ct.ThrowIfCancellationRequested();
                ready = WhatsAppController.IsReady(main);
                if (!ready) await Task.Delay(500, ct);
            }
            Step("WhatsApp gotowy: " + ready);
            if (!ready) return (Outcome.Retry, "WhatsApp się jeszcze wczytuje");
            await Task.Delay(2500, ct);
            WhatsAppController.Foreground(main);
            // Link otwarty w trakcie uruchamiania WhatsApp często ginie – po wczytaniu otwieramy go jeszcze raz.
            if (hasPhone) { Step("ponawiam link po uruchomieniu WhatsApp"); WhatsAppController.Launch(_contact.Phone); }
        }

        // 3. Właściwa rozmowa (po numerze – odpowiada za to link; po nazwie – klikamy na liście czatów).
        if (!hasPhone && !WhatsAppController.OpenChatByName(main, expected, ct))
            return (Outcome.Retry, $"nie znaleziono rozmowy „{expected}”");

        var title = await WaitForChatAsync(main, hasPhone ? _contact.Phone : null, expected, previousTitle, coldStart,
            () => { if (hasPhone) WhatsAppController.Launch(_contact.Phone); else WhatsAppController.OpenChatByName(main, expected, ct); }, ct);
        if (title == null) return (Outcome.Retry, "nie udało się otworzyć rozmowy");
        Step("rozmowa otwarta");

        if (expected.Length == 0 && hasPhone)
        {
            // Zapamiętujemy nazwę czatu; kolejne połączenia będą dodatkowo weryfikowane.
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => _contact.WaName = title);
            Log.Write("Zapamiętano nazwę czatu dla kontaktu.");
        }

        // 4. Przycisk połączenia – czekamy, aż nagłówek rozmowy będzie stabilny, i klikamy.
        status($"Dzwonię do:\n{_contact.Name}");
        await Task.Delay(coldStart ? 2000 : 700, ct);
        var clickedOk = false;
        for (var i = 0; i < 30 && !clickedOk; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (WhatsAppController.HasCallButton(main, _video)) clickedOk = WhatsAppController.StartCall(main, _video);
            if (!clickedOk) await Task.Delay(500, ct);
        }
        if (!clickedOk) return (Outcome.Retry, "brak przycisku połączenia");
        markClicked();

        // 5. Okno rozmowy. Dajemy WhatsApp sporo czasu: ponowne kliknięcie podczas dzwonienia mogłoby wywołać drugie połączenie.
        Window? call = null;
        for (var i = 0; i < 45 && call == null; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(300, ct);
            call = WhatsAppController.CallWindow();
        }
        if (call == null) return (Outcome.Retry, "WhatsApp nie otworzył okna rozmowy");

        Step("okno rozmowy: " + call.Name);
        WhatsAppController.MaximizeCallWindow(call);
        status($"Rozmowa z:\n{_contact.Name}");

        // 6. Trwa rozmowa – kończy się, gdy okno rozmowy zniknie albo naciśnięto czerwony przycisk.
        // Błąd odczytu nie oznacza końca rozmowy – wymagamy kilku kolejnych, udanych odczytów bez okna.
        var missing = 0;
        while (true)
        {
            await Task.Delay(1000, ct);
            var current = WhatsAppController.CallWindow();
            if (current != null) WhatsAppController.MaximizeCallWindow(current);   // WhatsApp potrafi przywrócić rozmiar po zestawieniu połączenia
            if (current != null || WhatsAppController.LastEnumFailed) { missing = 0; continue; }
            if (++missing >= 3) { Step("okno rozmowy zamknięte"); return (Outcome.Ended, ""); }
        }
    }

    /// <summary>Czeka, aż w WhatsApp będzie otwarta rozmowa z właściwą osobą. Nigdy nie dzwonimy "w ciemno".</summary>
    async Task<string?> WaitForChatAsync(Window main, string? phone, string expected, string? previousTitle, bool coldStart,
        Action reopen, CancellationToken ct)
    {
        var relaunched = false;
        var started = DateTime.UtcNow;
        var reopened = 0;
        // Gdy nie znamy jeszcze nazwy czatu, rozpoznajemy przełączenie po zmianie tytułu względem tego, co było otwarte.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(coldStart ? 45 : 20);
        var unchangedSince = DateTime.UtcNow;
        string? last = null;
        var stable = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
            var title = WhatsAppController.ChatTitle(main);
            stable = title != null && title == last ? stable + 1 : 0;
            last = title;
            if (title == null)
            {
                // Żadna rozmowa nie jest otwarta – WhatsApp zignorował polecenie. Powtarzamy je co kilka sekund.
                if (reopened < 3 && DateTime.UtcNow - started > TimeSpan.FromSeconds(6 * (reopened + 1)))
                {
                    reopened++;
                    Step($"brak otwartej rozmowy – otwieram ponownie ({reopened})");
                    reopen();
                }
                continue;
            }

            if (expected.Length > 0)
            {
                if (string.Equals(title, expected, StringComparison.OrdinalIgnoreCase) && stable >= 1) return title;
                continue;
            }

            // Nazwy czatu jeszcze nie znamy.
            if (previousTitle == null || !string.Equals(title, previousTitle, StringComparison.OrdinalIgnoreCase))
            {
                if (stable >= 1) return title;      // tytuł się zmienił i ustabilizował – WhatsApp przełączył rozmowę
                continue;
            }

            // Tytuł taki sam jak przed linkiem: albo to ta sama osoba, albo link nie zadziałał. Ponawiamy link raz,
            // a jeśli nadal bez zmian, przyjmujemy, że otwarta rozmowa jest właściwa (zwykle dzwoni się do tej samej osoby kolejny raz).
            if (phone != null && DateTime.UtcNow - unchangedSince > TimeSpan.FromSeconds(4))
            {
                if (!relaunched)
                {
                    relaunched = true;
                    unchangedSince = DateTime.UtcNow;
                    Step("tytuł bez zmian – ponawiam link");
                    WhatsAppController.Launch(phone);
                }
                else
                {
                    Step("tytuł bez zmian po ponowieniu – przyjmuję, że to właściwa rozmowa");
                    return title;
                }
            }
        }
        Step("nie doczekano się rozmowy");
        return null;
    }

    // ---------- koniec rozmowy ----------

    static async Task EndCallAsync(bool callWasStarted)
    {
        try
        {
            // Jeśli czerwony przycisk naciśnięto tuż po kliknięciu "dzwoń", okno rozmowy może jeszcze się pojawiać.
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(callWasStarted ? 8 : 1);
            Window? call = null;
            while (call == null && DateTime.UtcNow < until)
            {
                call = await FindCallWindowAsync();
                if (call == null) await Task.Delay(300);
            }
            if (call == null) return;

            WhatsAppController.EndCall(call);
            // Odczyt okien tuż po zamknięciu rozmowy bywa powolny – nie czekamy dłużej niż kilka sekund.
            until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < until && await FindCallWindowAsync() != null) await Task.Delay(250);
        }
        catch (Exception ex) { Log.Write("EndCallAsync: " + ex.Message); }
    }

    /// <summary>Szuka okna rozmowy, ale nie czeka na zawieszoną automatyzację dłużej niż 1,5 s.</summary>
    static async Task<Window?> FindCallWindowAsync()
    {
        var t = Task.Run(() => WhatsAppController.CallWindow());
        return await Task.WhenAny(t, Task.Delay(1500)) == t ? await t : null;
    }
}
