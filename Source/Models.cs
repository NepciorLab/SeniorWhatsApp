using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SeniorWhatsApp;

public static class AppInfo
{
    /// <summary>Repozytorium GitHub, z którego pobierane są aktualizacje (wydania "Releases").</summary>
    public const string DefaultRepository = "NepciorLab/SeniorWhatsApp";

    /// <summary>Zmienna środowiskowa SENIOR_WA_REPO pozwala sprawdzić działanie aktualizacji na innym repozytorium (testy).</summary>
    public static string Repository { get; } = Environment.GetEnvironmentVariable("SENIOR_WA_REPO") is { Length: > 0 } r ? r : DefaultRepository;

    public static Version Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";

    public static string ExePath { get; } = Environment.ProcessPath ?? "";
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Contact : Observable
{
    public const double MinZoom = 0.25, MaxZoom = 6;

    string _name = "", _phone = "", _waName = "", _imageFile = "";
    double _zoom = 1, _cx = 0.5, _cy = 0.5;
    int _rotation;
    ImageSource? _image, _base;
    bool _imageLoaded;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Nazwa wyświetlana (własna, niezależna od WhatsApp).</summary>
    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Raise(nameof(Initial)); }
    }

    /// <summary>Numer telefonu kontaktu w WhatsApp (może być pusty, jeśli podano nazwę czatu).</summary>
    public string Phone { get => _phone; set => Set(ref _phone, value); }

    /// <summary>Nazwa czatu widoczna w WhatsApp. Służy do znalezienia rozmowy bez numeru i do sprawdzenia, z kim dzwonimy.</summary>
    public string WaName { get => _waName; set => Set(ref _waName, value ?? ""); }

    /// <summary>Nazwa pliku ze zdjęciem w folderze images.</summary>
    public string ImageFile
    {
        get => _imageFile;
        set
        {
            if (!Set(ref _imageFile, value)) return;
            _base = null;
            _imageLoaded = false;
            Raise(nameof(Image));
            Raise(nameof(Crop));
        }
    }

    /// <summary>Powiększenie zdjęcia: 1 = największy kwadrat z całego zdjęcia, więcej = przybliżenie, mniej = zdjęcie pomniejszone w ramce.</summary>
    public double ImageZoom
    {
        get => _zoom;
        set { if (Set(ref _zoom, Math.Clamp(value, MinZoom, MaxZoom))) { Raise(nameof(Crop)); Raise(nameof(ZoomSlider)); } }
    }

    /// <summary>Wartość dla suwaka: logarytm z powiększenia, dzięki czemu środek suwaka to rozmiar normalny,
    /// w lewo zdjęcie się zmniejsza, w prawo powiększa.</summary>
    [JsonIgnore]
    public double ZoomSlider
    {
        get => Math.Log2(_zoom);
        set => ImageZoom = Math.Pow(2, value);
    }

    /// <summary>Środek kadru (0..1 w poziomie i w pionie względem całego zdjęcia).</summary>
    public double ImageCenterX { get => _cx; set { if (Set(ref _cx, Math.Clamp(value, -1, 2))) Raise(nameof(Crop)); } }
    public double ImageCenterY { get => _cy; set { if (Set(ref _cy, Math.Clamp(value, -1, 2))) Raise(nameof(Crop)); } }

    /// <summary>Obrót zdjęcia o wielokrotność 90° (0, 90, 180, 270), zgodnie z ruchem wskazówek zegara.</summary>
    public int ImageRotation
    {
        get => _rotation;
        set
        {
            var v = ((value % 360) + 360) % 360 / 90 * 90;
            if (!Set(ref _rotation, v)) return;
            _imageLoaded = false;
            Raise(nameof(Image));
            Raise(nameof(Crop));
        }
    }

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(_name) ? "?" : _name.Trim()[..1].ToUpperInvariant();

    [JsonIgnore]
    public ImageSource? Image
    {
        get
        {
            if (!_imageLoaded)
            {
                _base ??= SettingsStore.LoadImage(_imageFile);
                _image = _base;
                if (_base is BitmapSource bmp && _rotation != 0)
                {
                    var rotated = new TransformedBitmap(bmp, new RotateTransform(_rotation));
                    rotated.Freeze();
                    _image = rotated;
                }
                _imageLoaded = true;
            }
            return _image;
        }
    }

    /// <summary>Kwadratowy wycinek zdjęcia (w ułamkach całego obrazu), do użycia jako ImageBrush.Viewbox.
    /// Dla powiększenia poniżej 1 wycinek jest większy niż zdjęcie – poza zdjęciem widać tło ramki.</summary>
    [JsonIgnore]
    public Rect Crop
    {
        get
        {
            if (Image is not BitmapSource img || img.PixelWidth == 0 || img.PixelHeight == 0) return new Rect(0, 0, 1, 1);
            double w = img.PixelWidth, h = img.PixelHeight;
            var side = Math.Min(w, h) / Math.Clamp(_zoom, MinZoom, MaxZoom);
            double sx = side / w, sy = side / h;
            var cx = ClampCenter(_cx, sx);
            var cy = ClampCenter(_cy, sy);
            return new Rect(cx - sx / 2, cy - sy / 2, sx, sy);
        }
    }

    // Wycinek mniejszy od zdjęcia musi w nim zostać; wycinek większy od zdjęcia może się przesuwać tak, by zdjęcie nie wyszło poza ramkę.
    static double ClampCenter(double c, double size)
    {
        var lo = Math.Min(size / 2, 1 - size / 2);
        var hi = Math.Max(size / 2, 1 - size / 2);
        return Math.Clamp(c, lo, hi);
    }

    /// <summary>Przesuwa kadr; dx i dy to przesunięcie myszy jako ułamek wielkości ramki (zdjęcie "jedzie" za myszą).</summary>
    public void Pan(double dx, double dy)
    {
        var c = Crop;
        ImageCenterX = c.X + c.Width / 2 - dx * c.Width;
        ImageCenterY = c.Y + c.Height / 2 - dy * c.Height;
    }

    /// <summary>Obraca zdjęcie o 90° i przenosi środek kadru tak, by ta sama część zdjęcia została w ramce.</summary>
    public void Rotate(bool clockwise)
    {
        var c = Crop;
        double x = c.X + c.Width / 2, y = c.Y + c.Height / 2;
        var (nx, ny) = clockwise ? (1 - y, x) : (y, 1 - x);
        _cx = nx; _cy = ny;
        Raise(nameof(ImageCenterX)); Raise(nameof(ImageCenterY));
        ImageRotation += clockwise ? 90 : -90;
    }

    public void ResetCrop()
    {
        _zoom = 1; _cx = 0.5; _cy = 0.5;
        Raise(nameof(ImageZoom)); Raise(nameof(ZoomSlider)); Raise(nameof(ImageCenterX)); Raise(nameof(ImageCenterY)); Raise(nameof(Crop));
    }

    /// <summary>Po wybraniu nowego pliku ze zdjęciem (lub usunięciu) wracamy do ustawień domyślnych.</summary>
    public void SetImage(string file)
    {
        _base = null;
        _rotation = 0;
        ResetCrop();
        ImageFile = file;
        Raise(nameof(ImageRotation));
    }
}

public sealed class AppSettings : Observable
{
    // Domyślne rozmiary rosną razem z wysokością ekranu (pierwsze uruchomienie).
    static readonly double Scale = Math.Clamp(SystemParameters.PrimaryScreenHeight / 1080.0, 1.0, 2.5);
    double _tileWidth = Math.Round(560 * Scale), _tileHeight = Math.Round(150 * Scale), _nameFontSize = Math.Round(36 * Scale);
    bool _autoUpdate = true;

    public double TileWidth { get => _tileWidth; set => Set(ref _tileWidth, Math.Clamp(value, 260, 1000)); }
    public double TileHeight { get => _tileHeight; set => Set(ref _tileHeight, Math.Clamp(value, 80, 320)); }
    public double NameFontSize { get => _nameFontSize; set => Set(ref _nameFontSize, Math.Clamp(value, 14, 90)); }

    /// <summary>Automatyczne sprawdzanie i pobieranie aktualizacji z GitHuba.</summary>
    public bool AutoUpdate { get => _autoUpdate; set => Set(ref _autoUpdate, value); }

    public ObservableCollection<Contact> Contacts { get; set; } = new();
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Folder danych: obok programu (jeśli da się tam zapisywać), inaczej %APPDATA%\SeniorWhatsApp.</summary>
    public static string DataDir { get; } = ResolveDataDir();

    static string LegacyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SeniorWhatsApp");

    static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("SENIOR_WA_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var exeDir = Path.GetDirectoryName(AppInfo.ExePath);
        if (!string.IsNullOrEmpty(exeDir) && CanWrite(exeDir)) return exeDir;
        return LegacyDir;
    }

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static string ImagesDir => Path.Combine(DataDir, "images");
    static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        MigrateLegacy();
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("Nie udało się wczytać ustawień: " + ex.Message);
            try { File.Copy(SettingsPath, SettingsPath + ".bad", true); } catch { }
        }
        return new AppSettings();
    }

    /// <summary>Starsze wersje zapisywały dane w %APPDATA%. Przy pierwszym uruchomieniu kopiujemy je obok programu.</summary>
    static void MigrateLegacy()
    {
        try
        {
            if (File.Exists(SettingsPath) || string.Equals(DataDir, LegacyDir, StringComparison.OrdinalIgnoreCase)) return;
            var oldSettings = Path.Combine(LegacyDir, "settings.json");
            if (!File.Exists(oldSettings)) return;
            Directory.CreateDirectory(DataDir);
            File.Copy(oldSettings, SettingsPath, false);
            var oldImages = Path.Combine(LegacyDir, "images");
            if (Directory.Exists(oldImages))
            {
                Directory.CreateDirectory(ImagesDir);
                foreach (var f in Directory.GetFiles(oldImages))
                    File.Copy(f, Path.Combine(ImagesDir, Path.GetFileName(f)), false);
            }
            Log.Write("Przeniesiono ustawienia z " + LegacyDir + " do " + DataDir);
        }
        catch (Exception ex) { Log.Write("Migracja ustawień: " + ex.Message); }
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, SettingsPath, true);
        }
        catch (Exception ex) { Log.Write("Nie udało się zapisać ustawień: " + ex.Message); }
    }

    /// <summary>Kopiuje wybrany obraz do folderu aplikacji i zwraca nazwę pliku.</summary>
    public static string ImportImage(string sourcePath)
    {
        Directory.CreateDirectory(ImagesDir);
        var name = Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath).ToLowerInvariant();
        File.Copy(sourcePath, Path.Combine(ImagesDir, name), true);
        return name;
    }

    public static ImageSource? LoadImage(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        try
        {
            var path = Path.Combine(ImagesDir, file);
            if (!File.Exists(path)) return null;
            var uri = new Uri(path);

            // Duże zdjęcia z aparatu zmniejszamy przy wczytywaniu (oszczędza pamięć), ale zostawiamy zapas na powiększanie.
            var natural = BitmapDecoder.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;       // nie blokuje pliku
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (natural > 1800) bmp.DecodePixelWidth = 1800;
            bmp.UriSource = uri;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Write("Nie udało się wczytać obrazu " + file + ": " + ex.Message);
            return null;
        }
    }
}

public static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DataDir);
                var path = Path.Combine(SettingsStore.DataDir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
