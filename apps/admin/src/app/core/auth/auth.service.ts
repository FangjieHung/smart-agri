import { inject, Injectable } from '@angular/core';
import { Router } from '@angular/router';

const AUTH_SESSION_KEY = 'sa.auth.session';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly router = inject(Router);

  login(): void {
    localStorage.setItem(AUTH_SESSION_KEY, JSON.stringify({ user: 'admin' }));
  }

  logout(): void {
    localStorage.removeItem(AUTH_SESSION_KEY);
    void this.router.navigateByUrl('/login');
  }
}
