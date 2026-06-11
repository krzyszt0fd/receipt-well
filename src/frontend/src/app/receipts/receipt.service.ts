import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class ReceiptService {
  private readonly http = inject(HttpClient);

  getStagingSlot(): Observable<{ stagingUri: string; stagingBlobName: string }> {
    return this.http.post<{ stagingUri: string; stagingBlobName: string }>(
      `${environment.apiUrl}/receipts/staging-slot`,
      {}
    );
  }

  async uploadToBlob(sasUri: string, file: File): Promise<void> {
    const response = await fetch(sasUri, {
      method: 'PUT',
      headers: {
        'Content-Type': file.type,
        'x-ms-blob-type': 'BlockBlob',
        'x-ms-blob-content-disposition': `attachment; filename*=UTF-8''${encodeURIComponent(file.name)}`
      },
      body: file
    });
    if (!response.ok) {
      throw new Error(`Blob upload failed: ${response.status} ${response.statusText}`);
    }
  }
}
