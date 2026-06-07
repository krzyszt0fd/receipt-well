import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { MsalBroadcastService } from '@azure/msal-angular';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        { provide: MsalBroadcastService, useValue: { inProgress$: new Subject() } }
      ]
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });
});
