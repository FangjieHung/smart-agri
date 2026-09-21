import {
  Component,
  DestroyRef,
  effect,
  ElementRef,
  HostListener,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { BreakpointObserver } from '@angular/cdk/layout';
import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { DomPortal } from '@angular/cdk/portal';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';

const NARROW_QUERY = '(max-width: 1280px)';
const FOCUSABLE_SELECTOR =
  'button, a[href], input, select, textarea, [tabindex]:not([tabindex="-1"])';

@Component({
  selector: 'lib-responsive-panel',
  templateUrl: './responsive-panel.component.html',
  styleUrl: './responsive-panel.component.scss',
})
export class ResponsivePanelComponent {
  readonly open = input(false);
  readonly heading = input('');
  readonly closeLabel = input.required<string>();
  readonly showCloseButton = input(true);
  readonly showHeaderDivider = input(true);
  readonly closed = output<void>();

  private static nextId = 0;
  protected readonly headingId = `responsive-panel-heading-${ResponsivePanelComponent.nextId++}`;

  private readonly breakpointObserver = inject(BreakpointObserver);
  protected readonly isNarrow = toSignal(
    this.breakpointObserver.observe([NARROW_QUERY]).pipe(map((result) => result.matches)),
    { initialValue: false },
  );

  private readonly overlay = inject(Overlay);
  private readonly wrapperRef = viewChild<ElementRef<HTMLElement>>('panelWrapper');
  private readonly panelRef = viewChild<ElementRef<HTMLElement>>('panel');

  // Drives the template's @if instead of open() directly. Kept as a separate signal so the
  // "closing" effect below can synchronously detach the overlay portal (restoring the DOM node
  // to its inline position) *before* setting this false — if the template read open() directly,
  // Angular would tear down the @if view as part of the same change-detection pass that updates
  // the `open` input, which runs *before* our effect() gets a chance to run (effects flush after
  // template updates), orphaning the portaled node inside the CDK overlay instead of removing it.
  protected readonly renderContent = signal(false);

  private lastFocused: HTMLElement | null = null;
  private overlayRef: OverlayRef | null = null;
  private domPortal: DomPortal | null = null;
  private portaledWrapper: HTMLElement | null = null;

  constructor() {
    effect(() => {
      if (this.open()) {
        this.renderContent.set(true);
      } else {
        this.detachOverlay();
        this.renderContent.set(false);
      }
    });

    // Narrow-mode content is relocated into a CDK Overlay (appended to document.body) instead of
    // staying inline, so the fixed backdrop/sheet can't be trapped inside an ancestor's stacking
    // context (e.g. Angular Material's .mat-drawer-content, which sets its own z-index and would
    // otherwise cap us below the app's mobile sidenav no matter how high our own z-index is).
    // DomPortal *moves* the already-rendered wrapper node rather than recreating it, so an
    // isNarrow() flip mid-open never disturbs focus or destroys projected content. This only
    // needs to react while the wrapper already exists — the effect above owns teardown.
    effect(() => {
      const wrapper = this.wrapperRef();
      if (!wrapper) return;
      if (this.isNarrow()) {
        this.attachOverlay(wrapper);
      } else {
        this.detachOverlay();
      }
    });

    // Focus lifecycle (only depends on open() transitions, not isNarrow — so resizing mid-open
    // never re-steals or re-releases focus).
    effect(() => {
      if (this.open()) {
        this.lastFocused = document.activeElement as HTMLElement | null;
        this.focusableElements()[0]?.focus();
      } else {
        this.lastFocused?.focus?.();
        this.lastFocused = null;
      }
    });

    // Scroll lock (always reflects current open && isNarrow state)
    effect(() => {
      document.body.style.overflow = this.open() && this.isNarrow() ? 'hidden' : '';
    });

    inject(DestroyRef).onDestroy(() => this.detachOverlay());
  }

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.open()) this.closed.emit();
  }

  protected onTrapKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Tab' || !this.isNarrow()) return;
    const focusables = this.focusableElements();
    if (focusables.length === 0) return;
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  private attachOverlay(wrapper: ElementRef<HTMLElement>): void {
    if (this.overlayRef) return;
    const active = document.activeElement as HTMLElement | null;
    const hadFocus = !!active && wrapper.nativeElement.contains(active);
    this.overlayRef = this.overlay.create({ positionStrategy: this.overlay.position().global() });
    this.domPortal = new DomPortal(wrapper);
    this.portaledWrapper = wrapper.nativeElement;
    this.overlayRef.attach(this.domPortal);
    // jsdom (unlike real browsers) blurs an element when it's reparented via appendChild, even
    // within the same document — restore focus explicitly so the move itself is never observable.
    if (hadFocus) active?.focus();
  }

  private detachOverlay(): void {
    if (!this.domPortal) return;
    const active = document.activeElement as HTMLElement | null;
    const hadFocus = !!active && !!this.portaledWrapper?.contains(active);
    if (this.domPortal.isAttached) {
      this.domPortal.detach();
    }
    this.domPortal = null;
    this.portaledWrapper = null;
    this.overlayRef?.dispose();
    this.overlayRef = null;
    if (hadFocus) active?.focus();
  }

  private focusableElements(): HTMLElement[] {
    const root = this.panelRef()?.nativeElement ?? this.wrapperRef()?.nativeElement ?? null;
    return root ? Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)) : [];
  }
}
