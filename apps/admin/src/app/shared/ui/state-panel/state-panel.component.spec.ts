import { TestBed } from '@angular/core/testing';
import { StatePanelComponent } from './state-panel.component';

describe('StatePanelComponent', () => {
  it('displays the error reason and emits a recovery action', async () => {
    const fixture = TestBed.createComponent(StatePanelComponent);
    fixture.componentRef.setInput('state', 'error');
    fixture.componentRef.setInput('title', '無法讀取文件');
    fixture.componentRef.setInput(
      'description',
      '檔案格式不支援，請改用 PDF。',
    );
    fixture.componentRef.setInput('recoveryLabel', '重新選擇文件');
    let recovered = false;
    fixture.componentInstance.recover.subscribe(() => {
      recovered = true;
    });
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[role="alert"]')?.textContent).toContain(
      '檔案格式不支援，請改用 PDF。',
    );
    const button = element.querySelector('button') as HTMLButtonElement;
    expect(button.textContent?.trim()).toBe('重新選擇文件');
    expect(button.type).toBe('button');
    button.click();
    expect(recovered).toBe(true);
  });

  it('announces loading without displaying a recovery action', async () => {
    const fixture = TestBed.createComponent(StatePanelComponent);
    fixture.componentRef.setInput('state', 'loading');
    fixture.componentRef.setInput('title', '正在載入資料');
    fixture.componentRef.setInput('recoveryLabel', '重試');
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[role="status"]')?.textContent).toContain(
      '正在載入資料',
    );
    expect(element.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(element.querySelector('button')).toBeNull();
  });

  it('explains permission failures with a recovery action', async () => {
    const fixture = TestBed.createComponent(StatePanelComponent);
    fixture.componentRef.setInput('state', 'permission-denied');
    fixture.componentRef.setInput('title', '沒有存取權限');
    fixture.componentRef.setInput('description', '請向資料擁有者申請權限。');
    fixture.componentRef.setInput('recoveryLabel', '返回列表');
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain(
      '請向資料擁有者申請權限。',
    );
    expect(
      fixture.nativeElement.querySelector('button')?.textContent.trim(),
    ).toBe('返回列表');
  });
});
