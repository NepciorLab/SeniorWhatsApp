using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SeniorWhatsApp;

public partial class App : Application
{
    Mutex? _single;
    DispatcherTimer? _updateTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // Po aktualizacji nowa kopia czeka, aż stara się zamknie (inaczej zadziałałaby blokada "jedna kopia").
        var after = args.SkipWhile(a => a != "--after-update").Skip(1).FirstOrDefault();
        if (int.TryParse(after, out var oldPid))
        {
            try { Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }
        }

        _single = new Mutex(true, "SeniorWhatsApp.SingleInstance", out var first);
        if (!first) { Shutdown(); return; }          // jedna kopia programu wystarczy

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("Nieobsłużony wyjątek: " + ex.Exception);
            ex.Handled = true;
        };

        var demo = args.Contains("--demo");
        // Test układu: --size 1536x864 (rozmiar w jednostkach niezależnych od urządzenia, tj. po uwzględnieniu skalowania Windows).
        var sizeArg = args.SkipWhile(a => a != "--size").Skip(1).FirstOrDefault()?.Split('x');
        if (sizeArg is { Length: 2 } && double.TryParse(sizeArg[0], out var fw) && double.TryParse(sizeArg[1], out var fh))
        {
            MainWindow_ForceSize(fw, fh);
        }
        UpdateService.CleanupOld();
        // Pobrana wcześniej aktualizacja jest instalowana od razu przy starcie.
        if (!demo && UpdateService.IsUpdateReady && UpdateService.ApplyPendingAndRestart()) { Shutdown(); return; }

        // Diagnostyka (tylko odczyt, nic nie klika): wynik trafia do log.txt.
        if (args.Contains("--diagnoza"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Task.Run(async () =>
            {
                Log.Write($"DIAGNOZA: wersja {AppInfo.VersionText}, dane: {SettingsStore.DataDir}");
                Log.Write("DIAGNOZA: WhatsApp zainstalowany: " + WhatsAppController.IsInstalled());
                Log.Write("DIAGNOZA: kamera: " + await CameraService.HasCameraAsync());
                var w = WhatsAppController.MainWindow();
                Log.Write("DIAGNOZA: okno WhatsApp: " + (w != null));
                if (w != null)
                {
                    Log.Write("DIAGNOZA: tytuł otwartej rozmowy: " + (WhatsAppController.ChatTitle(w) ?? "(brak)"));
                    Log.Write("DIAGNOZA: przycisk wideo: " + WhatsAppController.HasCallButton(w, true) +
                              ", głosowy: " + WhatsAppController.HasCallButton(w, false));
                }
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }

        var settings = SettingsStore.Load();
        if (demo) FillDemo(settings);

        var window = new MainWindow(settings);
        window.Closed += (_, _) => { if (!demo) SettingsStore.Save(settings); };
        MainWindow = window;
        window.Show();

        if (!demo) StartUpdateChecks(settings);

        // Narzędzia deweloperskie: zrzut ekranu okna i zakończenie.
        var shot = args.SkipWhile(a => a != "--screenshot").Skip(1).FirstOrDefault();
        if (shot != null) ScheduleScreenshot(args, settings, window, shot);
    }

    /// <summary>Sprawdza aktualizacje kilka sekund po starcie, a potem co 6 godzin (gdy włączono automatyczne aktualizacje).</summary>
    void StartUpdateChecks(AppSettings settings)
    {
        async void Check()
        {
            if (settings.AutoUpdate) await UpdateManager.Instance.CheckAndDownloadAsync(manual: false);
        }
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        first.Tick += (_, _) => { first.Stop(); Check(); };
        first.Start();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += (_, _) => Check();
        _updateTimer.Start();
    }

    static void MainWindow_ForceSize(double w, double h)
    {
        SeniorWhatsApp.MainWindow.ForcedSize = new Size(w, h);
        Display.Override = new Rect(0, 0, w, h - 48);   // jak ekran z paskiem zadań
    }

    void ScheduleScreenshot(string[] args, AppSettings settings, Window window, string shot)
    {
        var select = args.Contains("--select");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var list = (System.Windows.Controls.ListBox)window.FindName("ContactList");
            if (select && settings.Contacts.Count > 0) list.SelectedIndex = 1;
            Window target = window;
            if (args.Contains("--settings")) { target = new SettingsWindow(settings) { Owner = window }; target.Show(); }
            if (args.Contains("--overlay")) { var o = new CallOverlayWindow(); o.SetStatus("Rozmowa z:\nCórka Kasia"); o.Show(); target = o; }
            if (args.Contains("--nocamera"))
            {
                ((FrameworkElement)window.FindName("DetailPanel")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("NoCameraPanel")).Visibility = Visibility.Visible;
            }
            if (args.Contains("--holdhint")) ((FrameworkElement)window.FindName("HoldHint")).Visibility = Visibility.Visible;
            var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t2.Tick += (_, _) => { t2.Stop(); Capture(target, shot); Shutdown(); };
            t2.Start();
        };
        timer.Start();
    }

    static void Capture(Window w, string path)
    {
        var rtb = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static void FillDemo(AppSettings s)
    {
        s.Contacts.Clear();
        foreach (var n in new[] { "Babcia Ania", "Córka Kasia", "Syn Tomek z rodziną", "Wnuczek Jaś", "Doktor Nowak", "Sąsiadka Basia", "Brat Marek", "Siostra Ela" })
            s.Contacts.Add(new Contact { Name = n, Phone = "600100200" });
        // Przykładowe zdjęcie (jeśli jest w folderze images): druga osoba ma kadr ustawiony na "twarz", pierwsza – domyślny.
        if (File.Exists(Path.Combine(SettingsStore.ImagesDir, "demo.png")))
        {
            foreach (var c in s.Contacts.Take(2)) c.ImageFile = "demo.png";
            s.Contacts[1].ImageZoom = 2.6; s.Contacts[1].ImageCenterX = 0.72; s.Contacts[1].ImageCenterY = 0.38;
            // trzecia osoba: zdjęcie pomniejszone i obrócone w prawo
            s.Contacts[2].ImageFile = "demo.png"; s.Contacts[2].ImageZoom = 0.55; s.Contacts[2].ImageRotation = 90;
        }
    }
}
