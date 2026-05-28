import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import {
  MSAL_INSTANCE,
  MsalService,
  MsalGuard,
  MsalBroadcastService
} from '@azure/msal-angular';
import { PublicClientApplication, BrowserCacheLocation } from '@azure/msal-browser';

import { routes } from './app.routes';
import { environment } from '../environments/environment';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptorsFromDi()),
    {
      provide: MSAL_INSTANCE,
      useValue: new PublicClientApplication({
        auth: {
          clientId: environment.externalId.clientId,
          authority: environment.externalId.authority,
          knownAuthorities: [environment.externalId.knownAuthority],
          redirectUri: '/'
        },
        cache: {
          cacheLocation: BrowserCacheLocation.LocalStorage
        }
      })
    },
    MsalService,
    MsalGuard,
    MsalBroadcastService
  ]
};
