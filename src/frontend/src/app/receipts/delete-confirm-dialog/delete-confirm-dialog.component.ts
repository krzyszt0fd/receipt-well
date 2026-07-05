import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';

export interface DeleteConfirmDialogData {
  fileName: string;
}

@Component({
  selector: 'app-delete-confirm-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Delete receipt?</h2>
    <mat-dialog-content>Delete {{ data.fileName }}? This can't be undone.</mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="dialogRef.close(false)">Cancel</button>
      <button mat-raised-button color="warn" type="button" (click)="dialogRef.close(true)">Delete</button>
    </mat-dialog-actions>
  `
})
export class DeleteConfirmDialogComponent {
  readonly dialogRef = inject(MatDialogRef<DeleteConfirmDialogComponent>);
  readonly data = inject<DeleteConfirmDialogData>(MAT_DIALOG_DATA);
}
