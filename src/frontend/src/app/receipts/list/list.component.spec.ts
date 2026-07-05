import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ComponentFixture } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { ReceiptListComponent } from './list.component';
import { ReceiptService, ReceiptSummary } from '../receipt.service';
import { DeleteConfirmDialogComponent } from '../delete-confirm-dialog/delete-confirm-dialog.component';

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

function createComponent(getReceipts: (query?: string) => ReturnType<ReceiptService['getReceipts']>) {
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

describe('ReceiptListComponent search (Phase 2)', () => {
  function createSearchFixture(
    getReceipts: (query?: string) => Observable<ReceiptSummary[]>
  ): ComponentFixture<ReceiptListComponent> {
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
    return fixture;
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('calls getReceipts with the typed term after 300ms debounce', () => {
    const getReceipts = vi.fn(() => of([]) as Observable<ReceiptSummary[]>);
    const fixture = createSearchFixture(getReceipts);
    const component = fixture.componentInstance;

    getReceipts.mockClear();
    component.searchControl.setValue('rower');
    vi.advanceTimersByTime(299);
    expect(getReceipts).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(getReceipts).toHaveBeenCalledWith('rower');
  });

  it('shows the no-match panel when a term is active and results are empty', () => {
    const fixture = createSearchFixture(() => of([]));
    const component = fixture.componentInstance;

    component.searchControl.setValue('xyz');
    vi.advanceTimersByTime(300);
    fixture.detectChanges();

    expect(component.hasActiveSearch()).toBe(true);
    expect(component.noSearchResults()).toBe(true);
  });

  it('manually deleting the term (debounce path) fetches without a query', () => {
    const getReceipts = vi.fn(() => of([baseReceipt]) as Observable<ReceiptSummary[]>);
    const fixture = createSearchFixture(getReceipts);
    const component = fixture.componentInstance;

    component.searchControl.setValue('rower');
    vi.advanceTimersByTime(300);
    const callsBefore = getReceipts.mock.calls.length;

    component.searchControl.setValue('');
    vi.advanceTimersByTime(300);
    expect(getReceipts).toHaveBeenCalledTimes(callsBefore + 1);
    expect(getReceipts).toHaveBeenLastCalledWith(undefined);
  });

  it('clearSearch() fetches immediately without a term and resets query atomically', () => {
    const getReceipts = vi.fn(() => of([baseReceipt]) as Observable<ReceiptSummary[]>);
    const fixture = createSearchFixture(getReceipts);
    const component = fixture.componentInstance;

    component.searchControl.setValue('rower');
    vi.advanceTimersByTime(300);
    getReceipts.mockClear();

    component.clearSearch();

    // No timer advance needed — bypasses debounce
    expect(getReceipts).toHaveBeenCalledWith(undefined);
    expect(component.query()).toBe('');
    expect(component.searchControl.value).toBe('');
  });

  it('background poll carries the active search term', () => {
    const getReceipts = vi.fn(() => of([pendingReceipt]) as Observable<ReceiptSummary[]>);
    const fixture = createSearchFixture(getReceipts);
    const component = fixture.componentInstance;

    component.searchControl.setValue('rower');
    vi.advanceTimersByTime(300);
    getReceipts.mockClear();

    vi.advanceTimersByTime(5000);
    expect(getReceipts).toHaveBeenCalledWith('rower');
  });
});

describe('ReceiptListComponent delete flow (Phase 3)', () => {
  function createDeleteFixture(options: {
    deleteReceipt?: (id: string) => Observable<void>;
    dialogResult?: boolean;
  } = {}) {
    const dialogRefStub = { afterClosed: () => of(options.dialogResult ?? true) };
    const dialogOpen = vi.fn(() => dialogRefStub);
    const snackBarOpen = vi.fn();

    TestBed.configureTestingModule({
      imports: [ReceiptListComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        {
          provide: ReceiptService,
          useValue: {
            getReceipts: () => of([baseReceipt]),
            deleteReceipt: options.deleteReceipt ?? (() => of(undefined))
          }
        },
        { provide: MatDialog, useValue: { open: dialogOpen } },
        { provide: MatSnackBar, useValue: { open: snackBarOpen } }
      ]
    });

    const fixture = TestBed.createComponent(ReceiptListComponent);
    fixture.detectChanges();
    return { component: fixture.componentInstance, dialogOpen, snackBarOpen };
  }

  it('opens the confirmation dialog with the receipt fileName', () => {
    const { component, dialogOpen } = createDeleteFixture();

    component.deleteReceipt(baseReceipt);

    expect(dialogOpen).toHaveBeenCalledWith(
      DeleteConfirmDialogComponent,
      expect.objectContaining({ data: { fileName: baseReceipt.fileName } })
    );
  });

  it('confirming deletes the receipt and removes it from the list', () => {
    const deleteReceipt = vi.fn(() => of(undefined));
    const { component } = createDeleteFixture({ deleteReceipt, dialogResult: true });

    component.deleteReceipt(baseReceipt);

    expect(deleteReceipt).toHaveBeenCalledWith(baseReceipt.id);
    expect(component.receipts()).toEqual([]);
  });

  it('cancelling calls neither delete nor changes the list', () => {
    const deleteReceipt = vi.fn(() => of(undefined));
    const { component } = createDeleteFixture({ deleteReceipt, dialogResult: false });

    component.deleteReceipt(baseReceipt);

    expect(deleteReceipt).not.toHaveBeenCalled();
    expect(component.receipts()).toEqual([baseReceipt]);
  });

  it('shows a snackbar error and keeps the row when delete fails', () => {
    const deleteReceipt = vi.fn(() => throwError(() => new Error('fail')));
    const { component, snackBarOpen } = createDeleteFixture({ deleteReceipt, dialogResult: true });

    component.deleteReceipt(baseReceipt);

    expect(snackBarOpen).toHaveBeenCalledWith(
      'Failed to delete receipt. Please try again.',
      expect.anything(),
      expect.anything()
    );
    expect(component.receipts()).toEqual([baseReceipt]);
  });
});
