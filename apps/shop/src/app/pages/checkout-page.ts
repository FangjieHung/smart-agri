import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DOCUMENT,
  inject,
  signal,
} from '@angular/core';
import { OrderingApi } from '../api/ordering-api';
import type { OrderRequest } from '../api/types';
import { describeSubmitFailure, type SubmitFailure } from '../domain/errors';
import {
  newSubmissionKey,
  orderBySubmissionKey,
  pendingSubmission,
  rememberOrder,
  savePendingSubmission,
  type RecentOrder,
} from '../domain/orders';
import { Announcer } from '../state/announcer';
import { Nav } from '../state/nav';
import { ShopStore } from '../state/shop-store';
import { ntd, telHref } from '../ui/format';
import { PageHeader } from '../ui/page-header';
import { PickupTicket } from '../ui/pickup-ticket';
import { TimeSheet } from '../ui/time-sheet';

type Field = 'name' | 'phone' | 'email';

/** 伺服器的欄位名稱 → 這頁的欄位。 */
const SERVER_FIELDS: Record<string, Field> = {
  'customer.name': 'name',
  'customer.phone': 'phone',
  'customer.email': 'email',
};

/**
 * 結帳：確認明細與取餐時間、填聯絡資料、送出。送出前先把送出鍵（UUID）與內容存進 localStorage，
 * 回應遺失時重試沿用同一個鍵，伺服器只會建立一筆訂單；成功就存下 token、清空購物車、前往訂單頁。
 */
@Component({
  selector: 'app-checkout-page',
  imports: [PageHeader, PickupTicket, TimeSheet],
  templateUrl: './checkout-page.html',
  styleUrl: './checkout-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class CheckoutPage {
  protected readonly store = inject(ShopStore);
  protected readonly nav = inject(Nav);
  private readonly api = inject(OrderingApi);
  private readonly announcer = inject(Announcer);
  private readonly document = inject(DOCUMENT);

  protected readonly ntd = ntd;
  protected readonly telHref = telHref;

  protected readonly name = signal('');
  protected readonly phone = signal('');
  protected readonly email = signal('');
  protected readonly errors = signal<Partial<Record<Field, string>>>({});
  protected readonly sending = signal(false);
  protected readonly failure = signal<SubmitFailure | null>(null);
  protected readonly existing = signal<RecentOrder | null>(null);
  protected readonly timeOpen = signal(false);
  protected readonly timeMessage = signal<string | null>(null);

  /** 這次結帳的送出鍵：上次送出沒有收到結果時沿用（伺服器若已收到會回「已經送出過」）。 */
  private submissionKey = '';

  protected readonly blockedLines = computed(() => this.store.lineWarnings().size > 0);

  constructor() {
    // 上次送出沒有收到結果：沿用送出鍵，並帶回當時填的聯絡資料（只有這種情況會留在手機上）。
    const pending = pendingSubmission(this.store.slug());
    this.submissionKey = pending?.submissionKey ?? newSubmissionKey();
    const customer = pending?.request.customer;
    if (customer) {
      this.name.set(customer.name ?? '');
      this.phone.set(customer.phone ?? '');
      this.email.set(customer.email ?? '');
    }
    void this.store.loadMenu();
    void this.store.loadSlots();
  }

  protected set(field: Field, value: string): void {
    ({ name: this.name, phone: this.phone, email: this.email })[field].set(value);
    if (this.errors()[field]) this.errors.update((errors) => ({ ...errors, [field]: undefined }));
  }

  /** 送出前在這支手機上能檢查的部分；其餘交給伺服器。 */
  private validate(): Partial<Record<Field, string>> {
    const limits = this.store.shop()?.limits;
    const errors: Partial<Record<Field, string>> = {};
    const name = this.name().trim();
    if (!name) errors.name = '請填寫姓名。';
    else if (limits && name.length > limits.nameMaxLength)
      errors.name = `姓名最多 ${limits.nameMaxLength} 個字。`;
    const digits = this.phone().replace(/\D/g, '');
    if (!this.phone().trim()) errors.phone = '請填寫電話，店家會用這支電話與您聯絡。';
    else if (digits.length < 8 || digits.length > 15)
      errors.phone = '請填寫手機或市話，例如 0912-345-678。';
    const email = this.email().trim();
    if (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))
      errors.email = 'Email 的格式不正確，例如 name@example.com。';
    return errors;
  }

  protected async submit(): Promise<void> {
    if (this.sending()) return;
    this.failure.set(null);
    const errors = this.validate();
    this.errors.set(errors);
    const firstInvalid = (['name', 'phone', 'email'] as const).find((field) => errors[field]);
    if (firstInvalid) {
      this.announcer.announce('有些資料需要修正，請看欄位旁的說明。');
      this.document.getElementById(`checkout-${firstInvalid}`)?.focus();
      return;
    }
    if (!this.store.serviceReady()) {
      this.timeMessage.set('請先選擇取餐時間。');
      this.timeOpen.set(true);
      return;
    }
    if (this.blockedLines()) {
      this.failure.set({
        recovery: 'mark-lines',
        message: '購物車裡有品項在這個取餐時間無法供應，請回購物車移除或改選時間。',
        fields: {},
        lines: [],
        orderNumber: null,
      });
      return;
    }

    const request = this.buildRequest();
    if (!request) return;
    const slug = this.store.slug();
    savePendingSubmission(slug, { submissionKey: this.submissionKey, request });

    this.sending.set(true);
    const result = await this.api.submit(slug, request);
    this.sending.set(false);

    if (result.ok) {
      rememberOrder(slug, result.data.token, this.submissionKey, result.data.order);
      savePendingSubmission(slug, null);
      this.store.clearCart();
      this.announcer.announce(`訂單已送出，訂單編號 ${result.data.order.displayNumber}`);
      this.nav.go(slug, 'status', { hash: result.data.token, replace: true });
      return;
    }

    // 伺服器明確拒絕（4xx）時訂單沒有建立，不必留著送出內容；只有連不上或 5xx 時結果不明，留著讓下次沿用同一個鍵。
    if (result.status >= 400 && result.status < 500) savePendingSubmission(slug, null);
    const failure = describeSubmitFailure(result, this.store.shop()?.phone ?? '');
    await this.recover(failure);
    this.failure.set(failure);
    this.announcer.announce(failure.message);
    this.document.getElementById('checkout-failure')?.focus();
  }

  private async recover(failure: SubmitFailure): Promise<void> {
    const slug = this.store.slug();
    switch (failure.recovery) {
      case 'refresh-menu':
        await Promise.all([this.store.refreshShop(), this.store.loadMenu()]);
        break;
      case 'pick-time':
        await Promise.all([this.store.refreshShop(), this.store.loadSlots()]);
        this.store.clearService();
        this.timeMessage.set(failure.message);
        this.timeOpen.set(true);
        break;
      case 'mark-lines':
        this.store.markLineProblems(failure.lines);
        if (failure.lines.length > 0) await this.store.loadMenu();
        break;
      case 'fix-fields': {
        const errors: Partial<Record<Field, string>> = {};
        for (const [field, message] of Object.entries(failure.fields)) {
          const local = SERVER_FIELDS[field];
          if (local) errors[local] = message;
        }
        this.errors.set(errors);
        break;
      }
      case 'show-existing': {
        const existing = orderBySubmissionKey(slug, this.submissionKey);
        this.existing.set(existing);
        savePendingSubmission(slug, null);
        this.store.clearCart();
        this.submissionKey = newSubmissionKey();
        break;
      }
      case 'stop':
        await this.store.refreshShop();
        break;
      case 'retry':
        break;
    }
  }

  private buildRequest(): OrderRequest | null {
    const shop = this.store.shop();
    const menu = this.store.menu();
    const service = this.store.service();
    if (!shop || !menu || !service) return null;
    const cart = this.store.cart();
    const email = this.email().trim();
    const orderComment = cart.orderComment.trim();
    const scheduled = service.type === 'takeout-scheduled';
    return {
      serviceType: service.type,
      pickupDate: scheduled ? service.date : null,
      pickupSlot: scheduled ? service.slot : null,
      menuVersion: menu.version,
      lines: cart.lines.map((line) => ({
        itemId: line.itemId,
        quantity: line.quantity,
        selections: line.selections,
        comment: line.comment,
      })),
      customer: { name: this.name().trim(), phone: this.phone().trim(), email: email || null },
      orderComment: shop.showOrderComment && orderComment ? orderComment : null,
      tableware: shop.showTableware ? (cart.tableware ?? false) : null,
      submissionKey: this.submissionKey,
    };
  }

  protected closeTime(): void {
    this.timeOpen.set(false);
    this.timeMessage.set(null);
  }
}
