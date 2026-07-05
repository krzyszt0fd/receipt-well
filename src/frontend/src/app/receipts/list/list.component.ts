import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, effect, inject, signal, viewChild, viewChildren } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subscription } from 'rxjs';
import { debounceTime, distinctUntilChanged } from 'rxjs/operators';
import { ReceiptService, ReceiptSummary } from '../receipt.service';
import { DeleteConfirmDialogComponent } from '../delete-confirm-dialog/delete-confirm-dialog.component';

const MAX_VISIBLE_TAGS = 5;
const POLL_INTERVAL_MS = 5000;
const POLL_STALE_THRESHOLD_MS = 30 * 60 * 1000;
const SEARCH_DEBOUNCE_MS = 300;
const MAX_FILENAME_LENGTH = 255;

@Component({
  selector: 'app-receipt-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatCardModule,
    MatChipsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    ReactiveFormsModule,
    RouterLink,
    DatePipe
  ],
  templateUrl: './list.component.html',
  styleUrl: './list.component.scss'
})
export class ReceiptListComponent implements OnInit, OnDestroy {
  private readonly receiptService = inject(ReceiptService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);

  readonly searchControl = new FormControl('');
  readonly query = signal('');

  readonly editingId = signal<string | null>(null);
  readonly editControl = new FormControl('', { nonNullable: true });
  private readonly editInput = viewChild('editInput', { read: ElementRef<HTMLInputElement> });
  private readonly pencilButtons = viewChildren<ElementRef<HTMLElement>>('pencilBtn');
  private pendingPencilFocusId: string | null = null;
  private readonly renameSubscriptions = new Map<string, Subscription>();

  constructor() {
    // Move focus into the input when a row enters edit mode.
    effect(() => {
      const input = this.editInput();
      if (input && this.editingId()) {
        input.nativeElement.focus();
      }
    });

    // Return focus to the row's pencil button once it re-renders on exit.
    effect(() => {
      const buttons = this.pencilButtons();
      if (this.pendingPencilFocusId) {
        const target = buttons.find(
          b => b.nativeElement.dataset['receiptId'] === this.pendingPencilFocusId
        );
        if (target) {
          target.nativeElement.focus();
          this.pendingPencilFocusId = null;
        }
      }
    });
  }

  readonly receipts = signal<ReceiptSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly hasPending = computed(() =>
    this.receipts().some(r => r.status === 'pending' && !this.isStalePending(r.uploadedAt))
  );
  readonly hasActiveSearch = computed(() => this.query().trim().length > 0);
  readonly noSearchResults = computed(
    () => this.hasActiveSearch() && !this.loading() && this.receipts().length === 0
  );

  private pollTimeoutId: ReturnType<typeof setTimeout> | null = null;
  private fetchSubscription: Subscription | null = null;
  private searchSubscription: Subscription | null = null;
  private deleteSubscription: Subscription | null = null;

  ngOnInit(): void {
    this.searchSubscription = this.searchControl.valueChanges.pipe(
      debounceTime(SEARCH_DEBOUNCE_MS),
      distinctUntilChanged()
    ).subscribe(term => {
      this.query.set(term ?? '');
      this.refreshReceipts();
    });

    this.loadReceipts();
  }

  ngOnDestroy(): void {
    this.clearPollTimeout();
    this.fetchSubscription?.unsubscribe();
    this.searchSubscription?.unsubscribe();
    this.deleteSubscription?.unsubscribe();
    this.renameSubscriptions.forEach(sub => sub.unsubscribe());
  }

  loadReceipts(): void {
    this.loading.set(true);
    this.error.set(null);
    this.runFetch({
      next: receipts => {
        this.receipts.set(receipts);
        this.loading.set(false);
        this.schedulePollIfPending();
      },
      error: () => {
        this.error.set('Failed to load receipts. Please try again.');
        this.loading.set(false);
      }
    });
  }

  clearSearch(): void {
    // Suppress valueChanges so the debounce subscription doesn't also fire.
    // Set query only after fresh results arrive so signals update atomically.
    this.searchControl.setValue('', { emitEvent: false });
    this.clearPollTimeout();
    this.fetchSubscription?.unsubscribe();
    this.fetchSubscription = this.receiptService.getReceipts(undefined).subscribe({
      next: receipts => {
        this.query.set('');
        this.receipts.set(receipts);
        this.schedulePollIfPending();
      },
      error: () => {
        this.query.set('');
        this.schedulePollIfPending();
      }
    });
  }

  private refreshReceipts(): void {
    this.runFetch({
      next: receipts => {
        this.receipts.set(receipts);
        this.schedulePollIfPending();
      },
      error: () => {
        // Silent: background refreshes (search debounce, polls) don't surface errors.
        this.schedulePollIfPending();
      }
    });
  }

  private pollReceipts(): void {
    this.refreshReceipts();
  }

  private runFetch(handlers: { next: (receipts: ReceiptSummary[]) => void; error: () => void }): void {
    this.clearPollTimeout();
    this.fetchSubscription?.unsubscribe();
    const term = this.query();
    this.fetchSubscription = this.receiptService.getReceipts(term || undefined).subscribe(handlers);
  }

  private schedulePollIfPending(): void {
    if (this.hasPending()) {
      this.pollTimeoutId = setTimeout(() => this.pollReceipts(), POLL_INTERVAL_MS);
    }
  }

  private clearPollTimeout(): void {
    if (this.pollTimeoutId !== null) {
      clearTimeout(this.pollTimeoutId);
      this.pollTimeoutId = null;
    }
  }

  /** Receipts uploaded before the processing queue existed never leave `pending` — stop polling for those. */
  private isStalePending(uploadedAt: string): boolean {
    return Date.now() - new Date(uploadedAt).getTime() > POLL_STALE_THRESHOLD_MS;
  }

  deleteReceipt(receipt: ReceiptSummary): void {
    this.deleteSubscription?.unsubscribe();
    this.deleteSubscription = this.dialog
      .open(DeleteConfirmDialogComponent, { data: { fileName: receipt.fileName } })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) {
          return;
        }
        this.deleteSubscription = this.receiptService.deleteReceipt(receipt.id).subscribe({
          next: () => this.receipts.update(list => list.filter(r => r.id !== receipt.id)),
          error: () => this.snackBar.open('Failed to delete receipt. Please try again.', 'Dismiss', { duration: 5000 })
        });
      });
  }

  startEdit(receipt: ReceiptSummary): void {
    this.editControl.setValue(receipt.fileName);
    this.editingId.set(receipt.id);
  }

  cancelEdit(): void {
    const id = this.editingId();
    if (id === null) {
      return;
    }
    this.pendingPencilFocusId = id;
    this.editingId.set(null);
  }

  saveEdit(receipt: ReceiptSummary): void {
    // Guard against a double-save: (blur) fires after (keydown.enter) has already
    // exited edit mode, so only act while this row is still the one being edited.
    if (this.editingId() !== receipt.id) {
      return;
    }

    const trimmed = this.editControl.value.trim();

    // Empty/whitespace or over-length: block the save and stay in edit mode.
    if (trimmed.length === 0 || trimmed.length > MAX_FILENAME_LENGTH) {
      return;
    }

    // Unchanged: no API call, just leave edit mode.
    if (trimmed === receipt.fileName) {
      this.cancelEdit();
      return;
    }

    const previous = receipt.fileName;
    this.receipts.update(list =>
      list.map(r => (r.id === receipt.id ? { ...r, fileName: trimmed } : r))
    );
    this.pendingPencilFocusId = receipt.id;
    this.editingId.set(null);

    this.renameSubscriptions.get(receipt.id)?.unsubscribe();
    const subscription = this.receiptService.renameReceipt(receipt.id, trimmed).subscribe({
      next: () => this.renameSubscriptions.delete(receipt.id),
      error: () => {
        this.renameSubscriptions.delete(receipt.id);
        this.receipts.update(list =>
          list.map(r => (r.id === receipt.id ? { ...r, fileName: previous } : r))
        );
        this.snackBar.open('Failed to rename receipt. Please try again.', 'Dismiss', { duration: 5000 });
      }
    });
    this.renameSubscriptions.set(receipt.id, subscription);
  }

  visibleTags(tags: string[]): string[] {
    return tags.slice(0, MAX_VISIBLE_TAGS);
  }

  extraTagCount(tags: string[]): number {
    return Math.max(0, tags.length - MAX_VISIBLE_TAGS);
  }
}
