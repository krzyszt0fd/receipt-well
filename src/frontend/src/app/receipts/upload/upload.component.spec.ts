import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';
import { UploadComponent } from './upload.component';
import { ReceiptService } from '../receipt.service';

const mockReceiptService = {
  getStagingSlot: () => of({ stagingUri: 'https://example.com/sas', stagingBlobName: 'user/guid' }),
  uploadToBlob: () => Promise.resolve(),
  confirmUpload: () => of({ receiptId: 'guid', fileName: 'receipt.jpg', fileSize: 12345 })
};

describe('UploadComponent', () => {
  let component: UploadComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UploadComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ReceiptService, useValue: mockReceiptService }
      ]
    }).compileComponents();

    const fixture = TestBed.createComponent(UploadComponent);
    component = fixture.componentInstance;
  });

  it('rejects a file exceeding 20 MB', () => {
    const file = new File([''], 'big.jpg', { type: 'image/jpeg' });
    Object.defineProperty(file, 'size', { value: 20_000_001 });

    component.onFileSelected({ target: { files: [file] } } as unknown as Event);

    expect(component.validationError()).toBeTruthy();
    expect(component.selectedFile()).toBeNull();
  });

  it('rejects a disallowed MIME type', () => {
    const file = new File(['content'], 'receipt.pdf', { type: 'application/pdf' });

    component.onFileSelected({ target: { files: [file] } } as unknown as Event);

    expect(component.validationError()).toBeTruthy();
    expect(component.selectedFile()).toBeNull();
  });

  it('accepts a valid JPEG', () => {
    const file = new File(['content'], 'receipt.jpg', { type: 'image/jpeg' });

    component.onFileSelected({ target: { files: [file] } } as unknown as Event);

    expect(component.validationError()).toBeNull();
    expect(component.selectedFile()).toBe(file);
  });

  it('accepts a valid PNG', () => {
    const file = new File(['content'], 'receipt.png', { type: 'image/png' });

    component.onFileSelected({ target: { files: [file] } } as unknown as Event);

    expect(component.validationError()).toBeNull();
    expect(component.selectedFile()).toBe(file);
  });
});
