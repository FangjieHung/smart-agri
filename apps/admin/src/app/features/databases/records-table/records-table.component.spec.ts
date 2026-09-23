import { TestBed } from '@angular/core/testing';
import type { TrackedSubjectView } from '../../../core/domain/database.model';
import { provideDatabaseTesting } from '../databases.testing';
import { RecordsTableComponent } from './records-table.component';

function subject(index: number): TrackedSubjectView {
  const result = provideDatabaseTesting().repository.getDatabaseTracking('account-smb-admin', 'database-customer-records');
  if (result.status !== 'ready') throw new Error('expected ready');
  return result.data.subjects[index];
}

function render(value: TrackedSubjectView) {
  TestBed.configureTestingModule({ imports: [RecordsTableComponent] });
  const fixture = TestBed.createComponent(RecordsTableComponent);
  fixture.componentRef.setInput('subject', value);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('RecordsTableComponent', () => {
  it('shows the subject’s records as a newest-first timeline', () => {
    const host = render(subject(0));
    const timeline = host.querySelector('ol.timeline');
    const items = Array.from(host.querySelectorAll('ol.timeline > li'));

    expect(timeline?.getAttribute('aria-label')).toBe('王小姐的紀錄時間軸');
    expect(items).toHaveLength(4);
    expect(items[0].querySelector('time')?.getAttribute('datetime')).toBe('2026-09-15T02:00:00.000Z');
    expect(items[0].textContent).toContain('2026-09-15');
    expect(items[0].textContent).toContain('本次');
    expect(items[1].textContent).toContain('上次');
    expect(items[3].textContent).toContain('首次');
    expect(items[0].textContent).toContain('3,200 元');
    expect(items[1].textContent).toContain('助理對話');
    expect(items[0].textContent).toContain('表單連結');
  });

  it('marks a single record as both the current and the first one', () => {
    const items = Array.from(render(subject(2)).querySelectorAll('ol.timeline > li'));

    expect(items).toHaveLength(1);
    expect(items[0].textContent).toContain('本次');
    expect(items[0].textContent).not.toContain('上次');
  });

  it('keeps a contentless trace of the records the subject withdrew', () => {
    const host = render(subject(1));
    const traces = Array.from(host.querySelectorAll('.withdrawn-list > li'));

    expect(host.querySelector('.withdrawn')?.textContent).toContain('已撤回');
    expect(traces).toHaveLength(1);
    expect(traces[0].textContent).toContain('2026-09-18');
    expect(traces[0].textContent).toContain('內容已移除');
    // 撤回的紀錄不會出現在時間軸，內容也不再出現在任何地方。
    expect(host.querySelectorAll('ol.timeline > li')).toHaveLength(2);
    expect(host.textContent).not.toContain('9,999');
  });
});
