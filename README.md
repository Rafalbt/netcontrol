# Przepustnica

Aplikacja desktopowa dla Windows do **ograniczania przepustowości sieci per aplikacja**.
Dodajesz aplikację, ustawiasz limit pobierania (np. 10 Mb/s) i opcjonalnie godziny działania
(np. 16:00–22:00) — wbudowany monitor na żywo pokazuje, że limit jest faktycznie egzekwowany.
Historia zbiera statystyki transferu i działania limitów.

**Stan projektu:** funkcjonalnie kompletna (Etapy 0–3 planu wdrożenia + instalator).
Do zrobienia: podpis kodu (Authenticode).

---

## Spis treści

- [Jak to działa (architektura)](#jak-to-działa-architektura)
- [Wymagania](#wymagania)
- [Instalacja (MSI)](#instalacja-msi)
- [Instrukcja użytkownika](#instrukcja-użytkownika)
- [Praca deweloperska](#praca-deweloperska)
- [Budowanie instalatora](#budowanie-instalatora)
- [Rejestracja usługi bez MSI](#rejestracja-usługi-bez-msi)
- [Dokumentacja IPC](#dokumentacja-ipc)
- [Struktura projektu](#struktura-projektu)
- [Dane i pliki na dysku](#dane-i-pliki-na-dysku)
- [Rozwiązywanie problemów](#rozwiązywanie-problemów)
- [Znane ograniczenia](#znane-ograniczenia)
- [Licencje zależności](#licencje-zależności)

---

## Jak to działa (architektura)

Dwa procesy rozdzielone granicą uprawnień:

```
Proces UI (Tauri 2 + React, bez uprawnień administratora)
  └─ IPC: named pipe \\.\pipe\przepustnica + token autoryzacyjny
       └─ Usługa Windows „Przepustnica" (LocalSystem)
              ├─ Silnik reguł (limit Mb/s + harmonogram godzinowy)
              ├─ Throttler — przechwytywanie pakietów WinDivert 2.x,
              │   pacer GCRA per reguła (pakiety ponad limit są opóźniane,
              │   nie gubione); poza oknem harmonogramu ruch jest blokowany
              ├─ Licznik ruchu per proces (ETW, dostawca kernelowy
              │   Microsoft-Windows-Kernel-Network)
              └─ SQLite — reguły, ustawienia i historia transferu
```

Kluczowe decyzje techniczne:

- **Dopasowanie reguła→proces po nazwie pliku .exe** (np. `chrome.exe`) — naturalnie
  obejmuje aplikacje wieloprocesowe (wszystkie procesy potomne przeglądarki dzielą nazwę).
- **Mapowanie połączenie→PID zdarzeniowo** przez drugi uchwyt WinDivert na warstwie
  Socket (sniff + recv-only). Odpytywanie tablicy TCP cyklicznie gubi krótkotrwałe
  połączenia — szybkie pobieranie potrafiło „uciec" limitowi zanim odświeżył się snapshot.
- **Limit dotyczy pobierania (inbound)**. Ograniczanie downloadu jest z natury przybliżone —
  pakiety już dotarły do karty sieciowej; opóźnianie ich doręczenia spowalnia nadawcę przez
  mechanikę TCP (ACK pacing). W UI komunikowane jako „limit ~X Mb/s".
- **UI działa też bez usługi** — w trybie demo z lokalnym symulatorem telemetrii
  (pasek boczny pokazuje wtedy „Tryb demo — brak usługi").

## Wymagania

**Maszyna docelowa (użytkownik końcowy):**

| Składnik | Wymaganie |
|---|---|
| System | Windows 10/11 x64 |
| Uprawnienia | Administrator tylko do instalacji (usługa działa jako LocalSystem) |
| WebView2 Runtime | Wymagany przez UI; Windows 10/11 ma go domyślnie |
| .NET Runtime | **Niewymagany** — usługa jest publikowana self-contained |

**Środowisko deweloperskie (budowanie ze źródeł):**

| Narzędzie | Wersja / uwagi |
|---|---|
| Node.js + npm | 22+ |
| Rust (rustup) + MSVC Build Tools | toolchain `x86_64-pc-windows-msvc`; `cargo` w `~/.cargo/bin` |
| .NET SDK | 8.0 |
| WiX Toolset | **5.0.2** przez `dotnet tool install --global wix --version 5.0.2` (WiX 6/7 wymaga płatnej licencji OSMF) |

## Instalacja (MSI)

1. Zbuduj instalator (patrz [Budowanie instalatora](#budowanie-instalatora)) albo weź gotowy
   `installer/Przepustnica-0.1.1.msi`.
2. Uruchom MSI (podwójny klik) i potwierdź UAC.

Instalator:
- kopiuje aplikację do `C:\Program Files\Przepustnica\` (UI: `Przepustnica.exe`,
  usługa: podkatalog `service\`),
- rejestruje i **uruchamia usługę Windows „Przepustnica"** (LocalSystem, autostart),
  przejmując ewentualną usługę zarejestrowaną wcześniej ręcznie,
- dodaje skrót **Przepustnica** w Menu Start,
- wspiera aktualizacje: nowszy MSI (ten sam UpgradeCode) sam odinstaluje starą wersję.

**Odinstalowanie:** Ustawienia → Aplikacje → Przepustnica → Odinstaluj
(albo `msiexec /x Przepustnica-0.1.1.msi`). Usługa jest zatrzymywana i wyrejestrowywana.
Dane w `%ProgramData%\Przepustnica` **zostają** — usuń ręcznie, jeśli chcesz wyczyścić
historię i reguły.

> **SmartScreen:** MSI nie jest podpisany certyfikatem Authenticode, więc na obcych
> maszynach Windows wyświetli ostrzeżenie „Windows protected your PC" → „More info"
> → „Run anyway". Podpis kodu jest na liście rzeczy do zrobienia.

## Instrukcja użytkownika

Aplikacja ma cztery ekrany (nawigacja w ciemnym pasku bocznym):

### Aplikacje (ekran główny)

Lista reguł z kartami KPI (liczba aplikacji, sumaryczny ruch „Teraz", ile ograniczanych,
transfer „Dziś"). Każdy wiersz pokazuje limit, godziny, bieżącą prędkość z paskiem
i status:

- 🟢 **Aktywna** — w oknie harmonogramu, poniżej limitu
- 🟠 **Ograniczana** — pacer aktywnie dławi ruch do limitu (wiersz podświetlony, pasek pulsuje)
- ⚪ **Uśpiona** — poza godzinami działania (ruch procesu jest wtedy blokowany) albo reguła wyłączona

Menu **⋯** w wierszu: Edytuj / Wyłącz–Włącz / Usuń limit.

### Dodaj aplikację (przycisk „+ Dodaj aplikację")

- **Wybór aplikacji:** lista realnie uruchomionych procesów (najpierw te generujące ruch
  sieciowy) albo przycisk **„Wskaż plik .exe…"** z natywnym oknem wyboru pliku.
- **Limit prędkości pobierania:** suwak 0,5–100 Mb/s zsynchronizowany z polem liczbowym;
  skrajna prawa pozycja = „bez limitu" (tylko monitoring, bez dławienia).
- **Godziny działania:** „Cała doba" albo „Wybrane godziny" (od–do; okno może przechodzić
  przez północ, np. 22:00→06:00). Poza oknem aplikacja ma status Uśpiona i jej ruch
  jest wstrzymany.

### Monitor na żywo

Wykres liniowy prędkości wszystkich reguł, aktualizowany co 1 s (zakresy 60 s / 15 min /
1 godz). Dla reguły aktualnie ograniczanej rysowana jest **przerywana linia limitu** —
krzywa „przykleja się" do niej, co potwierdza egzekwowanie. Poniżej karty podsumowania
(które aplikacje są dławione, ile Mb/s „zaoszczędzono").

### Historia

Statystyki z bazy: pobrano łącznie, **zaoszczędzono limitami** (suma ruchu opóźnionego
przez pacer + zablokowanego poza harmonogramem — estymata „popyt minus przepuszczone"),
liczba zdarzeń ograniczenia. Wykres słupkowy skumulowany per aplikacja
(okresy: Dzień / Tydzień / Miesiąc).

## Praca deweloperska

Codzienny cykl (dwa terminale):

```powershell
# Terminal 1 — usługa w trybie konsolowym (KONIECZNIE jako administrator,
# inaczej sesja ETW nie wystartuje i liczniki będą zerowe):
cd service\PrzepustnicaService
dotnet run

# Terminal 2 — UI:
cd ui
npm install          # tylko za pierwszym razem
npm run tauri dev
```

Pasek boczny pokaże **„Usługa połączona"**, gdy pipe zadziała (auto-reconnect co 2 s).
Bez usługi UI przechodzi w tryb demo z symulatorem.

Inne przydatne komendy:

```powershell
npm run build        # ui/ — sam typecheck + build frontendu (tsc && vite build)
dotnet build         # service/PrzepustnicaService — kompilacja bez uruchamiania
```

> Jeśli działa zarejestrowana usługa Windows, zatrzymaj ją przed startem trybu
> konsolowego (`Stop-Service Przepustnica` jako admin) — obie instancje biłyby się
> o pipe i sesję ETW.

## Budowanie instalatora

Jednym poleceniem (buduje UI w release, publikuje usługę self-contained, składa MSI):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
# wynik: installer\Przepustnica-0.1.1.msi
```

Kroki wykonywane pod spodem (gdyby trzeba było ręcznie):

```powershell
cd ui && npm run tauri build -- --no-bundle          # → ui\src-tauri\target\release\ui.exe
cd service\PrzepustnicaService
dotnet publish -c Release -r win-x64 --self-contained true -o ..\..\dist\service
cd installer && wix build Przepustnica.wxs -arch x64 -o Przepustnica-0.1.1.msi
```

Uwagi:
- `installer/Przepustnica.wxs` ma **stały UpgradeCode** — nie zmieniać, inaczej
  aktualizacje przestaną odinstalowywać starych wersji.
- Publish jest self-contained — katalog `dist\service` zawiera cały runtime .NET
  (~80 MB, w MSI kompresuje się do ~33 MB), w tym podkatalog `amd64\` z
  `KernelTraceControl.dll` **wymaganym przez ETW** (harvest w wxs jest rekurencyjny).
- Wersja MSI: atrybut `Version` w `Przepustnica.wxs` + nazwa pliku wyjściowego
  w `build-installer.ps1`.

## Rejestracja usługi bez MSI

Do developmentu / szybkich testów są skrypty (uruchamiane jako administrator):

```powershell
# najpierw opublikuj usługę:
cd service\PrzepustnicaService
dotnet publish -c Release -r win-x64 --self-contained true -o ..\..\dist\service

# rejestracja + start (LocalSystem, autostart, auto-restart po awarii):
powershell -ExecutionPolicy Bypass -File service\install-service.ps1

# wyrejestrowanie:
powershell -ExecutionPolicy Bypass -File service\uninstall-service.ps1
```

`install-service.ps1` sam zatrzymuje instancje konsolowe i starszą usługę.

> Skrypty `.ps1` w tym repo są celowo pisane **bez polskich znaków** — Windows
> PowerShell 5.1 czyta pliki bez BOM w kodowaniu ANSI i ogonki psują parser.

## Dokumentacja IPC

Kanał: named pipe `\\.\pipe\przepustnica`, komunikaty JSON (camelCase) rozdzielane
znakiem nowej linii. Autoryzacja: usługa przy starcie zapisuje losowy token do
`%ProgramData%\Przepustnica\ipc.token`; klient musi otworzyć sesję komunikatem
`hello` z tym tokenem, inaczej połączenie jest zamykane. ACL pipe: Authenticated Users.

**Komendy UI → usługa:**

| Komenda | Ładunek | Efekt |
|---|---|---|
| `hello` | `{token}` | otwarcie sesji (musi być pierwszym komunikatem); usługa odsyła `rules` |
| `getRules` | — | odesłanie `rules` |
| `setRule` | `{rule}` | upsert reguły (po `rule.id`), broadcast `rules` |
| `deleteRule` | `{id}` | usunięcie reguły, broadcast `rules` |
| `toggle` | `{id}` | przełączenie `enabled`, broadcast `rules` |
| `listProcesses` | — | odesłanie `processes` |
| `getHistory` | `{period}` (`day`/`week`/`month`) | odesłanie `history` |
| `getSettings` / `setSettings` | `{settings}` | odczyt/zapis ustawień, broadcast `settings` |

**Komunikaty usługa → UI:**

| Typ | Kiedy | Zawartość |
|---|---|---|
| `rules` | po `hello` i po każdej zmianie | `{rules: [{id, name, exeMatch, iconColor, initials, limitMbps, schedule: {mode, from, to}, enabled}]}` |
| `usage` | co 1 s (broadcast) | `{ts, apps: {id: {mbps, downMbps, upMbps, throttled, status}}, totalMbps, todayGb}` |
| `processes` | odpowiedź | `{processes: [{name, exeMatch, path, pidCount}]}` — posortowane: najpierw z ruchem |
| `history` | odpowiedź | `{period, labels, seriesGb: {ruleId: number[]}, savedGb, throttleEvents}` |
| `settings` | odpowiedź na `getSettings` i broadcast po zmianie | `{settings: {enforcementEnabled, linkCapacityMbps}}` |
| `error` | błąd komendy | `{command, message}` |

Struktura reguły (`AppRule`) jest wspólna dla UI, IPC i bazy — patrz
`ui/src/types.ts` i `service/PrzepustnicaService/Models.cs`.

## Struktura projektu

| Ścieżka | Co to |
|---|---|
| `ui/` | UI: Tauri 2 + React 19 (TypeScript, Vite). `src-tauri/src/lib.rs` = most pipe↔webview |
| `service/PrzepustnicaService/` | Usługa C#/.NET 8: ETW, WinDivert, SQLite, serwer IPC |
| `service/install-service.ps1` | Rejestracja usługi Windows (dev, bez MSI) |
| `installer/` | WiX 5: `Przepustnica.wxs` + `build-installer.ps1` → MSI |
| `dist/service/` | Wyjście `dotnet publish` (generowane, nie edytować) |
| `throttle-poc/` | Pierwotny proof-of-concept WinDivert (referencja historyczna) |
| `docs/DESIGN_HANDOFF.md` | Specyfikacja projektu UI ekran-po-ekranie (design tokens, interakcje) |
| `PLAN_WDROZENIA_WINDOWS.md` | Techniczny plan wdrożenia (architektura, etapy, ryzyka) |
| `Przepustnica.dc.html` | Prototyp hi-fi wszystkich ekranów (otwierany w przeglądarce) |

## Dane i pliki na dysku

| Ścieżka | Zawartość |
|---|---|
| `%ProgramData%\Przepustnica\przepustnica.db` | SQLite: reguły, ustawienia, historia (`usage_minutes`) |
| `%ProgramData%\Przepustnica\ipc.token` | Token autoryzacyjny pipe (tworzony przez usługę) |
| `C:\Program Files\Przepustnica\` | Instalacja MSI (UI + `service\`) |

## Rozwiązywanie problemów

| Objaw | Przyczyna / rozwiązanie |
|---|---|
| UI pokazuje „Tryb demo — brak usługi" | Usługa nie działa: `Get-Service Przepustnica` → `Start-Service Przepustnica` (admin). W dev: uruchom `dotnet run` **jako administrator** |
| Liczniki stoją na zerze mimo działającej usługi | Usługa działa bez uprawnień admina — sesja ETW nie wystartowała. Tryb konsolowy wymaga elevated terminala |
| Limit „nie łapie" szybkich pobrań | Upewnij się, że dopasowanie jest po właściwej nazwie exe (np. pobieranie robi proces potomny o innej nazwie) |
| MSI: ostrzeżenie SmartScreen | Pakiet niepodpisany — „More info" → „Run anyway" (do czasu wdrożenia podpisu kodu) |
| Konflikt z VPN/antywirusem | WinDivert działa w stosie WFP — kolejność filtrów może kolidować; testuj z konkretnym oprogramowaniem |
| Logi usługi | Tryb usługi: Podgląd zdarzeń → Dzienniki aplikacji (źródło „Przepustnica"). Tryb konsolowy: stdout |
| Test przepustowości do weryfikacji limitów | `curl -o nul https://speed.cloudflare.com/__down?bytes=20000000` (uwaga: po kilkunastu pobraniach potrafi zwrócić 403; zapas: `https://proof.ovh.net/files/10Mb.dat`) |

## Znane ograniczenia

- **Limit działa na pobieranie (inbound)** — upload nie jest ograniczany.
- Egzekwowanie jest **przybliżone** (natura dławienia downloadu) — realna prędkość
  osiada tuż przy limicie, nie co do bajta.
- Pakiet **niepodpisany** (SmartScreen/UAC ostrzega na obcych maszynach).
- Ruch UDP/QUIC jest przechwytywany (przewaga nad proxy), ale mapowanie UDP→PID
  bywa mniej pewne niż dla TCP.
- UI wymaga WebView2 Runtime (na Win10/11 obecny domyślnie).

## Licencje zależności

- **WinDivert** — LGPL v3 / licencja komercyjna. Przy dystrybucji publicznej
  zweryfikuj warunki (https://reqrypt.org/windivert.html).
- WiX Toolset 5 — MS-RL. Tauri — MIT/Apache-2.0. React — MIT.
  TraceEvent (Microsoft.Diagnostics.Tracing) — MIT.
