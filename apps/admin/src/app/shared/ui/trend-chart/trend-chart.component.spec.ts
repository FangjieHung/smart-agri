import { TestBed } from '@angular/core/testing';
import { TrendChartComponent, type TrendChartPoint } from './trend-chart.component';

const points: readonly TrendChartPoint[] = [
  { label: '2026-06-15', value: 3, display: '3 / 5' },
  { label: '2026-07-15', value: 4, display: '4 / 5' },
  { label: '2026-09-15', value: 5, display: '5 / 5' },
];

function render() {
  TestBed.configureTestingModule({ imports: [TrendChartComponent] });
  const fixture = TestBed.createComponent(TrendChartComponent);
  fixture.componentRef.setInput('title', '整體滿意度');
  fixture.componentRef.setInput('points', points);
  fixture.componentRef.setInput('min', 1);
  fixture.componentRef.setInput('max', 5);
  fixture.componentRef.setInput('summary', '整體滿意度：本次 5 / 5，較上次 +1 分。');
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('TrendChartComponent', () => {
  it('draws an accessible SVG whose name and description come from the title and summary', () => {
    const host = render();
    const svg = host.querySelector('svg[role="img"]');
    const [titleId, descId] = (svg?.getAttribute('aria-labelledby') ?? '').split(' ');

    expect(host.querySelector(`#${titleId}`)?.textContent).toContain('整體滿意度');
    expect(host.querySelector(`#${descId}`)?.textContent).toContain('本次 5 / 5');
    expect(svg?.querySelectorAll('circle').length).toBe(3);
    expect(svg?.querySelector('polyline')?.getAttribute('points')?.split(' ')).toHaveLength(3);
  });

  it('places higher values higher on the chart', () => {
    const circles = Array.from(render().querySelectorAll('circle'));
    const ys = circles.map((circle) => Number(circle.getAttribute('cy')));

    expect(ys[0]).toBeGreaterThan(ys[1]);
    expect(ys[1]).toBeGreaterThan(ys[2]);
  });

  it('provides an equivalent text summary and data table', () => {
    const host = render();

    expect(host.querySelector('figcaption')?.textContent).toContain('較上次 +1 分');
    expect(host.querySelector('table caption')?.textContent).toContain('整體滿意度');
    const rows = Array.from(host.querySelectorAll('tbody tr')).map((row) =>
      Array.from(row.children, (cell) => cell.textContent?.trim()).join(' '),
    );
    expect(rows).toEqual(['2026-06-15 3 / 5', '2026-07-15 4 / 5', '2026-09-15 5 / 5']);
  });
});
