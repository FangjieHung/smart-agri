import { Injectable, signal } from '@angular/core';

/** Lets the global recent-chat list refresh when a conversation changes in place. */
@Injectable({ providedIn: 'root' })
export class ChatHistoryRevisionService {
  private readonly value = signal(0);
  readonly revision = this.value.asReadonly();
  bump(): void { this.value.update((current) => current + 1); }
}
