import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptorsFromDi } from '@angular/common/http';
import {
  MSAL_INSTANCE,
  MSAL_GUARD_CONFIG,
  MSAL_INTERCEPTOR_CONFIG,
  MsalService,
  MsalGuard,
  MsalBroadcastService,
  MsalInterceptor,
  MsalGuardConfiguration,
  MsalInterceptorConfiguration
} from '@azure/msal-angular';
import { PublicClientApplication, BrowserCacheLocation, InteractionType } from '@azure/msal-browser';

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
          redirectUri: '/auth'
        },
        cache: {
          cacheLocation: BrowserCacheLocation.LocalStorage
        }
      })
    },
    {
      provide: MSAL_GUARD_CONFIG,
      useValue: {
        interactionType: InteractionType.Redirect,
        authRequest: { scopes: [environment.externalId.apiScope] },
        loginFailedRoute: '/'
      } as MsalGuardConfiguration
    },
    {
      provide: MSAL_INTERCEPTOR_CONFIG,
      useValue: {
        interactionType: InteractionType.Redirect,
        protectedResourceMap: new Map([[environment.apiUrl, [environment.externalId.apiScope]]])
      } as MsalInterceptorConfiguration
    },
    MsalService,
    MsalGuard,
    MsalBroadcastService,
    MsalInterceptor
  ]
};
