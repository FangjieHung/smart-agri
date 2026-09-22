import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export interface TrendChartPoint {
  readonly label: string;
  readonly value: number;
  /** 已格式化的顯示值，例如「3 / 5」或「1,200 元」。 */
  readonly display: string;
}

const WIDTH = 320;
const HEIGHT = 168;
const PAD = { top: 16, right: 20, bottom: 30, left: 44 } as const;

let nextId = 0;

/**
 * 不依賴第三方圖表套件的簡單折線圖。只負責把傳入的點畫出來，
 * 並提供等價的文字摘要與資料表；不計算任何業務差異。
 */
@Component({
  selector: 'app-trend-chart',
  templateUrl: './trend-chart.component.html',
  styleUrl: './trend-chart.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TrendChartComponent {
  readonly title = input.required<string>();
  readonly points = input.required<readonly TrendChartPoint[]>();
  readonly min = input.required<number>();
  readonly max = input.required<number>();
  /** 與圖表等價的文字摘要，由資料來源預先產生。 */
  readonly summary = input.required<string>();

  protected readonly id = `trend-chart-${nextId++}`;
  protected readonly width = WIDTH;
  protected readonly height = HEIGHT;
  protected readonly pad = PAD;

  protected readonly plotted = computed(() => {
    const points = this.points();
    const min = this.min();
    const max = this.max();
    const plotWidth = WIDTH - PAD.left - PAD.right;
    const plotHeight = HEIGHT - PAD.top - PAD.bottom;
    const step = points.length > 1 ? plotWidth / (points.length - 1) : 0;

    return points.map((point, index) => ({
      ...point,
      x: points.length > 1 ? PAD.left + step * index : PAD.left + plotWidth / 2,
      y:
        max > min
          ? PAD.top + ((max - point.value) / (max - min)) * plotHeight
          : PAD.top + plotHeight / 2,
    }));
  });

  protected readonly line = computed(() =>
    this.plotted()
      .map((point) => `${point.x.toFixed(1)},${point.y.toFixed(1)}`)
      .join(' '),
  );
}
