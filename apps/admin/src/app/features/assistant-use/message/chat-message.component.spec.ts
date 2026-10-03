import { TestBed } from '@angular/core/testing';
import type { ChatMessageView, ChatReplyView } from '../../../core/domain/conversation.model';
import { replyFor } from '../assistant-use.testing';
import { ChatMessageComponent } from './chat-message.component';

function render(message: ChatMessageView) {
  TestBed.configureTestingModule({ imports: [ChatMessageComponent] });
  const fixture = TestBed.createComponent(ChatMessageComponent);
  fixture.componentRef.setInput('message', message);
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement };
}

const assistant = (reply: ChatReplyView): ChatMessageView => ({
  id: 'chat-message-2',
  author: 'assistant',
  reply,
  createdAt: '2026-09-22T02:00:00.000Z',
});

describe('ChatMessageComponent', () => {
  it('shows the account’s own question as a user bubble', () => {
    const { host } = render({ id: 'chat-message-1', author: 'account', text: '退貨要幾天？', createdAt: '2026-09-22T02:00:00.000Z' });

    expect(host.querySelector('[data-author="account"]')?.textContent).toContain('退貨要幾天？');
  });

  it('labels company data answers and exposes their citations through a button', () => {
    const { fixture, host } = render(assistant(replyFor('收到商品後幾天內可以退貨？')));
    const emitted: unknown[] = [];
    fixture.componentInstance.openCitations.subscribe((value) => emitted.push(value));

    expect(host.querySelector('[data-kind="company-data"]')?.textContent).toContain('根據你的資料');
    const button = host.querySelector<HTMLButtonElement>('button.citation-toggle');
    expect(button?.textContent).toContain('查看引用來源');
    expect(button?.getAttribute('aria-haspopup')).toBe('dialog');
    button?.click();
    expect(emitted).toHaveLength(1);
  });

  it('keeps the company-data label but drops the citation button when 顯示引用出處 is off', () => {
    const { host } = render(
      assistant({
        kind: 'company-data',
        text: '收到商品後 7 天內可以申請退貨。',
        citations: [],
        citationNotice: '這則回答出自組織資料；這個助理設定為不顯示引用出處。',
      }),
    );

    const bubble = host.querySelector('[data-kind="company-data"]');
    expect(bubble?.textContent).toContain('根據你的資料');
    expect(host.querySelector('button.citation-toggle')).toBeNull();
    expect(host.querySelector('p.citation-notice')?.textContent).toContain('不顯示引用出處');
  });

  it('labels general knowledge separately and never shows a citation button for it', () => {
    const { host } = render(assistant(replyFor('皮革商品平常要怎麼保養？')));

    const bubble = host.querySelector('[data-kind="general-knowledge"]');
    expect(bubble?.textContent).toContain('一般知識補充');
    expect(bubble?.textContent).toContain('不是組織資料');
    expect(host.querySelector('button.citation-toggle')).toBeNull();
  });

  it('shows a no-result reply with next steps', () => {
    const { host } = render(assistant(replyFor('可以幫我訂機票嗎？')));

    const bubble = host.querySelector('[data-kind="no-result"]');
    expect(bubble?.textContent).toContain('查無資料');
    expect(bubble?.querySelectorAll('ul.next-steps li').length).toBeGreaterThan(0);
  });

  it('shows a database query answer with its source, period and the server’s figures', () => {
    const { host } = render(
      assistant({
        kind: 'database-query',
        text: '根據「訂單問題」的紀錄筆數查詢：近 30 天共有 3 筆有效紀錄。',
        query: {
          status: 'answered',
          databaseId: 'database-orders',
          databaseName: '訂單問題',
          query: 'record-count',
          queryLabel: '紀錄筆數',
          period: { name: 'last-30-days', from: '2026-08-24', to: '2026-09-22', label: '近 30 天（2026-08-24 至 2026-09-22）' },
          previousPeriod: { name: null, from: '2026-07-25', to: '2026-08-23', label: '2026-07-25 至 2026-08-23' },
          subjectOnly: false,
          figures: [{ metric: '有效紀錄筆數', value: 3, display: '3 筆', previousDisplay: '1 筆', changeLabel: '+2 筆' }],
          message: null,
        },
      }),
    );

    const bubble = host.querySelector('[data-kind="database-query"]');
    expect(bubble?.textContent).toContain('數據庫查詢');
    expect(host.querySelector('.query-source')?.textContent).toContain('訂單問題（紀錄筆數）');
    expect(host.querySelector('.query-source')?.textContent).toContain('近 30 天（2026-08-24 至 2026-09-22）');
    const cells = [...host.querySelectorAll('.query-figures tbody th, .query-figures tbody td')].map((cell) => cell.textContent?.trim());
    expect(cells).toEqual(['有效紀錄筆數', '3 筆', '1 筆', '+2 筆']);
    expect(host.querySelector('button')).toBeNull();
  });

  it('shows a refused query without any source, period or figure', () => {
    const { host } = render(
      assistant({
        kind: 'database-query',
        text: '目前無法查詢。',
        query: {
          status: 'not-available',
          databaseId: null,
          databaseName: null,
          query: null,
          queryLabel: null,
          period: null,
          previousPeriod: null,
          subjectOnly: false,
          figures: [],
          message: null,
        },
      }),
    );

    expect(host.querySelector('[data-kind="database-query"]')?.textContent).toContain('目前無法查詢。');
    expect(host.querySelector('.query-source')).toBeNull();
    expect(host.querySelector('.query-figures')).toBeNull();
  });

  it('lets the user start the inline form from a form request', () => {
    const { fixture, host } = render(assistant(replyFor('我要回報訂單問題')));
    const started: unknown[] = [];
    fixture.componentInstance.startForm.subscribe((form) => started.push(form));

    host.querySelector<HTMLButtonElement>('button.form-start')?.click();
    expect(started).toHaveLength(1);
  });
});
