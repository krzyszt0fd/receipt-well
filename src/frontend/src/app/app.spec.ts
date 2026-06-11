import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { MsalBroadcastService, MsalService } from '@azure/msal-angular';
import { App } from './app';

const mockMsalService = {
  instance: {
    getAllAccounts: () => [],
    getActiveAccount: () => null,
    setActiveAccount: () => {}
  }
};

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        { provide: MsalBroadcastService, useValue: { inProgress$: new Subject() } },
        { provide: MsalService, useValue: mockMsalService }
      ]
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });
});
