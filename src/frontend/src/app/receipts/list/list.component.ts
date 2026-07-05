import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
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

  visibleTags(tags: string[]): string[] {
    return tags.slice(0, MAX_VISIBLE_TAGS);
  }

  extraTagCount(tags: string[]): number {
    return Math.max(0, tags.length - MAX_VISIBLE_TAGS);
  }
}
