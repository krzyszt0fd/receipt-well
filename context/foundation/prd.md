---
project: ReceiptVault
version: 1
status: draft
created: 2026-05-19
context_type: greenfield
product_type: web-app
target_scale:
  users: small
  qps: low
  data_volume: small
timeline_budget:
  mvp_weeks: 3
  hard_deadline: null
  after_hours_only: true
---

## Vision & Problem Statement

Konsument indywidualny kupujący sprzęt (elektronika, sport, RTV/AGD) gubi lub traci czytelność paragonów papierowych. W momencie próby reklamacji lub zwrotu — tygodnie lub miesiące po zakupie — nie ma dowodu zakupu: traci prawo do gwarancji lub spędza czas przeszukując szuflady, portfele i torby.

Istniejące narzędzia wymagają ręcznego wprowadzania danych, co powoduje, że rzadko są używane w momencie zakupu. Automatyczna analiza zdjęcia paragonu i generowanie polskojęzycznych tagów opisujących produkt pozwala użytkownikowi sfotografować paragon i natychmiast otrzymać przeszukiwalny wpis — bez żadnej ręcznej pracy.

## User & Persona

Konsument indywidualny aktywnie kupujący sprzęt — dla siebie, nie do celów firmowych. Używa aplikacji bezpośrednio po zakupie w sklepie lub w domu przy skanowaniu zaległych paragonów. Urządzenie mobilne jest naturalnym pierwszym wyborem — aparat zawsze pod ręką. Celem każdej sesji jest dodanie paragonu w mniej niż 30 sekund i pewność, że odnajdzie go w razie potrzeby bez ręcznego opisywania.

## Success Criteria

### Primary

Użytkownik loguje się, wgrywa zdjęcie paragonu, aplikacja wyciąga dane i tworzy tagi, a użytkownik odnajduje paragon przez wyszukiwanie po tagu (np. "rower"). Cały flow — od logowania do znalezienia wgranego paragonu po tagu — jest możliwy bez żadnego ręcznego opisywania.

### Secondary

Przy wynikach wyszukiwania widoczna jest miniatura zdjęcia paragonu, umożliwiająca wzrokową identyfikację właściwego paragonu bez otwierania szczegółów.

### Guardrails

- Oryginalne zdjęcie paragonu nigdy nie jest tracone po wgraniu, niezależnie od wyniku przetwarzania.
- Paragony użytkownika są w pełni izolowane — żaden inny użytkownik nie ma do nich dostępu.
- Aplikacja działa na telefonie przez przeglądarkę bez konieczności instalowania aplikacji (RWD).

## User Stories

### US-01: Dodanie i odnalezienie paragonu

- **Given** użytkownik jest zalogowany i wgrywa zdjęcie paragonu za rower
- **When** aplikacja przetwarza zdjęcie i tworzy wpis z tagami
- **Then** użytkownik może wpisać "rower" w wyszukiwarkę i odnaleźć ten paragon

#### Acceptance Criteria

- Paragon zostaje zapisany w aplikacji natychmiast po wgraniu zdjęcia.
- Aplikacja wskazuje stan przetwarzania wpisu (oczekujący / gotowy do wyszukiwania).
- Po zakończeniu przetwarzania wyszukanie po tagu "rower" zwraca ten paragon.
- Jeśli ekstrakcja danych jest niepełna, paragon nadal widnieje w systemie ze zdjęciem i dostępnymi danymi cząstkowymi.

## Functional Requirements

### Uwierzytelnianie

- FR-001: Użytkownik może zalogować się do aplikacji przy użyciu konta zewnętrznego dostawcy tożsamości. Priority: must-have
  > Socrates: Brak kontrargumentu — logowanie jest niezbędne dla guardrail'a prywatności (izolacja danych między kontami). Stoi bez zmian.

### Zarządzanie paragonami

- FR-002: Użytkownik może wgrać zdjęcie paragonu (upload pliku lub aparat przez przeglądarkę). Priority: must-have
  > Socrates: Jakość zdjęcia może być zbyt niska dla ekstrakcji — brak wskazówek UX. Wniosek: aplikacja powinna podpowiadać użytkownikowi jak zrobić dobre zdjęcie (oświetlenie, kadr). Zachowano FR; wskazówki fotografowania trafią do wymagań UX.

- FR-003: System automatycznie wyciąga z paragonu nazwę sklepu, datę zakupu, cenę i nazwę produktu. Priority: must-have
  > Socrates: Automatyczna analiza może dawać błędne wyniki na zbladniętych paragonach — dokładnie tych, które trafiają do aplikacji. Wniosek: paragon musi być zapisany ze zdjęciem i danymi cząstkowymi nawet gdy ekstrakcja jest niepełna. Guardrail "zdjęcie nigdy nie ginie" już to pokrywa. FR stoi.

- FR-004: System automatycznie generuje tagi opisujące produkt z paragonu. Priority: must-have
  > Socrates: Generowane tagi mogą być niespójne językowo ("rower" vs "bicycle" vs "bike") — wyszukiwanie będzie zawodzić. Wniosek: normalizacja języka tagów (PL) to warunek użyteczności FR-005. Kwestia trafia do Business Logic jako reguła domenowa.

### Wyszukiwanie

- FR-005: Użytkownik może wyszukiwać swoje paragony po tagu. Priority: must-have
  > Socrates: Jakość wyszukiwania jest sprzężona z jakością tagów — niespójne tagi z FR-004 czynią search bezwartościowym. Wniosek: FR-005 i FR-004 stoją lub padają razem. Normalizacja tagów jest wymaganiem blokującym dla obu.

- FR-006: Użytkownik widzi miniaturę zdjęcia paragonu przy wynikach wyszukiwania. Priority: nice-to-have
  > Socrates: Miniatura jest szczególnie cenna jako fallback gdy ekstrakcja danych była cząstkowa — użytkownik może zidentyfikować paragon wzrokowo. Brak kontrargumentu. FR stoi.

## Non-Functional Requirements

- Aplikacja działa poprawnie na aktualnych wersjach Chrome, Safari i Firefox (desktop i mobile).
- Aplikacja przyjmuje zdjęcia do 10 MB w formatach JPEG, PNG i HEIC.
- Wgranie zdjęcia paragonu nie blokuje użytkownika — przetwarzanie jest asynchroniczne, a wyniki wyszukiwania dla nowego paragonu stają się dostępne po jego zakończeniu.
- Użytkownik może w każdej chwili sprawdzić stan przetwarzania każdego wgranego paragonu — aplikacja rozróżnia wpisy oczekujące na przetwarzanie od gotowych do wyszukiwania.

## Business Logic

Aplikacja generuje tagi opisujące produkt z treści paragonu i normalizuje je do języka polskiego — niezależnie od języka wejściowego paragonu.

Szczegóły reguły:
- Dowolna liczba tagów może opisywać produkt (kategoria, marka, model, cechy) — zakres generowanych tagów nie jest z góry ograniczony.
- Wszystkie tagi widoczne dla użytkownika są w języku polskim.
- Paragon jest zapisywany natychmiast po wgraniu zdjęcia. Ekstrakcja danych i tagowanie odbywają się asynchronicznie — użytkownik nie musi czekać na wynik.
- Jeśli ekstrakcja jest niepełna (zbladły paragon, zła jakość zdjęcia), paragon pozostaje zapisany ze zdjęciem i dostępnymi danymi cząstkowymi. Tagi i pola mogą być puste — wyszukiwanie działa po tym, co udało się wyciągnąć.

## Access Control

Użytkownik loguje się przy użyciu konta zewnętrznego dostawcy tożsamości — aplikacja nie przechowuje hasła. Jeden typ użytkownika, brak ról. Po zalogowaniu użytkownik widzi wyłącznie swoje paragony; dostęp z wielu urządzeń przez przeglądarkę jest obsługiwany przez synchronizację danych konta.

## Non-Goals

- Bez współdzielenia paragonów: brak funkcji rodziny, workspace'u ani udostępniania — każde konto widzi tylko swoje paragony.
- Bez ręcznego tworzenia wpisu: MVP wymaga zdjęcia — nie ma opcji dodania paragonu przez wpisanie danych bez fotografii.
- Bez eksportu danych: aplikacja nie generuje raportów, PDF-ów ani eksportów CSV/Excel do celów księgowych.
- Bez powiadomień: MVP nie śledzi dat gwarancji ani nie wysyła alertów o zbliżającym się terminie.

## Open Questions

Brak otwartych pytań — wszystkie elementy zaadresowane w trakcie sesji kształtowania produktu (`/10x-shape`, 2026-05-19).
