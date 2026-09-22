import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AssistantCardComponent } from './assistant-card.component';

describe('AssistantCardComponent', () => {
  it('only renders the summary fields that help an owner decide what to do next', async () => {
    await TestBed.configureTestingModule({
      imports: [AssistantCardComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(AssistantCardComponent);
    fixture.componentRef.setInput('assistant', {
      id: 'assistant-customer-service',
      name: '客服助理',
      purpose: '回答商品、退貨與配送問題',
      status: 'published',
      audience: 'authorized-external-customers',
      permission: 'configure',
    });
    fixture.componentRef.setInput('channels', ['官網嵌入', '專屬連結']);
    fixture.componentRef.setInput('recentActivity', '今天更新');
    fixture.detectChanges();

    const card = fixture.nativeElement as HTMLElement;
    expect(card.textContent).toContain('客服助理');
    expect(card.textContent).toContain('回答商品、退貨與配送問題');
    expect(card.textContent).toContain('已發布');
    expect(card.textContent).toContain('已授權外部客戶');
    expect(card.textContent).toContain('官網嵌入');
    expect(card.textContent).toContain('最近活動：今天更新');
    expect(card.textContent).not.toContain('商品使用指南');
    expect(card.textContent).not.toContain('account-smb-admin');
  });
});
