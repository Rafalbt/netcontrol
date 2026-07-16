# Handoff: Przepustnica — desktopowy menedżer limitów sieci (Windows)

## Overview
Przepustnica to aplikacja desktopowa (Windows) do ograniczania ruchu sieciowego **per aplikacja**
na jednym komputerze. Użytkownik dodaje aplikację, ustawia jej **limit prędkości pobierania**
(np. 10 Mb/s) oraz **godziny działania** (np. 16:00–22:00), a wbudowany **monitor na żywo**
potwierdza, że limity są faktycznie egzekwowane. Dodatkowo dostępna jest **historia** transferu
i egzekwowania limitów.

Wybrany kierunek wizualny: **„Chłodny dashboard"** — jasny interfejs z ciemną nawigacją boczną,
akcent fioletowo-indygo.

## About the Design Files
Pliki w tej paczce (`Przepustnica.dc.html`) to **referencje projektowe stworzone w HTML** — prototypy
pokazujące zamierzony wygląd i zachowanie, **a nie kod produkcyjny do skopiowania 1:1**. Zadaniem
dewelopera jest **odtworzyć te ekrany w docelowym środowisku aplikacji desktopowej** (patrz plan
wdrożenia poniżej — rekomendacja: **Tauri** front-end w HTML/CSS/JS lub React), zachowując zaproponowany
układ, kolory, typografię i przepływy, ale korzystając z ustalonych wzorców i bibliotek projektu.

Plik `Przepustnica.dc.html` to „Design Component" — otwiera się w przeglądarce. Zawiera **dwie tury**:
- **Tura 1** (`1a`, `1b`, `1c`) — trzy kierunki wizualne. Zatwierdzony: **1b**.
- **Tura 2** (`2a`, `2b`, `2c`, `2d`) — rozwinięcie 1b w pełny przepływ. **To jest wersja do wdrożenia.**

## Fidelity
**High-fidelity (hifi).** Finalne kolory, typografia, odstępy i układ. UI należy odtworzyć wiernie
pod względem wyglądu, używając bibliotek/wzorców środowiska docelowego. Dane liczbowe na ekranach
(prędkości, GB, wykresy) są **przykładowe** — w produkcji pochodzą z usługi monitorującej.

---

## Screens / Views

Wszystkie ekrany dzielą wspólne **okno aplikacji** (rogi 18px, cień) i **nawigację boczną** (220px):
pozycje „Aplikacje", „Monitor na żywo", „Historia", „Ustawienia". Aktywna pozycja ma tło
`oklch(0.7 0.16 275 / 0.28)` i biały tekst; nieaktywne `#b9b4d4`. Tło sidebaru `oklch(0.28 0.06 275)`
(ciemny fiolet). Logo: kwadrat 30px, `oklch(0.7 0.16 275)`, litera „P", + napis „Przepustnica"
(Space Grotesk, 600).

### 1. Aplikacje (ekran główny) — `2a`
- **Purpose**: przegląd wszystkich aplikacji z limitami; punkt startowy, dodawanie nowej aplikacji.
- **Layout**: grid `220px 1fr` (sidebar + treść). Treść: nagłówek z tytułem i przyciskiem „+ Dodaj
  aplikację", rząd 4 kart KPI (`repeat(4,1fr)`, gap 12px), następnie tabela w karcie z obramowaniem
  (`1px #ece9f4`, radius 14px).
- **Komponenty**:
  - **Nagłówek**: „Aplikacje" (Space Grotesk 22px/700, `#201d33`) + podtytuł „Zarządzaj limitami
    prędkości i godzinami działania" (13px, `#7d7898`). Przycisk „+ Dodaj aplikację": tło
    `oklch(0.55 0.18 275)`, biały tekst 14px/700, padding 11×18px, radius 11px.
  - **Karty KPI** (border `1px #ece9f4`, radius 13px, padding 14×16px): etykieta 12px/600 `#7d7898`;
    wartość Space Grotesk 22px/700. Dane: Aplikacje **5**; Teraz **38,8 Mb/s**; Ograniczane **2**
    (kolor akcentu); Dziś **14,2 GB**.
  - **Tabela**: kolumny `2.2fr 1.1fr 1.2fr 1.6fr 1fr 0.5fr` = Aplikacja / Limit / Godziny / Teraz /
    Status / (menu ⋯). Nagłówek: tło `#f7f5fb`, 12px/700 uppercase `#7d7898`, letter-spacing .04em.
    Wiersze rozdzielone `1px #f1eef7`, padding 14×18px.
    - Kolumna „Aplikacja": ikona 34px radius 9px (kolorowy placeholder + 2-literowy skrót) + nazwa
      600 `#201d33`.
    - Kolumna „Teraz": mała liczba (Space Grotesk 12.5px) + pasek postępu 6px (tło `#eeebf6`,
      wypełnienie kolorem aplikacji, szerokość = zużycie/limit).
    - Status (12px/700): „● Aktywna" `oklch(0.5 0.13 150)` (zielony); „● Ograniczana"
      `oklch(0.55 0.16 30)` (koralowy, wiersz podświetlony tłem `oklch(0.55 0.18 275 / 0.05)`,
      pasek animowany pulsem); „● Uśpiona" `#8b86a3` (cały wiersz opacity 0.6).
  - **Dane wierszy** (przykładowe): Przeglądarka 25 Mb/s / cała doba / 12,3 / Aktywna;
    Streaming wideo 15 Mb/s / 18–23 / 15,0 / Ograniczana; Gra online 10 Mb/s / 16–22 / 8,1 / Aktywna;
    Klient chmury 5 Mb/s / 09–17 / 3,4 / Aktywna; Aktualizacje systemu 2 Mb/s / 02–06 / 0,0 / Uśpiona.

### 2. Dodaj aplikację (okno dialogowe) — `2b`
- **Purpose**: wybór aplikacji i ustawienie limitu prędkości + godzin.
- **Layout**: modal (radius 16px, biały) na przyciemnionym tle `oklch(0.28 0.06 275 / 0.14)`.
  Nagłówek / treść / stopka rozdzielone `1px #f1eef7`.
- **Komponenty**:
  - **Nagłówek**: „Nowy limit aplikacji" (Space Grotesk 19px/700) + przycisk zamknięcia ✕ (28px,
    tło `#f2f0f9`).
  - **Pole „Aplikacja"**: wybrana pozycja w ramce z akcentem `oklch(0.55 0.18 275 / 0.4)`, tłem
    `oklch(0.55 0.18 275 / 0.05)`; ikona + nazwa + „zmień ▾". Pod spodem hint: „Wykryto 12
    uruchomionych aplikacji · możesz też wskazać plik .exe".
  - **„Limit prędkości pobierania"**: suwak (track 8px `#eeebf6`, wypełnienie i uchwyt 20px w
    akcencie) + pole liczbowe „10 Mb/s". Skala od „0,5 Mb/s" do „bez limitu".
  - **„Godziny działania"**: dwa radio-kafle — „Cała doba" (nieaktywny) i „Wybrane godziny"
    (aktywny, ramka `oklch(0.55 0.18 275)`); pod nimi dwa pola czasu „16:00 → 22:00" + hint
    „poza tymi godzinami aplikacja jest wstrzymywana".
  - **Stopka**: „Anuluj" (ramka `#e0dcec`, tekst `#5b5675`) + „Zapisz limit" (tło akcentu, biały).

### 3. Monitor na żywo — `2c`
- **Purpose**: potwierdzenie, że limity są egzekwowane w czasie rzeczywistym.
- **Layout**: sidebar + treść. W sidebarze na dole wskaźnik „● Monitorowanie aktywne" (zielony,
  pulsujący). Treść: nagłówek + przełącznik zakresu (60 s / 15 min / 1 godz), duża karta wykresu,
  dwie karty podsumowania (`1fr 1fr`).
- **Komponenty**:
  - **Wykres**: SVG liniowy 900×220, siatka pozioma `#f1eef7`. Linie: Przeglądarka
    `oklch(0.7 0.13 250)`, Streaming `oklch(0.68 0.16 30)`, Gra `oklch(0.62 0.16 300)`, każda 2.5px.
    **Linia limitu** Streamingu: pozioma, przerywana `oklch(0.68 0.16 30)` (dash 6 5) — krzywa
    Streamingu „przykleja się" do niej, co wizualnie dowodzi egzekwowania limitu. Legenda pod
    wykresem z wartościami na żywo.
  - **Karta alertu** (ramka/tło koralowe): „Streaming wideo — limit egzekwowany", opis: utrzymywany
    na 15,0 Mb/s; bez limitu ~42 Mb/s. Ikona ⚑.
  - **Karta OK** (zielona ikona ✓): „Pozostałe aplikacje w normie", „Zaoszczędzono 27 Mb/s".

### 4. Historia — `2d`
- **Purpose**: statystyki transferu i egzekwowania limitów.
- **Layout**: sidebar + treść. Nagłówek + przełącznik Dzień/Tydzień/Miesiąc; rząd 3 kart KPI;
  karta z wykresem słupkowym skumulowanym.
- **Komponenty**:
  - **KPI**: Pobrano łącznie **96,4 GB**; Zaoszczędzono limitami **31,8 GB** (zielony);
    Zdarzeń ograniczenia **214** (akcent).
  - **Wykres słupkowy skumulowany** (Pn–Nd, wysokość 150px): każdy słupek podzielony na segmenty
    Streaming / Przeglądarka / Gra (te same kolory co wyżej), radius 6px u góry. Legenda pod spodem.

---

## Interactions & Behavior
- **„+ Dodaj aplikację"** → otwiera modal `2b`. „Zapisz limit" → zapis reguły i powrót do listy
  `2a` z nowym wierszem. „Anuluj"/✕ → zamknięcie bez zmian.
- **Nawigacja boczna** przełącza między `2a`/`2c`/`2d`/Ustawienia.
- **Menu ⋯** w wierszu tabeli: edycja / włącz-wyłącz / usuń limit.
- **Suwak prędkości** i **pole liczbowe** są zsynchronizowane; „Wybrane godziny" odsłania pola czasu.
- **Monitor** aktualizuje się co 1 s (przesuwające okno danych). Pasek „Ograniczana" pulsuje
  (`barPulse`, 1.8 s ease-in-out, opacity 1↔0.72).
- **Statusy** wynikają ze stanu runtime: w godzinach + poniżej limitu = Aktywna; przy limicie =
  Ograniczana; poza godzinami = Uśpiona.
- **Przełączniki zakresu / okresu** zmieniają dane wykresów.

## State Management
Stan aplikacji (frontend), zsynchronizowany z usługą backendową:
- `apps: [{ id, name, exeMatch, iconColor, initials, limitMbps, schedule: {mode, from, to}, enabled }]`
- `liveUsage: { [appId]: { mbps, throttled: bool } }` — strumień z usługi (co 1 s).
- `linkCapacityMbps`, `totalNowMbps`, `throttledCount`, `todayGb`.
- `history: { range, series: { [appId]: number[] } }`.
- `ui: { activeNav, addModalOpen, monitorRange, historyPeriod }`.
Przejścia: zapis modala → dodaje/aktualizuje `apps` i wysyła regułę do usługi; zdarzenia z usługi →
aktualizują `liveUsage` i statusy.

## Design Tokens
- **Kolory**
  - Tło treści / karty: `#ffffff`; obramowania kart `#ece9f4`, linie wierszy `#f1eef7`, nagłówek
    tabeli `#f7f5fb`, tory pasków `#eeebf6`.
  - Tekst: podstawowy `#201d33`; drugorzędny `#7d7898` / `#5b5675`; wyciszony `#a29dbc`.
  - Akcent (indygo): `oklch(0.55 0.18 275)`; jasny wariant `oklch(0.7 0.16 275)`; sidebar
    `oklch(0.28 0.06 275)`.
  - Kolory aplikacji: przeglądarka `oklch(0.7 0.13 250)`, streaming/koralowy alert
    `oklch(0.68 0.16 30)`, gra `oklch(0.62 0.16 300)`, chmura `oklch(0.62 0.02 260)`, uśpiona `#b8b3c9`.
  - Status zielony (OK/Aktywna): `oklch(0.5–0.6 0.13 150)`.
  - Kropki „traffic lights": `#ff5f57` / `#febc2e` / `#28c840`.
- **Typografia**: nagłówki i liczby **Space Grotesk** (400–700); tekst UI **Manrope** (400–800).
  Skala: 30 / 22 / 19 / 16 / 14 / 13 / 12.5 / 12 / 11 px.
- **Radius**: okno 18px; karty 13–16px; przyciski/pola 9–11px; pigułki/paski 999px.
- **Cień okna**: `0 30px 60px -24px rgba(30,25,60,0.4), 0 0 0 1px rgba(0,0,0,0.04)`.
- **Odstępy**: padding treści 22–26px; gap kart 12px; padding wiersza 14×18px.
- **Animacja**: `@keyframes barPulse { 0%,100%{opacity:1} 50%{opacity:.72} }`.

## Assets
- **Brak plików graficznych.** Ikony aplikacji to placeholdery — kolorowe kwadraty z 2-literowym
  skrótem. W produkcji zastąpić ikonami aplikacji pobieranymi z systemu (ikona z pliku .exe) lub
  neutralnymi placeholderami. Nazwy aplikacji (Przeglądarka, Streaming itd.) są przykładowe.
- Fonty: Google Fonts — Space Grotesk, Manrope. W aplikacji desktopowej dołączyć lokalnie.
- Drobne glify (＋, ✕, ⋯, ▾, ●, ⚑, ✓, ↕, 🕑) — zastąpić zestawem ikon projektu (np. Lucide).

## Files
- `Przepustnica.dc.html` — pełny prototyp wszystkich ekranów (tura 2 = wersja do wdrożenia).
- `PLAN_WDROZENIA_WINDOWS.md` — techniczny plan wdrożenia na Windows (architektura, etapy, ryzyka).
