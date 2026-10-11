# Senior WhatsApp

**Prosta nakładka na WhatsApp dla osób starszych i słabowidzących.**

Zamiast skomplikowanego okna WhatsApp – duże, kontrastowe kafelki z osobami. Jedno kliknięcie wybiera osobę, drugie
(duży zielony przycisk z kamerką) rozpoczyna rozmowę wideo. Podczas rozmowy w prawym dolnym rogu ekranu świeci duża
czerwona słuchawka, która ją kończy. Wszystko ustawia się raz, a użytkownik widzi tylko to, co potrzebne.

> ### 🤖 Program powstaje z intensywnym udziałem AI
> Ten projekt jest tworzony we współpracy z asystentem AI (**Claude**, Anthropic). AI napisało większość kodu, teksty
> interfejsu i tę dokumentację, na podstawie wymagań i uwag właściciela projektu, który kierował pomysłem i sprawdzał
> program w działaniu. Kod miał **ograniczony przegląd przez człowieka** i był testowany głównie na Windows 11 z WhatsApp
> Desktop (ze sklepu Microsoft) w języku polskim. Przejrzyj go, zanim zaczniesz na nim polegać. Zgłoszenia błędów i
> propozycje zmian są mile widziane.
>
> To nieoficjalna nakładka – nie jest związana z firmą WhatsApp ani Meta.

## Funkcje

* **Kafelki osób** po lewej: zdjęcie po lewej stronie kafelka, nazwa po prawej. Szerokość, wysokość kafelka i wielkość
  czcionki ustawia się suwakami (z podglądem na żywo).
* **Wybrana osoba**: na środku u góry duże zdjęcie z podpisem, pod spodem zielony przycisk z kamerką i słuchawką.
* **Własne nazwy i zdjęcia** – niezależne od tego, jak osoba jest zapisana w WhatsApp (np. „Babcia Ania”).
  Zdjęcie można **przesunąć myszą i powiększyć** (kółko myszy lub suwak), żeby twarz była w środku kadru.
* **Rozmowa wideo jednym przyciskiem.** Program otwiera WhatsApp, sprawdza, że otwarta jest rozmowa z właściwą osobą
  (nigdy nie dzwoni „w ciemno”), klika „Rozmowa wideo” i pilnuje połączenia.
* **Okno rozmowy wideo otwiera się zmaksymalizowane** (program pilnuje tego przez całą rozmowę).
* **Czerwona słuchawka** w prawym dolnym rogu, zawsze na wierzchu, kończy rozmowę.
* **Samo ponawia próby**: jeśli WhatsApp nie był uruchomiony, siedział w zasobniku albo jeszcze się wczytywał, program
  czeka, ponawia otwarcie rozmowy i kliknięcie (do 3 prób). Użytkownik nie musi nic robić ani nikogo wołać.
* **Brak kamery**: duża informacja „Nie wykryto kamery” i przycisk „Zadzwoń bez kamery” (zwykła rozmowa głosowa).
* **Bezpieczne ustawienia**: przycisk „Ustawienia” (na dole, mały) trzeba **przytrzymać przez 1 sekundę**. Krótkie
  kliknięcie pokazuje na 3 sekundy podpowiedź. Czerwony przycisk **X** (64 × 64) w prawym górnym rogu wyłącza program.
* **Import z WhatsApp**: Ustawienia → „Importuj z WhatsApp…” wczytuje nazwy rozmów z listy czatów. WhatsApp nie
  udostępnia numerów telefonów ani zdjęć, więc takie osoby są wywoływane po nazwie czatu, a zdjęcia dodaje się ręcznie.
* Zawsze uruchamia się **zmaksymalizowany**. Tylko jedna kopia programu naraz.
* **Automatyczne aktualizacje** z GitHub Releases (patrz niżej).

## Uruchomienie

1. Zainstaluj WhatsApp ze sklepu Microsoft i zaloguj się.
2. Pobierz `SeniorWhatsApp-<wersja>-win-x64.zip` z zakładki [Releases](../../releases), rozpakuj w dowolnym miejscu i
   uruchom `SeniorWhatsApp.exe`. Plik jest samodzielny – **nie wymaga instalowania .NET**.
3. Przytrzymaj **Ustawienia** przez sekundę → **Dodaj osobę** → nazwa, numer WhatsApp (np. `+48 600 100 200`), zdjęcie.

Wymagania: Windows 10/11 (64-bit), WhatsApp Desktop ze sklepu Microsoft, w języku polskim lub angielskim.

Aby program startował razem z Windows: `Win + R` → `shell:startup` → wstaw tam skrót do `SeniorWhatsApp.exe`.

## Gdzie są ustawienia

**Obok pliku `SeniorWhatsApp.exe`**: `settings.json` (osoby, rozmiary), folder `images` (zdjęcia) i `log.txt`.
Cały folder można skopiować na inny komputer. Jeśli folder programu jest tylko do odczytu (np. `Program Files`),
dane trafiają do `%APPDATA%\SeniorWhatsApp`. Ustawienia z wcześniejszych wersji (z `%APPDATA%`) są kopiowane
automatycznie przy pierwszym uruchomieniu.

## Aktualizacje

Program co kilka godzin pyta GitHub o najnowsze wydanie repozytorium zapisanego w `Source/Models.cs`
(`AppInfo.DefaultRepository`). Nowsza wersja jest pobierana w tle i instalowana, gdy nikt nie korzysta z programu
(5 minut bez ruchu myszą) albo przy następnym uruchomieniu; w Ustawieniach można też zrobić to ręcznie
(„Sprawdź teraz” / „Zainstaluj i uruchom ponownie”) lub wyłączyć automatyczne aktualizacje.
Dopóki w repozytorium nie ma żadnego wydania, sprawdzanie kończy się po cichu.

Pobierany plik musi pochodzić z `github.com` / `githubusercontent.com` (przez HTTPS), być programem (`MZ`) i –
jeśli GitHub poda sumę kontrolną – zgadzać się z sumą SHA-256.

### Jak wydać nową wersję

```bash
git tag v1.2.0
git push origin v1.2.0
```

Workflow [`release.yml`](.github/workflows/release.yml) zbuduje program (numer wersji bierze z tagu), spakuje do
`SeniorWhatsApp-1.2.0-win-x64.zip` i opublikuje wydanie – to właśnie ten plik pobierają programy użytkowników.

## Budowanie z kodu

Wymagany [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) na Windows.

```bash
cd Source
dotnet build          # wersja debug
cd ..
build.cmd             # samodzielny SeniorWhatsApp.exe w tym folderze
```

## Jak to działa i diagnostyka

WhatsApp Desktop jest sterowany przez **UI Automation** (jak czytnik ekranu): program znajduje przyciski po nazwie
(„Rozmowa wideo”, „Rozmowa głosowa”, „End call”). Jeśli po aktualizacji WhatsApp zmieni nazwy przycisków, wystarczy
poprawić listy nazw na górze `Source/WhatsAppController.cs`.

`SeniorWhatsApp.exe --diagnoza` (nic nie klika) zapisuje w `log.txt`, czy WhatsApp i kamera są widoczne oraz czy
program widzi przyciski rozmowy.

Odpowiedzialność za rozmowy: program dzwoni **tylko** po potwierdzeniu, że w WhatsApp otwarta jest rozmowa z wybraną
osobą (porównanie nazwy czatu). Przy pierwszym połączeniu z numerem nazwa czatu jest zapamiętywana i sprawdzana później.
