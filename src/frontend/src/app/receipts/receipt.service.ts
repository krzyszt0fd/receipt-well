import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface ReceiptSummary {
  id: string;
  fileName: string;
  fileSize: number;
  status: 'pending' | 'ready' | 'error';
  uploadedAt: string;
  storeName: string | null;
  purchaseDate: string | null;
  tags: string[];
}

@Injectable({ providedIn: 'root' })
export class ReceiptService {
  private readonly http = inject(HttpClient);

  getReceipts(query?: string): Observable<ReceiptSummary[]> {
    const params = query ? new HttpParams().set('q', query) : undefined;
    return this.http.get<ReceiptSummary[]>(`${environment.apiUrl}/receipts`, { params });
  }

  getStagingSlot(): Observable<{ stagingUri: string; stagingBlobName: string }> {
    return this.http.post<{ stagingUri: string; stagingBlobName: string }>(
      `${environment.apiUrl}/receipts/staging-slot`,
      {}
    );
  }

  confirmUpload(stagingBlobName: string, originalFileName: string): Observable<{ receiptId: string; fileName: string; fileSize: number }> {
    return this.http.post<{ receiptId: string; fileName: string; fileSize: number }>(
      `${environment.apiUrl}/receipts/confirm`,
      { stagingBlobName, originalFileName }
    );
  }

  deleteReceipt(id: string): Observable<void> {
    return this.http.delete<void>(`${environment.apiUrl}/receipts/${id}`);
  }

  renameReceipt(id: string, fileName: string): Observable<void> {
    return this.http.put<void>(`${environment.apiUrl}/receipts/${id}`, { fileName });
  }

  getDownloadUrl(id: string): Observable<{ downloadUri: string }> {
    return this.http.get<{ downloadUri: string }>(`${environment.apiUrl}/receipts/${id}/download-url`);
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
