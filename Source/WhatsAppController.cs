using System.Diagnostics;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Windows.Devices.Enumeration;

namespace SeniorWhatsApp;

/// <summary>
/// Steruje programem WhatsApp Desktop (ze sklepu Microsoft) przez UI Automation – tak jak robią to czytniki ekranu.
/// WhatsApp udostępnia drzewo elementów dopiero wtedy, gdy wykryje klienta nasłuchującego zdarzeń, dlatego
/// przy pierwszym użyciu rejestrujemy "pusty" nasłuch (patrz <see cref="Auto"/>).
/// </summary>
internal static class WhatsAppController
{
    // Nazwy przycisków w nagłówku rozmowy (WhatsApp ma interfejs w języku systemu).
    static readonly string[] VideoNames = { "Rozmowa wideo", "Wideorozmowa", "Video call" };
    static readonly string[] VoiceNames = { "Rozmowa głosowa", "Połączenie głosowe", "Voice call" };
    static readonly string[] ChatsTabNames = { "Czaty", "Chats" };
    const string ChatListName = "Lista czatów";
    const string ChatListNameEn = "Chat list";
    static readonly Regex EndCallName = new(@"^(End call|Zako[ńn]cz( rozmow[ęe]| po[łl][ąa]czenie)?|Roz[łl][ąa]cz)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CallWindowName = new(@"call|rozmow|po[łl][ąa]cz", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Do nazwy w nagłówku WhatsApp dokleja status ("Jan widziano dzisiaj o 18:43", "Jan online", "Jan pisze…").
    static readonly Regex PresenceSuffix = new(@"\s+(widziano|online|pisze|nagrywa|kliknij|last seen|typing|recording|click)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex TimeSuffix = new(
        @"\s+(\d{1,2}:\d{2}|\d{1,2}[./]\d{1,2}[./]\d{2,4}|Wczoraj|Dzisiaj|Yesterday|Today|poniedzia[łl]ek|wtorek|[śs]roda|czwartek|pi[ąa]tek|sobota|niedziela|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Lazy<UIA3Automation> Host = new(() =>
    {
        var a = new UIA3Automation();
        a.RegisterFocusChangedEvent(_ => { });   // sam fakt nasłuchu włącza drzewo dostępności w WhatsApp
        return a;
    });

    static UIA3Automation Auto => Host.Value;

    // ---------- numer, instalacja, uruchomienie ----------

    public static string NormalizePhone(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.Length == 9) digits = "48" + digits;   // polski numer bez kierunkowego
        return digits;
    }

    public static bool IsInstalled()
    {
        try { using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("whatsapp"); return k != null; }
        catch { return false; }
    }

    /// <summary>Uruchamia WhatsApp (jeśli trzeba) i – gdy podano numer – otwiera rozmowę z tą osobą.</summary>
    public static void Launch(string? phone)
    {
        var uri = string.IsNullOrWhiteSpace(phone) ? "whatsapp://" : "whatsapp://send?phone=" + NormalizePhone(phone);
        Log.Write("Otwieram: " + (string.IsNullOrWhiteSpace(phone) ? uri : "whatsapp://send?phone=…"));
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    // ---------- okna ----------

    static HashSet<int> Pids() =>
        Process.GetProcesses()
            .Where(p => p.ProcessName.StartsWith("WhatsApp", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id).ToHashSet();

    static string SafeName(AutomationElement e)
    {
        try { return e.Name ?? ""; } catch { return ""; }
    }

    /// <summary>True, jeśli ostatnie wyliczenie okien się nie udało (np. okno właśnie znikało) – wynik był wtedy niepełny.</summary>
    public static volatile bool LastEnumFailed;

    public static List<Window> Windows()
    {
        var result = new List<Window>();
        LastEnumFailed = false;
        foreach (var pid in Pids())
        {
            try
            {
                foreach (var w in Auto.GetDesktop().FindAllChildren(cf => cf.ByProcessId(pid)))
                    if (w.ControlType == ControlType.Window) result.Add(w.AsWindow());
            }
            catch (Exception ex) { LastEnumFailed = true; Log.Write("Windows(): " + ex.Message); }
        }
        return result;
    }

    public static Window? MainWindow(List<Window>? all = null)
    {
        all ??= Windows();
        return all.FirstOrDefault(w => SafeName(w) == "WhatsApp")
               ?? all.Where(w => !CallWindowName.IsMatch(SafeName(w))).OrderByDescending(Area).FirstOrDefault();
    }

    public static Window? CallWindow(List<Window>? all = null)
    {
        all ??= Windows();
        return all.FirstOrDefault(w => { var n = SafeName(w); return n != "WhatsApp" && CallWindowName.IsMatch(n); });
    }

    static double Area(AutomationElement e)
    {
        try { var r = e.BoundingRectangle; return r.Width * r.Height; } catch { return 0; }
    }

    public static void Foreground(Window w)
    {
        try
        {
            var h = w.Properties.NativeWindowHandle.ValueOrDefault;
            Native.Restore(h);
            Native.SetForegroundWindow(h);
        }
        catch { }
    }

    /// <summary>Okno rozmowy (połączenia) ma być zmaksymalizowane – osoba starsza nie będzie go rozciągać ręcznie.</summary>
    public static void MaximizeCallWindow(Window call)
    {
        try
        {
            var h = call.Properties.NativeWindowHandle.ValueOrDefault;
            if (h != IntPtr.Zero && !Native.IsMaximizedOrMinimized(h)) Native.Maximize(h);
        }
        catch (Exception ex) { Log.Write("MaximizeCallWindow: " + ex.Message); }
    }

    // ---------- nagłówek rozmowy ----------

    static Button? HeaderButton(Window main, string[] names)
    {
        try
        {
            var top = main.BoundingRectangle.Top;
            foreach (var name in names)
            {
                foreach (var e in main.FindAllDescendants(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name))))
                {
                    var y = e.BoundingRectangle.Top - top;
                    if (y >= 0 && y < 160) return e.AsButton();   // przycisk z nagłówka, a nie wpis w historii rozmowy
                }
            }
        }
        catch (Exception ex) { Log.Write("HeaderButton: " + ex.Message); }
        return null;
    }

    /// <summary>WhatsApp jest zalogowany i wczytał listę czatów (albo ma otwartą rozmowę) – można go już obsługiwać.</summary>
    public static bool IsReady(Window main)
    {
        try { return ChatList(main) != null || ChatTitle(main) != null; }
        catch { return false; }
    }

    public static bool HasCallButton(Window main, bool video) => HeaderButton(main, video ? VideoNames : VoiceNames) != null;

    /// <summary>Nazwa osoby/grupy widoczna w nagłówku otwartej rozmowy (null, gdy żadna rozmowa nie jest otwarta).</summary>
    public static string? ChatTitle(Window main)
    {
        try
        {
            var profile = main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Dane profilu")))
                          ?? main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Profile details")));
            if (profile == null) return null;
            var pr = profile.BoundingRectangle;
            // Tytuł jest przyciskiem obok "Dane profilu" – szukamy go tylko wśród sąsiadów (całe drzewo jest ogromne).
            for (var parent = profile.Parent; parent != null; parent = parent.Parent)
            {
                foreach (var b in parent.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                {
                    var r = b.BoundingRectangle;
                    if (Math.Abs(r.Top - pr.Top) < 24 && r.Left >= pr.Right - 4 && r.Left < pr.Right + 40 && r.Width > 60)
                    {
                        var n = PresenceSuffix.Replace(SafeName(b).Trim(), "").Trim();
                        if (n.Length > 0) return n;
                    }
                }
                if (parent.BoundingRectangle.Width > pr.Width * 6) break;   // wystarczająco szeroki kontener (nagłówek)
            }
        }
        catch (Exception ex) { Log.Write("ChatTitle: " + ex.Message); }
        return null;
    }

    public static bool Press(AutomationElement e)
    {
        try
        {
            if (e.Patterns.Invoke.IsSupported) { e.Patterns.Invoke.Pattern.Invoke(); return true; }
            e.Click();
            return true;
        }
        catch (Exception ex) { Log.Write("Press: " + ex.Message); return false; }
    }

    public static bool StartCall(Window main, bool video)
    {
        var b = HeaderButton(main, video ? VideoNames : VoiceNames);
        if (b == null) return false;
        Log.Write("Klikam: " + SafeName(b));
        return Press(b);
    }

    // ---------- koniec rozmowy ----------

    /// <summary>Kończy rozmowę: przycisk "End call"; jeśli go nie ma, zamyka okno rozmowy.</summary>
    public static bool EndCall(Window call)
    {
        try
        {
            foreach (var b in call.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
            {
                if (EndCallName.IsMatch(SafeName(b)) && Press(b)) { Log.Write("Zakończono przyciskiem: " + SafeName(b)); return true; }
            }
            var h = call.Properties.NativeWindowHandle.ValueOrDefault;
            if (h != IntPtr.Zero)
            {
                Log.Write("Brak przycisku zakończenia – zamykam okno rozmowy");
                Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
        }
        catch (Exception ex) { Log.Write("EndCall: " + ex.Message); }
        return false;
    }

    // ---------- lista czatów (import i otwieranie rozmowy po nazwie) ----------

    static AutomationElement? ChatList(Window main) =>
        main.FindFirstDescendant(cf => cf.ByName(ChatListName)) ?? main.FindFirstDescendant(cf => cf.ByName(ChatListNameEn));

    public static void ShowChatsTab(Window main)
    {
        foreach (var n in ChatsTabNames)
        {
            var tab = main.FindAllDescendants(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(n)))
                .FirstOrDefault(e => e.BoundingRectangle.Left - main.BoundingRectangle.Left < 120);
            if (tab != null) { Press(tab); Thread.Sleep(400); return; }
        }
    }

    static string TitleFromRow(AutomationElement row)
    {
        // Wiersz zawiera element "Nazwa godzina" (niski, ok. 25 px) oraz podgląd ostatniej wiadomości.
        foreach (var c in row.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)))
        {
            var r = c.BoundingRectangle;
            var n = SafeName(c);
            if (r.Height > 5 && r.Height < 40 && n.Length > 0) return TimeSuffix.Replace(n, "").Trim();
        }
        return "";
    }

    static AutomationElement? ScrollContainer(AutomationElement list)
    {
        var p = list.Parent;
        for (var i = 0; p != null && i < 8; i++, p = p.Parent)
            if (p.Patterns.Scroll.IsSupported) return p;
        return null;
    }

    /// <summary>Odwiedza kolejne "ekrany" listy czatów (lista jest wirtualizowana) i woła <paramref name="visit"/> dla każdego wiersza.</summary>
    static void WalkChatList(Window main, Func<AutomationElement, string, bool> visit, CancellationToken ct)
    {
        var list = ChatList(main);
        if (list == null) return;
        var scroller = ScrollContainer(list);
        var scroll = scroller?.Patterns.Scroll.PatternOrDefault;
        try { scroll?.SetScrollPercent(-1 /* bez przewijania w poziomie */, 0); } catch { }
        Thread.Sleep(300);

        var lastKey = "";
        for (var page = 0; page < 80; page++)
        {
            ct.ThrowIfCancellationRequested();
            var keys = new List<string>();
            foreach (var row in list.FindAllChildren())
            {
                var title = TitleFromRow(row);
                if (title.Length == 0) continue;
                keys.Add(title);
                if (visit(row, title)) return;
            }
            var key = string.Join("|", keys);
            if (scroll == null || key == lastKey) return;
            lastKey = key;
            try { scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); } catch { return; }
            Thread.Sleep(350);
            if (scroll.VerticalScrollPercent.ValueOrDefault >= 99.5) // ostatni ekran też trzeba odczytać
            {
                foreach (var row in list.FindAllChildren())
                {
                    var title = TitleFromRow(row);
                    if (title.Length > 0 && visit(row, title)) return;
                }
                return;
            }
        }
    }

    public static List<string> ReadChatNames(CancellationToken ct)
    {
        var names = new List<string>();
        var main = MainWindow();
        if (main == null) return names;
        ShowChatsTab(main);
        WalkChatList(main, (_, title) =>
        {
            if (!names.Contains(title, StringComparer.OrdinalIgnoreCase)) names.Add(title);
            return false;
        }, ct);
        return names;
    }

    /// <summary>Wybiera na liście czatów rozmowę o danej nazwie.</summary>
    public static bool OpenChatByName(Window main, string name, CancellationToken ct)
    {
        ShowChatsTab(main);
        var found = false;
        WalkChatList(main, (row, title) =>
        {
            if (!string.Equals(title, name.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            var target = row.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                             .FirstOrDefault(e => e.Patterns.SelectionItem.IsSupported) ?? row;
            try
            {
                // WhatsApp ignoruje programowe "zaznaczenie" wiersza – potrzebne jest prawdziwe kliknięcie.
                try { if (target.Patterns.ScrollItem.IsSupported) target.Patterns.ScrollItem.Pattern.ScrollIntoView(); } catch { }
                Thread.Sleep(250);
                target.Click();
                found = true;
            }
            catch (Exception ex) { Log.Write("OpenChatByName: " + ex.Message); }
            return found;
        }, ct);
        return found;
    }
}

/// <summary>Sprawdza, czy do komputera jest podłączona kamera.</summary>
internal static class CameraService
{
    public static async Task<bool> HasCameraAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask();
            Log.Write("Wykryte kamery: " + devices.Count);
            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Write("Wykrywanie kamery nie powiodło się (zakładam, że jest): " + ex.Message);
            return true;
        }
    }
}
