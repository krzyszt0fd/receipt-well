import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ReceiptService, ReceiptSummary } from '../receipt.service';

const MAX_VISIBLE_TAGS = 5;

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
export class ReceiptListComponent implements OnInit {
  private readonly receiptService = inject(ReceiptService);

  readonly receipts = signal<ReceiptSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.loadReceipts();
  }

  loadReceipts(): void {
    this.loading.set(true);
    this.error.set(null);
    this.receiptService.getReceipts().subscribe({
      next: receipts => {
        this.receipts.set(receipts);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Failed to load receipts. Please try again.');
        this.loading.set(false);
      }
    });
  }

  visibleTags(tags: string[]): string[] {
    return tags.slice(0, MAX_VISIBLE_TAGS);
  }

  extraTagCount(tags: string[]): number {
    return Math.max(0, tags.length - MAX_VISIBLE_TAGS);
  }
}
