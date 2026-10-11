using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SeniorWhatsApp;

public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string DownloadUrl, string? Sha256, string PageUrl);

/// <summary>
/// Aktualizacje z wydań (GitHub Releases) repozytorium <see cref="AppInfo.Repository"/>.
/// Przepis na wydanie: tag "vX.Y.Z" + plik "SeniorWhatsApp-X.Y.Z-win-x64.zip" (z SeniorWhatsApp.exe w środku)
/// albo sam plik "SeniorWhatsApp.exe". Dopóki w repozytorium nie ma żadnego wydania, sprawdzanie kończy się po cichu.
/// </summary>
public static class UpdateService
{
    static readonly HttpClient Http = CreateClient();
    static readonly Regex ZipAsset = new(@"^SeniorWhatsApp-.*win-x64\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string ExePath => AppInfo.ExePath;
    static string NewPath => ExePath + ".new";
    static string OldPath => ExePath + ".old";

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SeniorWhatsApp/" + AppInfo.VersionText);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Pyta GitHub o najnowsze wydanie. Zwraca null, gdy brak nowszej wersji (lub brak wydań / brak sieci).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{AppInfo.Repository}/releases/latest";
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) { Log.Write("Aktualizacje: brak opublikowanych wydań."); return null; }
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) { Log.Write("Aktualizacje: nieznany format tagu " + tag); return null; }
        if (version <= Normalize(AppInfo.Version)) return null;

        JsonElement? chosen = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (ZipAsset.IsMatch(name)) { chosen = a; break; }
            if (name.Equals("SeniorWhatsApp.exe", StringComparison.OrdinalIgnoreCase)) chosen ??= a;
        }
        if (chosen is not { } asset) { Log.Write("Aktualizacje: wydanie " + tag + " nie ma pasującego pliku."); return null; }

        var download = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!IsTrustedUrl(download)) { Log.Write("Aktualizacje: odrzucono adres " + download); return null; }

        string? sha = null;
        if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            sha = digest[7..];

        return new UpdateInfo(version, tag, asset.GetProperty("name").GetString() ?? "", download, sha,
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static bool IsTrustedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var host = u.Host.ToLowerInvariant();
        return host == "github.com" || host.EndsWith(".github.com") || host.EndsWith(".githubusercontent.com");
    }

    /// <summary>Pobiera aktualizację do pliku SeniorWhatsApp.exe.new obok programu. Nie podmienia działającego pliku.</summary>
    public static async Task DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(ExePath)!, "update-download.tmp");
        try
        {
            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var finalHost = resp.RequestMessage?.RequestUri;
                if (finalHost == null || !IsTrustedUrl(finalHost.ToString())) throw new InvalidOperationException("Nieoczekiwany adres pobierania.");
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (info.Sha256 != null)
            {
                await using var fs = File.OpenRead(tmp);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Suma kontrolna pobranego pliku się nie zgadza.");
            }

            if (File.Exists(NewPath)) File.Delete(NewPath);
            if (info.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(tmp);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("SeniorWhatsApp.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("W archiwum nie ma SeniorWhatsApp.exe.");
                entry.ExtractToFile(NewPath, true);
            }
            else File.Move(tmp, NewPath, true);

            if (!LooksLikeProgram(NewPath)) { File.Delete(NewPath); throw new InvalidOperationException("Pobrany plik nie jest programem."); }
            Log.Write("Pobrano aktualizację " + info.Tag);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static bool LooksLikeProgram(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length < 1_000_000) return false;
        using var fs = fi.OpenRead();
        return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
    }

    public static bool IsUpdateReady => File.Exists(NewPath);

    /// <summary>Podmienia program na pobraną wersję i uruchamia ją. Zwraca true, gdy bieżący proces ma się zakończyć.</summary>
    public static bool ApplyPendingAndRestart()
    {
        try
        {
            if (!File.Exists(NewPath) || !LooksLikeProgram(NewPath)) return false;
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);              // działający plik można przemianować
            File.Move(NewPath, ExePath);
            Process.Start(new ProcessStartInfo(ExePath, $"--after-update {Environment.ProcessId}") { UseShellExecute = true });
            Log.Write("Zainstalowano aktualizację, uruchamiam ponownie.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Nie udało się zainstalować aktualizacji: " + ex.Message);
            try { if (!File.Exists(ExePath) && File.Exists(OldPath)) File.Move(OldPath, ExePath); } catch { }
            return false;
        }
    }

    public static void CleanupOld()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
    }
}

/// <summary>Stan aktualizacji dla okna ustawień i działania w tle.</summary>
public sealed class UpdateManager : Observable
{
    public static UpdateManager Instance { get; } = new();

    string _status = "";
    bool _busy;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public bool ReadyToInstall => UpdateService.IsUpdateReady;

    public async Task CheckAndDownloadAsync(bool manual)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            if (manual) Status = "Sprawdzam…";
            var info = await UpdateService.CheckAsync();
            if (info == null)
            {
                Status = UpdateService.IsUpdateReady ? "Nowa wersja jest pobrana i czeka na instalację." : "Masz najnowszą wersję.";
            }
            else
            {
                Status = $"Pobieram wersję {info.Version.Major}.{info.Version.Minor}.{info.Version.Build}…";
                await UpdateService.DownloadAsync(info);
                Status = $"Wersja {info.Version.Major}.{info.Version.Minor}.{info.Version.Build} jest pobrana i czeka na instalację.";
            }
        }
        catch (Exception ex)
        {
            Log.Write("Aktualizacja: " + ex.Message);
            Status = manual ? "Nie udało się sprawdzić aktualizacji (brak internetu?)." : Status;
        }
        finally
        {
            Busy = false;
            Raise(nameof(ReadyToInstall));
        }
    }
}
