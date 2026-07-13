---
project: ReceiptWell
version: 1
status: draft
created: 2026-05-31
updated: 2026-07-13
prd_version: 1
main_goal: market-feedback
top_blocker: decisions
---

# Roadmap: ReceiptWell

> Derived from `context/foundation/prd.md` (v1) + auto-researched codebase baseline.
> Edit-in-place; archive when superseded.
> Slices below are listed in dependency order. The "At a glance" table is the index.

## Vision recap

Indywidualny konsument gubi lub traci czytelność paragonów papierowych — w momencie reklamacji lub zwrotu nie ma dowodu zakupu. ReceiptWell pozwala sfotografować paragon i natychmiast otrzymać przeszukiwalny wpis bez żadnej ręcznej pracy: AI wyciąga dane i generuje tagi opisujące produkt, znormalizowane do języka polskiego. Rdzeń hipotezy produktowej: czy automatyczna ekstrakcja i tagowanie są wystarczająco dobre, żeby użytkownik mógł odnaleźć paragon po słowie kluczowym tygodnie po zakupie — nawet jeśli zdjęcie było złej jakości?

## North star

**S-04: Użytkownik może wyszukać swój paragon po tagu** — to pierwsza historyjka (US-01), której ukończenie udowadnia, że produkt działa: jeśli AI wyciągnie tagi z realnego paragonu i użytkownik odnajdzie go wpisując "rower", hipoteza produktowa jest potwierdzona.

> Gwiazda przewodnia (north star) — to najkrótszy pełny przepływ od wgrania paragonu do znalezienia go po tagu; ustawiona jak najwcześniej w sekwencji, bo wszystko inne ma wartość tylko jeśli ten kawałek zadziała.

## At a glance

| ID   | Change ID                     | Outcome (user can …)                                                            | Prerequisites | PRD refs                      | Status   |
| ---- | ----------------------------- | ------------------------------------------------------------------------------- | ------------- | ----------------------------- | -------- |
| F-01 | complete-auth-gate            | (foundation) endpointy chronione `[Authorize]`, Angular route guard podłączony  | —             | FR-001, Access Control        | done     |
| S-01 | receipt-upload-confirm        | wgrać zdjęcie paragonu i zobaczyć natychmiastowe potwierdzenie (nazwa, rozmiar) | F-01          | FR-002                        | done     |
| S-02 | receipts-list-with-status     | zobaczyć listę swoich paragonów ze statusem przetwarzania                       | F-01, S-01    | US-01 (AC), NFR status        | done     |
| S-03 | ai-extraction-and-enrichment  | zobaczyć w liście wyciągnięte metadane: sklep, datę, tagi                       | F-01, S-01    | FR-003, FR-004, NFR async     | done     |
| S-04 | tag-search                    | wpisać tag i znaleźć swój paragon w wynikach wyszukiwania                       | F-01, S-03    | FR-005, US-01                 | done     |
| S-05 | receipt-thumbnail-in-search   | zobaczyć miniaturę zdjęcia paragonu przy wynikach wyszukiwania                  | S-04          | FR-006                        | proposed |

## Streams

Navigation aid — groups items that share a Prerequisites chain. Canonical ordering still lives in the dependency graph below; this table is the proposed reading order across parallel tracks.

| Stream | Theme                  | Chain                               | Note                                                                  |
| ------ | ---------------------- | ----------------------------------- | --------------------------------------------------------------------- |
| A      | UX foundation          | `F-01` → `S-01` → `S-02`           | Auth gate + upload flow + list; każdy krok daje weryfikowalny UI.     |
| B      | AI pipeline & search   | `S-03` → `S-04`                     | Rozgałęzia się od `S-01`; S-03 równolegle z S-02; S-04 = gwiazda przewodnia. |
| C      | Miniatura (nice-to-have) | `S-05`                            | Standalone po S-04; niezależne od S-02 i S-03.                        |

## Baseline

Co jest już w kodzie na 2026-05-31 (auto-researched + potwierdzone przez użytkownika).
Foundations poniżej zakładają, że poniższe warstwy są obecne i ich nie przebudowują.

- **Frontend:** partial — Angular 21.2.0, router skonfigurowany (puste routes), brak komponentów domenowych (`src/frontend/src/app/app.routes.ts`)
- **Backend / API:** partial — ASP.NET Core 9.0 minimal API, jeden endpoint template `/weatherforecast` (`src/backend/Program.cs:52`), brak encji ani serwisów domenowych
- **Data:** absent — Azure Blob SDK tylko dla data protection key ring; brak modeli Receipt/Tag, brak SDK dla CosmosDB ani Azure Search
- **Auth:** partial — Entra External ID JWT bearer wired (`src/backend/Program.cs:27-32`), MSAL skonfigurowany w frontend (`app.config.ts`), ale brak `[Authorize]` na endpointach i brak route guards w Angular
- **Deploy / infra:** present — Terraform IaC (10 plików `.tf` w `/infra/`), GitHub Actions CI/CD dla backend (Azure App Service) i frontend (Azure Static Web Apps)
- **Observability:** absent — tylko domyślny logging ASP.NET Core; brak Application Insights, brak health-check endpointu

## Foundations

### F-01: Brama autoryzacji

- **Outcome:** (foundation) wszystkie endpointy aplikacji chronione atrybutem `[Authorize]`; Angular routes wymagające logowania przekierowują do Entra External ID zamiast ładować się bez sesji.
- **Change ID:** complete-auth-gate
- **PRD refs:** FR-001, sekcja Access Control
- **Unlocks:** S-01, S-02, S-03, S-04 (guardrail prywatności: każdy endpoint musi weryfikować tożsamość i izolować dane per konto)
- **Prerequisites:** —
- **Parallel with:** —
- **Blockers:** —
- **Unknowns:** —
- **Risk:** Bez auth gate każdy endpoint jest publiczny — błąd konfiguracji narusza guardrail prywatności (izolacja paragonów per konto). Sequenced jako pierwsze, bo downstream slices nie mogą być bezpiecznie wdrożone bez tego zabezpieczenia.
- **Status:** done

## Slices

### S-01: Potwierdzenie wgrania

- **Outcome:** użytkownik może wgrać zdjęcie paragonu i zobaczyć natychmiastowe potwierdzenie z nazwą pliku i rozmiarem
- **Change ID:** receipt-upload-confirm
- **PRD refs:** FR-002, NFR (10 MB / JPEG / PNG / HEIC), guardrail "zdjęcie nigdy nie ginie"
- **Prerequisites:** F-01
- **Parallel with:** —
- **Blockers:** —
- **Unknowns:** —
- **Risk:** Slice wprowadza kontrakt danych: Azure Blob dla zdjęć + encja Receipt (ID, blob URL, status: pending, timestamp). Kontrakt ten jest konsumowany przez S-02 (lista) i S-03 (AI pipeline) — zmiana modelu Receipt po S-01 kosztuje podwójnie.
- **Status:** done

### S-02: Lista paragonów ze statusem

- **Outcome:** użytkownik może zobaczyć listę swoich wgranych paragonów ze statusem przetwarzania ("w trakcie" / "gotowy")
- **Change ID:** receipts-list-with-status
- **PRD refs:** US-01 (AC: "Aplikacja wskazuje stan przetwarzania wpisu"), NFR (widoczny status przetwarzania)
- **Prerequisites:** F-01, S-01
- **Parallel with:** S-03
- **Blockers:** —
- **Unknowns:** —
- **Risk:** Do momentu ukończenia S-03 wszystkie paragony na liście będą w statusie "w trakcie" (AI pipeline jeszcze nie działa). To poprawny stan przejściowy — lista jest użyteczna jako potwierdzenie, że wgranie dotarło.
- **Status:** done

### S-03: Wzbogacenie paragonów przez AI

- **Outcome:** użytkownik widzi na liście paragonów wyciągnięte metadane: nazwę sklepu, datę wystawienia i tagi produktu
- **Change ID:** ai-extraction-and-enrichment
- **PRD refs:** FR-003, FR-004, NFR (asynchroniczne przetwarzanie), reguły Business Logic (normalizacja tagów do języka polskiego)
- **Prerequisites:** F-01, S-01
- **Parallel with:** S-02
- **Blockers:** —
- **Unknowns:**
  - Czy jakość ekstrakcji AI jest wystarczająca na zbladniętych i niskiej jakości paragonach, by tagi były użyteczne? — Owner: user (walidacja na realnych paragonach podczas implementacji). Block: no (PRD ma fallback: paragon pozostaje z danymi cząstkowymi i zdjęciem; ale jeśli ekstrakcja zawodzi zbyt często, hipoteza produktowa odpada).
- **Risk:** Technicznie najcięższy slice: Azure Functions (trigger blob) + AI SDK for .NET + normalizacja tagów do PL + zapis wyników z powrotem do Receipt. Zalecane: przetestować ekstrakcję na 5–10 realnych paragonach jak najwcześniej, zanim pipeline pójdzie na produkcję.
- **Status:** done

### S-04: Wyszukiwanie po tagu

- **Outcome:** użytkownik może wpisać tag (np. "rower") i znaleźć swój paragon w wynikach wyszukiwania
- **Change ID:** tag-search
- **PRD refs:** FR-005, US-01
- **Prerequisites:** F-01, S-03
- **Parallel with:** —
- **Blockers:** —
- **Unknowns:** —
- **Risk:** Slice wprowadza Azure Search index: indeksowanie paragonów po tagach + endpoint wyszukiwania + UI. Jakość wyników bezpośrednio zależy od jakości tagów z S-03 — jeśli S-03's Unknown potwierdzi słabą ekstrakcję, S-04 ujawni to użytkownikowi jako pierwsze.
- **Status:** done

### S-05: Miniatury paragonów w wynikach

- **Outcome:** użytkownik widzi miniaturę zdjęcia paragonu przy każdym wyniku wyszukiwania
- **Change ID:** receipt-thumbnail-in-search
- **PRD refs:** FR-006
- **Prerequisites:** S-04
- **Parallel with:** —
- **Blockers:** —
- **Unknowns:** —
- **Risk:** Nice-to-have z PRD (secondary success criterion). Zdjęcia są już w Azure Blob po S-01, więc miniatura to głównie kwestia serwowania URL i renderowania w UI. Niskie ryzyko technicznie.
- **Status:** proposed

## Backlog Handoff

| Roadmap ID | Change ID                    | Suggested issue title                                    | Ready for `/10x-plan` | Notes                                  |
| ---------- | ---------------------------- | -------------------------------------------------------- | --------------------- | -------------------------------------- |
| F-01       | complete-auth-gate           | Complete auth gate: `[Authorize]` + Angular route guard  | yes                   | Run `/10x-plan complete-auth-gate`     |
| S-01       | receipt-upload-confirm       | Receipt upload: immediate confirmation with file info    | no                    | Wymaga ukończenia F-01                 |
| S-02       | receipts-list-with-status    | Receipt list: display with processing status             | no                    | Wymaga S-01; może iść równolegle z S-03 |
| S-03       | ai-extraction-and-enrichment | AI pipeline: extraction + tag normalization + enrichment | no                    | Wymaga S-01; może iść równolegle z S-02 |
| S-04       | tag-search                   | Tag search: Azure Search index + search UI               | no                    | Wymaga S-03                            |
| S-05       | receipt-thumbnail-in-search  | Receipt thumbnails in search results                     | no                    | Wymaga S-04                            |

## Open Roadmap Questions

1. **Czy jakość ekstrakcji AI (Azure OpenAI lub Anthropic SDK for .NET) na typowych polskich paragonach jest wystarczająca, by wyszukiwanie po tagu było użyteczne?** — Owner: user. Block: roadmap-wide. Ta hipoteza jest rdzeniem produktu — jeśli ekstrakcja zawodzi zbyt często na realnych paragonach, cała wartość S-04 odpada. Zalecane: przetestować na kilku realnych przykładach podczas implementacji S-03, zanim pipeline trafi na produkcję.

## Parked

- **Współdzielenie paragonów** — Why parked: PRD §Non-Goals. Brak funkcji rodziny, workspace'u ani udostępniania.
- **Ręczne tworzenie wpisu** — Why parked: PRD §Non-Goals. MVP wymaga zdjęcia; brak opcji wpisania danych bez fotografii.
- **Eksport danych (PDF, CSV, raporty)** — Why parked: PRD §Non-Goals. Poza zakresem MVP.
- **Powiadomienia o terminach gwarancji** — Why parked: PRD §Non-Goals. MVP nie śledzi dat gwarancji ani nie wysyła alertów.

## Done

(Empty on first generation. `/10x-archive` appends an entry here — and flips that item's `Status` to `done` — when a change whose `Change ID` matches the item is archived.)
- **F-01: (foundation) wszystkie endpointy aplikacji chronione atrybutem `[Authorize]`; Angular routes wymagające logowania przekierowują do Entra External ID zamiast ładować się bez sesji.** — Archived 2026-07-13 → `context/archive/2026-06-02-complete-auth-gate/`. Lesson: —.
- **S-01: użytkownik może wgrać zdjęcie paragonu i zobaczyć natychmiastowe potwierdzenie z nazwą pliku i rozmiarem** — Archived 2026-07-13 → `context/archive/2026-06-07-receipt-upload-confirm/`. Lesson: —.
- **S-02: użytkownik może zobaczyć listę swoich wgranych paragonów ze statusem przetwarzania ("w trakcie" / "gotowy")** — Archived 2026-07-13 → `context/archive/2026-06-21-receipts-list-with-status/`. Lesson: —.
- **S-03: użytkownik widzi na liście paragonów wyciągnięte metadane: nazwę sklepu, datę wystawienia i tagi produktu** — Archived 2026-07-13 → `context/archive/2026-06-14-ai-extraction-and-enrichment/`. Lesson: —.
- **S-04: użytkownik może wpisać tag (np. "rower") i znaleźć swój paragon w wynikach wyszukiwania** — Archived 2026-07-13 → `context/archive/2026-06-24-tag-search/`. Lesson: —.
