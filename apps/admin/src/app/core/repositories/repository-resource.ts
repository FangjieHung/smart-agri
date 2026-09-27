import { computed, type Signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import {
  catchError,
  defer,
  EMPTY,
  expand,
  filter,
  fromEvent,
  of,
  switchMap,
  take,
  timer,
  type Observable,
} from 'rxjs';
import type { RepositoryView } from './demo-repository';

/**
 * 畫面要分開顯示的四種狀態（加上 `ready`／`partial-failure` 的資料）：
 * - `loading`：還沒有工作階段，或第一次讀取尚未回來；
 * - `error`：讀取失敗（5xx、連線中斷）——**不是**「沒有資料」；
 * - `permission-denied`：repository 回傳的正常結果；
 * - 真正的空白：`ready` 且清單長度為 0，由畫面自己判斷。
 */
export type LoadedView<T> = RepositoryView<T> | { readonly status: 'error' };

export interface RepositoryResource<T> {
  readonly view: Signal<LoadedView<T>>;
  /** 重新讀取；讀取期間保留上一份資料，不會整塊閃回載入中。 */
  reload(): void;
}

/**
 * 非同步 repository 方法的讀取模式（M2 起各功能區照做，第一個是團隊設定）：
 *
 * ```ts
 * private readonly list = repositoryResource({
 *   params: () => this.session.activeAccountId() ?? undefined,
 *   stream: () => this.repository.listKnowledgeBaseSummaries(),
 * });
 * protected readonly view = this.list.view;
 * ```
 *
 * `params` 回傳 undefined 時不讀取（停在 loading）；值改變（換身分、換網址 id）就重新
 * 讀取並取消上一個請求。寫入成功後呼叫 `reload()`。必須在注入環境中呼叫（欄位初始值）。
 */
export function repositoryResource<T, P>(options: {
  readonly params: () => P | undefined;
  readonly stream: (params: P) => Observable<RepositoryView<T>>;
}): RepositoryResource<T> {
  const resource = rxResource<RepositoryView<T>, P | undefined>({
    params: options.params,
    // `params` 是 undefined 時 resource 停在 idle，不會呼叫 stream。
    stream: ({ params }) => options.stream(params as P),
    defaultValue: { status: 'loading' },
  });

  return {
    view: computed<LoadedView<T>>(() => {
      if (resource.hasValue()) return resource.value();
      return resource.error() === undefined ? { status: 'loading' } : { status: 'error' };
    }),
    reload: () => {
      resource.reload();
    },
  };
}

/** 分頁可見時立即完成，否則等到下一次變成可見。 */
export function whenDocumentVisible(document: Document): Observable<unknown> {
  return defer(() =>
    document.visibilityState === 'visible'
      ? of(null)
      : fromEvent(document, 'visibilitychange').pipe(
          filter(() => document.visibilityState === 'visible'),
          take(1),
        ),
  );
}

/**
 * 先讀一次，之後只要 `shouldPoll(最新結果)` 為真，就每隔 `intervalMs` 再讀一次；分頁隱藏
 * 時暫停，回到分頁立刻補讀。取消訂閱（元件銷毀、`params` 改變、`reload()`）就停止。
 *
 * 第一次讀取失敗照常拋出（畫面顯示錯誤）；**之後**的輪詢失敗只是暫時看不到進度，
 * 沿用上一份結果、下一輪再試，不把整頁換成錯誤狀態。
 */
export function pollWhile<T>(
  read: () => Observable<T>,
  shouldPoll: (value: T) => boolean,
  options: { readonly intervalMs: number; readonly document: Document },
): Observable<T> {
  return read().pipe(
    expand((previous) =>
      shouldPoll(previous)
        ? timer(options.intervalMs).pipe(
            switchMap(() => whenDocumentVisible(options.document)),
            take(1),
            switchMap(() => read().pipe(catchError(() => of(previous)))),
          )
        : EMPTY,
    ),
  );
}
