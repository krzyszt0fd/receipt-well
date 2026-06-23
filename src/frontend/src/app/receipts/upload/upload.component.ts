import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { firstValueFrom } from 'rxjs';
import { ReceiptService } from '../receipt.service';

type UploadState = 'idle' | 'uploading' | 'confirmed' | 'error';

@Component({
  selector: 'app-upload',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatCardModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule, RouterLink],
  templateUrl: './upload.component.html',
  styleUrl: './upload.component.scss'
})
export class UploadComponent {
  private readonly receiptService = inject(ReceiptService);

  readonly state = signal<UploadState>('idle');
  readonly validationError = signal<string | null>(null);
  readonly selectedFile = signal<File | null>(null);
  readonly stagingBlobName = signal<string | null>(null);
  readonly confirmedReceipt = signal<{ fileName: string; fileSize: number } | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly retriable = signal(true);

  readonly formattedFileSize = computed(() => {
    const receipt = this.confirmedReceipt();
    if (!receipt) return '';
    const bytes = receipt.fileSize;
    if (bytes >= 1_000_000) {
      return `${(bytes / 1_000_000).toFixed(1)} MB`;
    }
    return `${Math.round(bytes / 1_000)} KB`;
  });

  private readonly ALLOWED_TYPES = new Set(['image/png', 'image/jpeg', 'image/webp']);
  private readonly MAX_SIZE = 20_000_000;

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;

    if (!file) {
      this.selectedFile.set(null);
      return;
    }

    if (!this.ALLOWED_TYPES.has(file.type)) {
      this.validationError.set('Unsupported file type. Please choose a PNG, JPEG, or WEBP.');
      this.selectedFile.set(null);
      return;
    }

    if (file.size > this.MAX_SIZE) {
      this.validationError.set('File exceeds the 20 MB limit. Please choose a smaller photo.');
      this.selectedFile.set(null);
      return;
    }

    this.validationError.set(null);
    this.selectedFile.set(file);
  }

  async submit(): Promise<void> {
    const file = this.selectedFile();
    if (!file || this.state() !== 'idle') return;

    this.state.set('uploading');

    let slot: { stagingUri: string; stagingBlobName: string };
    try {
      slot = await firstValueFrom(this.receiptService.getStagingSlot());
    } catch (err) {
      this.handleError(err, 'Failed to prepare upload. Please try again.');
      return;
    }

    try {
      await this.receiptService.uploadToBlob(slot.stagingUri, file);
    } catch (err) {
      this.handleError(err, 'Upload failed. Please try again.');
      return;
    }

    // Blob is fully in staging — record name so confirm can be retried independently
    this.stagingBlobName.set(slot.stagingBlobName);
    await this.confirmStaged(file.name);
  }

  async tryAgain(): Promise<void> {
    const blobName = this.stagingBlobName();
    const file = this.selectedFile();
    if (blobName && file) {
      // Blob is still in staging — retry only the confirm step
      this.state.set('uploading');
      this.errorMessage.set(null);
      await this.confirmStaged(file.name);
    } else {
      this.reset();
    }
  }

  reset(): void {
    this.state.set('idle');
    this.selectedFile.set(null);
    this.stagingBlobName.set(null);
    this.confirmedReceipt.set(null);
    this.errorMessage.set(null);
    this.validationError.set(null);
    this.retriable.set(true);
  }

  private async confirmStaged(originalFileName: string): Promise<void> {
    const blobName = this.stagingBlobName()!;
    try {
      const result = await firstValueFrom(
        this.receiptService.confirmUpload(blobName, originalFileName)
      );
      this.confirmedReceipt.set({ fileName: result.fileName, fileSize: result.fileSize });
      this.state.set('confirmed');
    } catch (err) {
      this.handleError(err, 'Confirmation failed. Please try again.');
    }
  }

  private handleError(err: unknown, fallback: string): void {
    if (err instanceof HttpErrorResponse && err.status === 400) {
      this.retriable.set(false);
      const body = err.error;
      const firstFieldError = body?.errors
        ? (Object.values(body.errors as Record<string, string[]>)[0]?.[0])
        : undefined;
      this.errorMessage.set(body?.detail ?? firstFieldError ?? body?.title ?? fallback);
    } else {
      this.retriable.set(true);
      this.errorMessage.set(err instanceof Error ? err.message : fallback);
    }
    this.state.set('error');
  }
}
