import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Subscription } from 'rxjs';
import { ReceiptService, ReceiptSummary } from '../receipt.service';

const MAX_VISIBLE_TAGS = 5;
const POLL_INTERVAL_MS = 5000;

@Component({
  selector: 'app-receipt-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatCardModule,
    MatChipsModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    RouterLink,
    DatePipe
  ],
  templateUrl: './list.component.html',
  styleUrl: './list.component.scss'
})
export class ReceiptListComponent implements OnInit, OnDestroy {
  private readonly receiptService = inject(ReceiptService);

  readonly receipts = signal<ReceiptSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly hasPending = computed(() => this.receipts().some(r => r.status === 'pending'));

  private pollTimeoutId: ReturnType<typeof setTimeout> | null = null;
  private fetchSubscription: Subscription | null = null;

  ngOnInit(): void {
    this.loadReceipts();
  }

  ngOnDestroy(): void {
    this.clearPollTimeout();
    this.fetchSubscription?.unsubscribe();
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

  private pollReceipts(): void {
    this.runFetch({
      next: receipts => {
        this.receipts.set(receipts);
        this.schedulePollIfPending();
      },
      error: () => {
        // Silent retry: background poll failures don't surface to the user.
        this.schedulePollIfPending();
      }
    });
  }

  private runFetch(handlers: { next: (receipts: ReceiptSummary[]) => void; error: () => void }): void {
    this.clearPollTimeout();
    this.fetchSubscription?.unsubscribe();
    this.fetchSubscription = this.receiptService.getReceipts().subscribe(handlers);
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

  visibleTags(tags: string[]): string[] {
    return tags.slice(0, MAX_VISIBLE_TAGS);
  }

  extraTagCount(tags: string[]): number {
    return Math.max(0, tags.length - MAX_VISIBLE_TAGS);
  }
}
