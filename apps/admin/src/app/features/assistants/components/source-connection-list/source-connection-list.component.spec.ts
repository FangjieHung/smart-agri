import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import type { ConnectableSourceView } from '../../../../core/domain/assistant-draft.model';
import { SourceConnectionListComponent } from './source-connection-list.component';

const GUIDE: ConnectableSourceView = {
  id: 'knowledge-product-guide',
  type: 'knowledge-base',
  name: '商品使用指南',
  summary: '4 份文件、2 則 FAQ',
  permission: 'owner',
  status: 'ready',
  updatedAt: '2026-09-18T07:30:00.000Z',
};

const ORDERS: ConnectableSourceView = {
  id: 'database-orders',
  type: 'database',
  name: '訂單資料庫',
  summary: '3 個資料表',
  permission: 'read-only',
  status: 'ready',
  updatedAt: '2026-09-18T07:30:00.000Z',
};

function render() {
  TestBed.configureTestingModule({
    imports: [SourceConnectionListComponent],
    providers: [provideRouter([])],
  });
  const fixture = TestBed.createComponent(SourceConnectionListComponent);
  return { fixture, page: fixture.nativeElement as HTMLElement };
}

describe('SourceConnectionListComponent', () => {
  it('counts a real connected source in the summary', () => {
    const { fixture, page } = render();
    fixture.componentRef.setInput('sources', [GUIDE, ORDERS]);
    fixture.componentRef.setInput('connected', [{ id: GUIDE.id, type: 'knowledge-base' }]);
    fixture.detectChanges();

    expect(page.querySelector('.source-summary')?.textContent).toContain('已連接 1 個知識庫、0 個資料庫');
  });

  /**
   * 對應 issue #49：API 模式下，mock 助理可能引用了不存在的知識庫 id（例如種子助理連
   * 接的知識庫，在這個組織的真實資料裡不存在）。這種「幽靈連接」不在 `sources` 裡，
   * 必須直接略過不計入已連接數，不能讓使用者以為多連了一個看不到的來源。
   */
  it('skips a connected reference that is not in the connectable sources list', () => {
    const { fixture, page } = render();
    fixture.componentRef.setInput('sources', [ORDERS]);
    fixture.componentRef.setInput('connected', [
      { id: ORDERS.id, type: 'database' },
      { id: 'knowledge-does-not-exist', type: 'knowledge-base' },
    ]);
    fixture.detectChanges();

    expect(page.querySelector('.source-summary')?.textContent).toContain('已連接 0 個知識庫、1 個資料庫');
  });

  /** issue #115：沒有文件也沒有 FAQ 的知識庫顯示「尚無內容」，不是「可使用」。 */
  it('shows 尚無內容 for a knowledge base with no documents or FAQs, and 可使用 when it has content', () => {
    const empty: ConnectableSourceView = { ...GUIDE, id: 'knowledge-empty', summary: '0 份文件、0 則 FAQ', status: 'empty' };
    const { fixture, page } = render();
    fixture.componentRef.setInput('sources', [empty, GUIDE]);
    fixture.componentRef.setInput('connected', []);
    fixture.detectChanges();

    const badges = Array.from(page.querySelectorAll<HTMLElement>('.status-badge'));
    expect(badges.map((badge) => badge.textContent?.trim())).toEqual(['尚無內容', '可使用']);
    expect(badges[0].getAttribute('data-tone')).toBe('neutral');
    expect(badges[1].getAttribute('data-tone')).toBe('success');
  });
});
