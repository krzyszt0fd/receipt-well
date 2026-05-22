---
project: ReceiptVault
context_type: greenfield
product_type: web-app
target_scale:
  users: small
timeline_budget:
  mvp_weeks: 3
  hard_deadline: null
  after_hours_only: true
updated: 2026-05-19
checkpoint:
  current_phase: 8
  phases_completed: [1, 2, 3, 4, 5, 6, 7]
  frs_drafted: 6
  quality_check_status: accepted
---

## Vision & Problem Statement

**Ból:** Paragony papierowe giną lub bledną — użytkownik traci dowód zakupu dokładnie wtedy, gdy najbardziej go potrzebuje (zwrot, reklamacja, gwarancja).

**Osoba:** Indywidualny konsument aktywnie kupujący sprzęt (elektronika, sport, RTV/AGD) — dla siebie, nie do celów firmowych.

**Moment:** Kilka tygodni lub miesięcy po zakupie, gdy próbuje skorzystać z gwarancji lub złożyć reklamację — i nie może znaleźć paragonu.

**Koszt dziś:** Traci prawo do zwrotu / gwarancji lub spędza czas przeszukując szuflady, portfele i torby.

**Insight:** Istniejące narzędzia wymagają ręcznego wprowadzania danych. AI umożliwia automatyczną ekstrakcję (sklep, data, cena, produkt) i tagowanie — zero ręcznej pracy przy dodaniu paragonu.

**Wizja:** Aplikacja, w której wystarczy sfotografować paragon — AI przetwarza go w ciągu sekund, użytkownik dostaje przeszukiwalną bibliotekę swoich zakupów.

## User & Persona

**Rola:** Konsument indywidualny — jeden użytkownik, prywatna kolekcja paragonów.

**Kontekst używania:** Sklep (zaraz po zakupie, by od razu zarchiwizować) lub dom (by przeskanować zaległe paragony). Urządzenie mobilne jako naturalny pierwszy wybór — aparat zawsze pod ręką.

**Cel sesji:** Dodać paragon w < 30 sekund i mieć pewność, że znajdzie go w razie potrzeby bez ręcznego opisywania.

---

## Access Control

- Model: konto użytkownika z logowaniem przez zewnętrznego dostawcę OAuth/OIDC.
- Role: płaski model — jeden typ użytkownika, brak ról. Każdy użytkownik widzi wyłącznie swoje paragony.
- Sesja: dostęp z wielu urządzeń przez przeglądarkę (RWD) — dane synchronizowane przez konto.
- Wybór konkretnego dostawcy: decyzja stackowa → patrz `## Forward: tech-stack`.

---

## Forward: tech-stack

- Auth provider: zewnętrzny dostawca OAuth/OIDC (decyzja stackowa — do ustalenia w tech-stack-selector)
- Azure budget: ~100 EUR/miesiąc

---

## Success Criteria

### Primary

Użytkownik loguje się, wgrywa zdjęcie paragonu, AI automatycznie tworzy tagi i wyciąga dane, a użytkownik odnajduje ten paragon przez wyszukiwanie po tagu (np. "rower").

MVP flow:
1. Użytkownik otwiera aplikację w przeglądarce → loguje się (OAuth/OIDC)
2. Dodaje zdjęcie paragonu (upload lub aparat mobilny przez przeglądarkę)
3. AI przetwarza zdjęcie → ekstrakcja: sklep, data, cena, produkt + tagi
4. Użytkownik wpisuje tag w wyszukiwarkę → widzi pasujące paragony

`timeline_budget.mvp_weeks: 3`

### Secondary

Przy wynikach wyszukiwania widoczna miniatura zdjęcia paragonu — użytkownik od razu ocenia czy to właściwy paragon.

### Guardrails

- Oryginalne zdjęcie paragonu nigdy nie jest tracone po wgraniu, niezależnie od wyniku przetwarzania AI.
- Paragony użytkownika są w pełni izolowane — żaden inny użytkownik nie ma do nich dostępu.
- Aplikacja działa na telefonie przez przeglądarkę (RWD) bez konieczności instalowania apki.

---

## Functional Requirements

### Uwierzytelnianie

- FR-001: Użytkownik może zalogować się do aplikacji przez zewnętrznego dostawcę OAuth/OIDC. Priority: must-have
  > Socrates: Brak kontrargumentu — logowanie jest niezbędne dla guardrail'a prywatności (izolacja danych między kontami). Stoi bez zmian.

### Zarządzanie paragonami

- FR-002: Użytkownik może wgrać zdjęcie paragonu (upload pliku lub aparat przez przeglądarkę). Priority: must-have
  > Socrates: Jakość zdjęcia może być zbyt niska dla ekstrakcji AI — brak wskazówek UX. Wniosek: aplikacja powinna podpowiadać użytkownikowi jak zrobić dobre zdjęcie (oświetlenie, kadr). Zachowano FR; wskazówki fotografowania trafią do wymagań UX.

- FR-003: System automatycznie wyciąga z paragonu nazwę sklepu, datę zakupu, cenę i nazwę produktu. Priority: must-have
  > Socrates: AI będzie się mylić na zbladniętych paragonach — dokładnie tych, które trafiają do aplikacji. Wniosek: paragon musi być zapisany (zdjęcie + dane cząstkowe) nawet gdy ekstrakcja jest niepełna. Guardrail "zdjęcie nigdy nie ginie" już to pokrywa. FR stoi; fallback przy niepełnej ekstrakcji do logiki biznesowej.

- FR-004: System automatycznie generuje tagi opisujące produkt z paragonu. Priority: must-have
  > Socrates: AI może generować niespójne tagi ("rower" vs "bicycle" vs "bike") — wyszukiwanie będzie zawodzić. Wniosek: normalizacja języka tagów (PL) to warunek użyteczności FR-005. Kwestia trafia do Business Logic jako reguła domenowa.

### Wyszukiwanie

- FR-005: Użytkownik może wyszukiwać swoje paragony po tagu. Priority: must-have
  > Socrates: Jakość wyszukiwania jest sprzężona z jakością tagów — niespójne tagi z FR-004 czynią search bezwartościowym. Wniosek: FR-005 i FR-004 stoją lub padają razem. Normalizacja tagów jest wymaganiem blokującym dla obu.

- FR-006: Użytkownik widzi miniaturę zdjęcia paragonu przy wynikach wyszukiwania. Priority: nice-to-have
  > Socrates: Miniatura jest szczególnie cenna jako fallback gdy AI wyciągnęła dane cząstkowe — użytkownik może zidentyfikować paragon wzrokowo. Brak kontrargumentu. FR stoi.

---

## User Stories

### US-01: Dodanie i odnalezienie paragonu

**Given** użytkownik jest zalogowany i ma zdjęcie paragonu za rower,
**When** wgrywa zdjęcie do aplikacji i czeka na przetworzenie przez AI,
**Then** system wyciąga dane (sklep, data, cena, produkt) i tworzy tagi (np. "rower"),
  i użytkownik może wpisać "rower" w wyszukiwarkę i znaleźć ten paragon.

---

## Business Logic

Aplikacja swobodnie generuje tagi z treści paragonu przez AI, a następnie normalizuje je do języka polskiego — niezależnie od języka paragonu lub surowego outputu modelu.

Szczegóły:
- AI może generować dowolną liczbę tagów opisujących produkt (kategoria, marka, model, cechy).
- Wszystkie tagi są prezentowane użytkownikowi w języku polskim.
- Paragon jest zapisywany natychmiast po wgraniu zdjęcia (guardrail: zdjęcie nigdy nie ginie). Ekstrakcja danych i tagowanie odbywają się asynchronicznie w tle — użytkownik nie musi czekać na wynik.
- Jeśli ekstrakcja jest niepełna (zbladły paragon, zła jakość zdjęcia), paragon pozostaje zapisany ze zdjęciem i cząstkowymi danymi. Tagi i pola mogą być puste — wyszukiwanie działa po tym co udało się wyciągnąć.

---

## Non-Functional Requirements

- Aplikacja działa poprawnie na aktualnych wersjach Chrome, Safari i Firefox (RWD — desktop i mobile).
- Aplikacja przyjmuje zdjęcia do 10 MB w formatach JPEG, PNG i HEIC.
- Przetwarzanie AI (ekstrakcja + tagowanie) jest asynchroniczne — użytkownik nie oczekuje na wynik przy wgrywaniu; paragony pojawiają się w wynikach wyszukiwania gdy przetwarzanie dobiegnie końca.
- Widoczny status przetwarzania paragonu (np. "w trakcie" / "gotowy") — użytkownik wie, czy może już szukać po nowym paragonie.

---

## Non-Goals

- Bez współdzielenia paragonów: brak funkcji rodziny, workspace'u ani udostępniania — każde konto widzi tylko swoje paragony.
- Bez ręcznego tworzenia wpisu: MVP wymaga zdjęcia — nie ma opcji dodania paragonu przez wpisanie danych bez fotografii.
- Bez eksportu danych: aplikacja nie generuje raportów, PDF-ów ani eksportów CSV/Excel do księgowości.
- Bez powiadomień: MVP nie śledzi dat gwarancji ani nie wysyła alertów push/e-mail o zbliżającym się terminie.

---

## Open Questions

<!-- brak otwartych pytań po cross-check -->

## Quality cross-check

Wykonano 2026-05-19. Wszystkie 5 elementów (greenfield) obecnych. Status: accepted.
