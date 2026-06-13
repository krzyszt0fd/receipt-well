import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { Router } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { environment } from '../../environments/environment';

@Component({
  selector: 'app-landing',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatCardModule, MatButtonModule, MatIconModule],
  templateUrl: './landing.html',
  styleUrl: './landing.scss'
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
