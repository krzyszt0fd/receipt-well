import { Routes } from '@angular/router';
import { MsalGuard, MsalRedirectComponent } from '@azure/msal-angular';
import { LandingComponent } from './landing/landing';
import { ShellComponent } from './shell/shell';

export const routes: Routes = [
  { path: 'auth', component: MsalRedirectComponent },
  { path: '', component: LandingComponent },
  {
    path: 'home',
    component: ShellComponent,
    canActivate: [MsalGuard],
    children: [
      { path: '', redirectTo: 'upload', pathMatch: 'full' },
      {
        path: 'upload',
        loadComponent: () =>
          import('./receipts/upload/upload.component').then(m => m.UploadComponent)
      }
    ]
  },
  { path: '**', redirectTo: '' }
];
