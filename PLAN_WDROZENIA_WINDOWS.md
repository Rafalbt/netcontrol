# Plan wdrożenia — Przepustnica na Windows

Dokument techniczny: jak z prototypu (`Przepustnica.dc.html`) zbudować działającą aplikację
desktopową ograniczającą ruch sieciowy per aplikacja na Windows.

---

## 1. Największe wyzwanie (przeczytaj najpierw)

Windows **nie ma prostego, wbudowanego API „ustaw limit X Mb/s dla procesu Y"**. Realny throttling
per-proces wymaga wejścia na poziom sterownika/filtra sieci. Są trzy podejścia — wybór definiuje
całą resztę:

| Podejście | Jak działa | Plusy | Minusy |
|---|---|---|---|
| **A. WFP + WinDivert** (rekomendowane na MVP) | Sterownik WinDivert przechwytuje pakiety, w user-mode kolejkujesz/zwalniasz je do zadanej przepustowości (token bucket) per PID | Pełna kontrola prędkości i godzin; działa dla dowolnej aplikacji; nie trzeba pisać własnego sterownika | Wymaga uprawnień admina; WinDivert ma sterownik podpisany, ale trzeba go dystrybuować; licencja WinDivert (LGPL/komercyjna) |
| **B. Windows QoS Policy** (GPO / `New-NetQosPolicy`) | Reguły QoS po nazwie aplikacji ograniczające throttle rate | Natywne, bez sterownika | Ograniczanie głównie wysyłki/priorytetu, słaba kontrola pobierania per-app; mało granularne |
| **C. Lokalny proxy** | Ruch aplikacji kierowany przez lokalny proxy, który throttluje | Bez sterownika | Działa tylko dla HTTP(S)/aplikacji wspierających proxy; nie dla dowolnego ruchu/UDP/gier |

**Rekomendacja: podejście A (WinDivert).** Reszta planu zakłada A. Podejścia B/C można dołożyć jako
fallback/tryb light.

> Ograniczanie *pobierania* (download) jest z natury przybliżone — pakiety już dotarły do łącza;
> throttling polega na opóźnianiu potwierdzeń/kolejkowaniu, co spowalnia nadawcę (TCP). Komunikuj to
> jako „limit ~X Mb/s", nie twardą gwarancję co do bajta.

---

## 2. Architektura

```
┌─────────────────────────────┐        IPC (named pipe / localhost + token)
│  UI (Tauri, HTML/CSS/JS)     │  <───────────────────────────────────────┐
│  ekrany 2a–2d z prototypu    │                                          │
│  bez uprawnień admina        │                                          │
└─────────────────────────────┘                                          │
                                                                          ▼
                                              ┌───────────────────────────────────────┐
                                              │  Usługa Windows (backend, admin/SYSTEM) │
                                              │  - silnik reguł (limit + harmonogram)   │
                                              │  - throttler (token-bucket per PID)     │
                                              │  - WinDivert (przechwytywanie pakietów) │
                                              │  - licznik ruchu per proces (ETW)       │
                                              │  - baza reguł + historia (SQLite)       │
                                              └───────────────────────────────────────┘
```

- **UI** = ten sam HTML co prototyp, opakowany w **Tauri** (lekki, natywne okno; Rust tylko jako
  cienka warstwa). Alternatywa: Electron (szybszy start, ale ~150 MB). UI działa bez admina.
- **Usługa** = proces z uprawnieniami (Windows Service uruchamiany jako `LocalSystem`, albo helper
  z manifestem `requireAdministrator`). Tu dzieje się cała logika sieciowa. Język: **Rust** lub
  **C#/.NET** (dobre bindingi do WinDivert i ETW; łatwe pakowanie usługi).
- **IPC**: named pipe `\\.\pipe\przepustnica` lub localhost WebSocket z tokenem. UI wysyła komendy
  (`setRule`, `deleteRule`, `toggle`), usługa strumieniuje telemetrię (`usage` co 1 s).

---

## 3. Mapowanie funkcji UI → backend

| Element UI (ekran) | Co robi backend |
|---|---|
| „+ Dodaj aplikację" → wybór aplikacji (`2b`) | Lista uruchomionych procesów (`EnumProcesses` + ścieżka .exe + ikona); wskazanie pliku .exe ręcznie |
| Limit prędkości (suwak Mb/s) (`2b`) | Parametr token-bucket: `rate = X Mb/s`, `burst` ~ 1–2× rate |
| Godziny działania (`2b`) | Harmonogram; scheduler włącza/wyłącza regułę o granicy godzin |
| Wiersz „Teraz" + pasek (`2a`) | Bieżąca przepływność per PID z licznika ETW |
| Status Aktywna/Ograniczana/Uśpiona (`2a`) | Aktywna=w oknie i <limit; Ograniczana=bucket dławi; Uśpiona=poza godzinami (ruch procesu blokowany/wstrzymany) |
| Monitor na żywo — wykres (`2c`) | Strumień próbek co 1 s; linia limitu = wartość reguły |
| Karty „limit egzekwowany / zaoszczędzono" (`2c`) | Różnica między zapotrzebowaniem (kolejka) a przepuszczoną przepustowością |
| Historia + KPI (`2d`) | Agregacja z SQLite (dzień/tydzień/miesiąc); „zaoszczędzono" = suma zdławionego ruchu |

**Dopasowanie procesu do reguły**: reguła trzyma ścieżkę .exe (i opcjonalnie hash). Przy starcie
procesu o pasującej ścieżce → przypnij jego PID i połączenia do reguły. Uwaga na aplikacje
wieloprocesowe (przeglądarki, launchery) — dopuść dopasowanie po drzewie procesów.

---

## 4. Rdzeń throttlingu (token bucket)

Dla każdej aktywnej reguły:
1. WinDivert przechwytuje pakiety powiązane z połączeniami danego PID (mapowanie
   połączenie→PID przez `GetExtendedTcpTable`/`GetExtendedUdpTable`).
2. Token bucket: `tokens += rate * dt` (cap = burst). Pakiet przepuszczony, jeśli starczy tokenów;
   inaczej trafia do kolejki i jest zwalniany, gdy tokeny narosną.
3. Dla downloadu skuteczniej działa dławienie strony odbioru (opóźnianie ACK / kontrola okna),
   ale prosty bucket na przychodzących pakietach też redukuje efektywną prędkość — zacznij od niego.
4. Poza godzinami: `drop` pakietów procesu (lub wstrzymanie) → status „Uśpiona".

Telemetria: co 1 s licz bajty przepuszczone i zakolejkowane per reguła → wyślij do UI.

---

## 5. Etapy (proponowana kolejność)

**Etap 0 — Szkielet (1 tydz.)**
- Tauri + osadzenie ekranów 2a–2d (statyczne, dane mockowane w UI).
- Zdefiniuj kontrakt IPC (schematy komend i telemetrii). UI działa na fejkowym backendzie.

**Etap 1 — Odczyt (monitor bez limitów) (1–2 tyg.)**
- Usługa Windows + IPC. Lista procesów z ikonami. Licznik ruchu per proces (ETW: dostawca
  `Microsoft-Windows-Kernel-Network`). Monitor `2c` i „Teraz" na `2a` na **prawdziwych** danych.
- Persist reguł i próbek w SQLite → zasil Historię `2d`.

**Etap 2 — Egzekwowanie (rdzeń) (2–3 tyg.)**
- Integracja WinDivert, mapowanie połączenie→PID, token-bucket na jednej aplikacji testowej.
- Limit prędkości z `2b` realnie działa; status „Ograniczana"; linia limitu na wykresie się zgadza.

**Etap 3 — Harmonogram + wiele reguł (1 tyg.)**
- Scheduler godzin (włącz/wyłącz/„Uśpiona"). Wiele jednoczesnych reguł. Obsługa startu/zamknięcia
  procesów i aplikacji wieloprocesowych.

**Etap 4 — Dopieszczenie + pakowanie (1–2 tyg.)**
- Autostart usługi, obsługa błędów, powiadomienia (np. „limit przekroczony").
- Installer (MSI/WiX lub NSIS) instalujący usługę + UI; **podpis kodu** (Authenticode) —
  bez tego SmartScreen/UAC będą straszyć użytkownika.
- Ustawienia (globalny wyłącznik, uruchamianie z systemem, pojemność łącza).

---

## 6. Stos technologiczny (rekomendacja)

- **UI**: Tauri 2 + obecny HTML/CSS/JS (lub przepisany na React, jeśli zespół woli). Fonty
  (Space Grotesk, Manrope) dołączone lokalnie. Ikony: Lucide.
- **Backend/usługa**: **C#/.NET 8** jako usługa Windows (najszybsza ścieżka: dobre wsparcie ETW,
  WinDivert.NET, `System.ServiceProcess`, WiX). Alternatywa: **Rust** (`windows` crate + `windivert`
  crate) — spójny z Tauri, ale więcej pracy ręcznej.
- **Dane**: SQLite (reguły + historia próbek/agregatów).
- **Throttling**: WinDivert 2.x.
- **Installer**: WiX (MSI) + certyfikat do podpisu kodu.

---

## 7. Ryzyka i uwagi

- **Uprawnienia**: throttling wymaga admina/SYSTEM. Rozdziel UI (bez admina) od usługi (z admin) —
  nie zmuszaj całej apki do UAC.
- **Antywirus/SmartScreen**: sterownik sieciowy + niepodpisany plik = ostrzeżenia. Podpis kodu jest
  praktycznie obowiązkowy do dystrybucji.
- **Download vs upload**: bądź szczery w UI, że to „limit ~X Mb/s". Rozważ osobne limity pobierania
  i wysyłania w przyszłości.
- **HTTPS/QUIC/UDP**: WinDivert działa na warstwie pakietów, więc obejmuje też QUIC/UDP (gry) —
  przewaga nad proxy. Ale wymaga poprawnego mapowania połączeń UDP→PID.
- **Aplikacje wieloprocesowe** (Chrome, Steam): reguła po ścieżce + drzewie procesów, nie po
  pojedynczym PID.
- **VPN/inne filtry**: kolejność filtrów WFP może kolidować z VPN/antywirusem — testuj z popularnym
  oprogramowaniem.
- **Licencja WinDivert**: sprawdź warunki (LGPL/komercyjna) pod kątem dystrybucji.

---

## 8. Definicja MVP

Minimalny sensowny produkt = **Etapy 0–2** dla jednej reguły + monitor na żywo:
użytkownik dodaje jedną aplikację, ustawia limit Mb/s, a monitor pokazuje, że prędkość jest
faktycznie trzymana przy limicie. Harmonogram, historia i wiele reguł to kolejne przyrosty.
