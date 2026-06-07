import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { Router } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../environments/environment';

@Component({
  selector: 'app-landing',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main>
      <h1>ReceiptWell</h1>
      <button type="button" (click)="signIn()" aria-label="Sign in to ReceiptWell">Sign In</button>
    </main>
  `
})
export class LandingComponent implements OnInit {
  private readonly msalService = inject(MsalService);
  private readonly router = inject(Router);

  ngOnInit(): void {
    if (this.msalService.instance.getAllAccounts().length > 0) {
      this.router.navigate(['/home']);
    }
  }

  signIn(): void {
    this.msalService.loginRedirect({
      scopes: [environment.externalId.apiScope],
      redirectStartPage: '/home'
    });
  }
}
