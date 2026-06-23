import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Observable, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { ReceiptListComponent } from './list.component';
import { ReceiptService, ReceiptSummary } from '../receipt.service';

const baseReceipt: ReceiptSummary = {
  id: 'r1',
  fileName: 'receipt.jpg',
  fileSize: 12345,
  status: 'ready',
  uploadedAt: '2026-06-01T10:00:00Z',
  storeName: 'Corner Store',
  purchaseDate: '2026-06-01T00:00:00Z',
  tags: ['food', 'groceries']
};

const pendingReceipt: ReceiptSummary = {
  ...baseReceipt,
  id: 'r2',
  status: 'pending',
  uploadedAt: new Date().toISOString()
};

const stalePendingReceipt: ReceiptSummary = {
  ...baseReceipt,
  id: 'r3',
  status: 'pending',
  uploadedAt: '2020-01-01T00:00:00Z'
};

function createComponent(getReceipts: () => ReturnType<ReceiptService['getReceipts']>) {
  TestBed.configureTestingModule({
    imports: [ReceiptListComponent],
    providers: [
      provideNoopAnimations(),
      provideRouter([]),
      { provide: ReceiptService, useValue: { getReceipts } }
    ]
  });

  const fixture = TestBed.createComponent(ReceiptListComponent);
  fixture.detectChanges();
  return fixture.componentInstance;
}

describe('ReceiptListComponent', () => {
  it('renders the populated state on successful fetch', () => {
    const component = createComponent(() => of([baseReceipt]));

    expect(component.loading()).toBe(false);
    expect(component.error()).toBeNull();
    expect(component.receipts()).toEqual([baseReceipt]);
  });

  it('renders the empty state for zero receipts', () => {
    const component = createComponent(() => of([]));

    expect(component.loading()).toBe(false);
    expect(component.error()).toBeNull();
    expect(component.receipts()).toEqual([]);
  });

  it('renders the error state when the fetch errors', () => {
    const component = createComponent(() => throwError(() => new Error('network error')));

    expect(component.loading()).toBe(false);
    expect(component.error()).toBeTruthy();
    expect(component.receipts()).toEqual([]);
  });

  it('caps visible tags at 5 and reports the correct overflow count', () => {
    const component = createComponent(() => of([]));
    const tags = ['a', 'b', 'c', 'd', 'e', 'f', 'g'];

    expect(component.visibleTags(tags)).toEqual(['a', 'b', 'c', 'd', 'e']);
    expect(component.extraTagCount(tags)).toBe(2);
  });

  it('reports zero overflow when tags are within the cap', () => {
    const component = createComponent(() => of([]));

    expect(component.extraTagCount(['a', 'b'])).toBe(0);
  });
});

describe('ReceiptListComponent polling (Phase 3)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('polls while a receipt is pending and stops once settled', () => {
    let callCount = 0;
    const getReceipts = (): Observable<ReceiptSummary[]> => {
      callCount++;
      return callCount === 1 ? of([pendingReceipt]) : of([{ ...pendingReceipt, status: 'ready' as const }]);
    };
    const component = createComponent(getReceipts);

    expect(callCount).toBe(1);
    expect(component.hasPending()).toBe(true);

    vi.advanceTimersByTime(5000);
    expect(callCount).toBe(2);
    expect(component.hasPending()).toBe(false);

    vi.advanceTimersByTime(5000);
    expect(callCount).toBe(2);
  });

  it('does not surface an error and keeps polling on a transient background failure', () => {
    let callCount = 0;
    const getReceipts = (): Observable<ReceiptSummary[]> => {
      callCount++;
      if (callCount === 2) {
        return throwError(() => new Error('transient'));
      }
      return of([callCount < 3 ? pendingReceipt : { ...pendingReceipt, status: 'ready' as const }]);
    };
    const component = createComponent(getReceipts);

    expect(callCount).toBe(1);

    vi.advanceTimersByTime(5000);
    expect(callCount).toBe(2);
    expect(component.error()).toBeNull();
    expect(component.loading()).toBe(false);

    vi.advanceTimersByTime(5000);
    expect(callCount).toBe(3);
    expect(component.hasPending()).toBe(false);

    vi.advanceTimersByTime(5000);
    expect(callCount).toBe(3);
  });

  it('manual refresh cancels the pending poll timer instead of duplicating it', () => {
    let callCount = 0;
    const getReceipts = (): Observable<ReceiptSummary[]> => {
      callCount++;
      return of([pendingReceipt]);
    };
    const component = createComponent(getReceipts);

    expect(callCount).toBe(1);

    vi.advanceTimersByTime(5000);
    expect(callCount).toBe(2);

    component.loadReceipts();
    expect(callCount).toBe(3);

    vi.advanceTimersByTime(5000);
    expect(callCount).toBe(4);
  });

  it('does not poll for a receipt stuck pending from before the queue existed', () => {
    const component = createComponent(() => of([stalePendingReceipt]));

    expect(component.receipts()).toEqual([stalePendingReceipt]);
    expect(component.hasPending()).toBe(false);
  });

  it('stops the poll timer on destroy', () => {
    let callCount = 0;
    const getReceipts = (): Observable<ReceiptSummary[]> => {
      callCount++;
      return of([pendingReceipt]);
    };
    const component = createComponent(getReceipts);

    expect(callCount).toBe(1);

    component.ngOnDestroy();
    vi.advanceTimersByTime(5000);
    expect(callCount).toBe(1);
  });
});
