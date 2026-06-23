import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
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
  return fixture.componentInstance;
}

describe('ReceiptListComponent', () => {
  it('renders the populated state on successful fetch', () => {
    const component = createComponent(() => of([baseReceipt]));

    component.ngOnInit();

    expect(component.loading()).toBe(false);
    expect(component.error()).toBeNull();
    expect(component.receipts()).toEqual([baseReceipt]);
  });

  it('renders the empty state for zero receipts', () => {
    const component = createComponent(() => of([]));

    component.ngOnInit();

    expect(component.loading()).toBe(false);
    expect(component.error()).toBeNull();
    expect(component.receipts()).toEqual([]);
  });

  it('renders the error state when the fetch errors', () => {
    const component = createComponent(() => throwError(() => new Error('network error')));

    component.ngOnInit();

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
