import { TestBed } from '@angular/core/testing';
import { EmptyStateComponent } from './empty-state.component';

describe('EmptyStateComponent', () => {
  it('accepts a primary action and emits it from a native button', async () => {
    const fixture = TestBed.createComponent(EmptyStateComponent);
    fixture.componentRef.setInput('title', '尚未建立知識庫');
    fixture.componentRef.setInput(
      'description',
      '加入公司資料，讓助理可以引用回答。',
    );
    fixture.componentRef.setInput('actionLabel', '建立知識庫');
    let activated = false;
    fixture.componentInstance.primaryAction.subscribe(() => {
      activated = true;
    });
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('h2')?.textContent).toBe('尚未建立知識庫');
    const button = element.querySelector('button') as HTMLButtonElement;
    expect(button.textContent?.trim()).toBe('建立知識庫');
    expect(button.type).toBe('button');
    button.click();
    expect(activated).toBe(true);
  });

  it('does not show an unlabeled action when none is provided', async () => {
    const fixture = TestBed.createComponent(EmptyStateComponent);
    fixture.componentRef.setInput('title', '尚無紀錄');
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });
});
