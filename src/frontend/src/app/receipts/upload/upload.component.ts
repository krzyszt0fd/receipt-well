import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
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
  imports: [MatCardModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './upload.component.html',
  styleUrl: './upload.component.scss'
})
export class UploadComponent {
  private readonly receiptService = inject(ReceiptService);

  readonly state = signal<UploadState>('idle');
  readonly validationError = signal<string | null>(null);
  readonly selectedFile = signal<File | null>(null);
  readonly stagingBlobName = signal<string | null>(null);

  private readonly ALLOWED_TYPES = new Set(['image/png', 'image/jpeg', 'image/webp', 'image/gif']);
  private readonly MAX_SIZE = 20_000_000;

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;

    if (!file) {
      this.selectedFile.set(null);
      return;
    }

    if (!this.ALLOWED_TYPES.has(file.type)) {
      this.validationError.set('Unsupported file type. Please choose a PNG, JPEG, WEBP, or GIF.');
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
    try {
      const slot = await firstValueFrom(this.receiptService.getStagingSlot());
      this.stagingBlobName.set(slot.stagingBlobName);
      await this.receiptService.uploadToBlob(slot.stagingUri, file);
      // Phase 3: call confirmUpload here and transition to 'confirmed'
    } catch {
      // Phase 3: set errorMessage and transition to 'error' here
      this.state.set('idle');
    }
  }
}
