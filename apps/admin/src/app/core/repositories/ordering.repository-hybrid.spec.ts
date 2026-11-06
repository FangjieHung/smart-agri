import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import { DemoSessionService } from '../session/demo-session.service';
import {
  API_ORDERING_ASAP_BAND_PATH,
  API_ORDERING_ITEMS_PATH,
  API_ORDERING_ORDERS_PATH,
  API_ORDERING_PAUSE_PATH,
  API_ORDERING_SUMMARY_PATH,
  API_ORDERING_TABLES_PATH,
  apiOrderingActionPath,
  apiOrderingCloseTablePath,
  apiOrderingItemStatePath,
  OrderingRepository,
} from './ordering.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-11-07 (about 03:39 Taipei) from the real API (issue #402): `dotnet run` of this branch on port 5301 against a
// throw-away PostgreSQL database `ordering402adm` (migrated with the development seed `anxin`, then
// `ordering-shop import --organization anxin` of `deploy/ordering-shop.example.json` with the dine-in hours overridden to
// 00:00–23:59 because it was recorded at night), signed in as `admin` and `customer` through /api/v1/auth/login +
// /connect/authorize + /connect/token, by `scratchpad/ordering/402rec/record_adm.py`. The #399 sequence: two takeout orders
// were submitted through the public API (陳小姐 and 林先生, both for 12:00) between `summary-empty` and `summary-pending`;
// `accept-409` repeats `accept-200` with the same version. Then the shop was unpaused and one delivery order (王太太, zone
// 新營區其他里, #401) was submitted (`orders-pending-delivery`), accepted (`delivery-accept-200`) and marked ready
// (`delivery-ready-200`). Then (#402) two dine-in orders were sent from table A1's QR code (#004 王先生 with 不要香菜, #005
// without a name), which opened one table session (`orders-pending-dinein`, `summary-tables`, `tables-200`); 〔已結帳〕 was
// refused while both were pending (`close-409-pending`), #004 (`dinein-accept-200`, no promisedAt) and #005 were accepted
// (`tables-ready-to-close`), a close with the version before was refused (`close-409-changed`), the close with the current
// version succeeded (`close-200`), and the tables list and summary were read again (`tables-empty`, `summary-closed`).
// The bodies are pasted unchanged; never edit them to match the types.
/** summary-empty: HTTP 200. */
const SUMMARY_EMPTY_JSON = `{"now":"2026-11-06T19:39:03.622548+00:00","businessDate":"2026-11-07","timeZone":"Asia/Taipei","shopName":"王記麵店","slug":"wangji-noodles","pendingCount":0,"activeCount":0,"latestOrderAt":null,"changedAt":null,"paused":false,"asapBand":"15-30","asap":{"minMinutes":15,"maxMinutes":30},"services":{"takeoutScheduled":true,"takeoutAsap":true,"delivery":true,"deliveryAsap":true,"dineIn":true},"menuVersion":1,"shopVersion":1,"openTableCount":0}`;
/** summary-pending: HTTP 200. */
const SUMMARY_PENDING_JSON = `{"now":"2026-11-06T19:39:03.767877+00:00","businessDate":"2026-11-07","timeZone":"Asia/Taipei","shopName":"王記麵店","slug":"wangji-noodles","pendingCount":2,"activeCount":0,"latestOrderAt":"2026-11-06T19:39:03.756884+00:00","changedAt":"2026-11-06T19:39:03.756884+00:00","paused":false,"asapBand":"15-30","asap":{"minMinutes":15,"maxMinutes":30},"services":{"takeoutScheduled":true,"takeoutAsap":true,"delivery":true,"deliveryAsap":true,"dineIn":true},"menuVersion":1,"shopVersion":1,"openTableCount":0}`;
/** orders-pending: HTTP 200. */
const ORDERS_PENDING_JSON = `{"now":"2026-11-06T19:39:03.776796+00:00","view":"pending","orders":[{"id":"01a1b25f-2a8a-7ad3-8f16-9e0753467188","number":1,"displayNumber":"#001","status":"pending","version":1,"serviceType":"takeout-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":null,"customerName":"陳小姐","customerPhone":"0912345678","customerPhoneDisplay":"0912-345-678","customerEmail":"chen@example.com","lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"},{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":"到店門口會打電話","tableware":true,"subtotal":340,"total":340,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.65668+00:00","statusChangedAt":"2026-11-06T19:39:03.65668+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":null,"autoAccepted":false},{"id":"01a1b25f-2ad2-7e6f-8301-455d48444457","number":2,"displayNumber":"#002","status":"pending","version":1,"serviceType":"takeout-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":null,"customerName":"林先生","customerPhone":"0933222111","customerPhoneDisplay":"0933-222-111","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":false,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.756884+00:00","statusChangedAt":"2026-11-06T19:39:03.756884+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":null,"autoAccepted":false}]}`;
/** accept-200: HTTP 200. */
const ACCEPT_200_JSON = `{"id":"01a1b25f-2a8a-7ad3-8f16-9e0753467188","number":1,"displayNumber":"#001","status":"accepted","version":2,"serviceType":"takeout-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":"2026-11-07T04:00:00+00:00","customerName":"陳小姐","customerPhone":"0912345678","customerPhoneDisplay":"0912-345-678","customerEmail":"chen@example.com","lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"},{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":"到店門口會打電話","tableware":true,"subtotal":340,"total":340,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.65668+00:00","statusChangedAt":"2026-11-06T19:39:03.798686+00:00","accepted":{"at":"2026-11-06T19:39:03.798686+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":null,"autoAccepted":false}`;
/** accept-409: HTTP 409. */
const ACCEPT_409_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"order-changed","message":"這筆訂單剛剛被更新了，請確認目前的狀態。","order":{"id":"01a1b25f-2a8a-7ad3-8f16-9e0753467188","number":1,"displayNumber":"#001","status":"accepted","version":2,"serviceType":"takeout-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":"2026-11-07T04:00:00+00:00","customerName":"陳小姐","customerPhone":"0912345678","customerPhoneDisplay":"0912-345-678","customerEmail":"chen@example.com","lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"},{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":"到店門口會打電話","tableware":true,"subtotal":340,"total":340,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.65668+00:00","statusChangedAt":"2026-11-06T19:39:03.798686+00:00","accepted":{"at":"2026-11-06T19:39:03.798686+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":null,"autoAccepted":false}}`;
/** decline-422: HTTP 422. */
const DECLINE_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"message":"選「其他」時請寫明原因。","errors":{"note":["選「其他」時請寫明原因。"]}}`;
/** decline-200: HTTP 200. */
const DECLINE_200_JSON = `{"id":"01a1b25f-2ad2-7e6f-8301-455d48444457","number":2,"displayNumber":"#002","status":"declined","version":2,"serviceType":"takeout-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":null,"customerName":"林先生","customerPhone":"0933222111","customerPhoneDisplay":"0933-222-111","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":false,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.756884+00:00","statusChangedAt":"2026-11-06T19:39:03.85665+00:00","accepted":null,"ready":null,"completed":null,"declined":{"at":"2026-11-06T19:39:03.85665+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"},"reason":"sold-out","note":"餛飩今天賣完了，抱歉！"},"cancelled":null,"actions":[],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":null,"autoAccepted":false}`;
/** orders-active: HTTP 200. */
const ORDERS_ACTIVE_JSON = `{"now":"2026-11-06T19:39:03.867099+00:00","view":"active","orders":[{"id":"01a1b25f-2a8a-7ad3-8f16-9e0753467188","number":1,"displayNumber":"#001","status":"accepted","version":2,"serviceType":"takeout-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":"2026-11-07T04:00:00+00:00","customerName":"陳小姐","customerPhone":"0912345678","customerPhoneDisplay":"0912-345-678","customerEmail":"chen@example.com","lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"},{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":"到店門口會打電話","tableware":true,"subtotal":340,"total":340,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.65668+00:00","statusChangedAt":"2026-11-06T19:39:03.798686+00:00","accepted":{"at":"2026-11-06T19:39:03.798686+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":null,"autoAccepted":false}]}`;
/** orders-done: HTTP 200. */
const ORDERS_DONE_JSON = `{"now":"2026-11-06T19:39:03.878694+00:00","view":"done","orders":[{"id":"01a1b25f-2ad2-7e6f-8301-455d48444457","number":2,"displayNumber":"#002","status":"declined","version":2,"serviceType":"takeout-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":null,"customerName":"林先生","customerPhone":"0933222111","customerPhoneDisplay":"0933-222-111","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":false,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.756884+00:00","statusChangedAt":"2026-11-06T19:39:03.85665+00:00","accepted":null,"ready":null,"completed":null,"declined":{"at":"2026-11-06T19:39:03.85665+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"},"reason":"sold-out","note":"餛飩今天賣完了，抱歉！"},"cancelled":null,"actions":[],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":null,"autoAccepted":false}]}`;
/** pause-200: HTTP 200. */
const PAUSE_200_JSON = `{"now":"2026-11-06T19:39:03.903997+00:00","businessDate":"2026-11-07","timeZone":"Asia/Taipei","shopName":"王記麵店","slug":"wangji-noodles","pendingCount":0,"activeCount":1,"latestOrderAt":"2026-11-06T19:39:03.756884+00:00","changedAt":"2026-11-06T19:39:03.85665+00:00","paused":true,"asapBand":"15-30","asap":{"minMinutes":15,"maxMinutes":30},"services":{"takeoutScheduled":true,"takeoutAsap":true,"delivery":true,"deliveryAsap":true,"dineIn":true},"menuVersion":1,"shopVersion":2,"openTableCount":0}`;
/** asap-band-200: HTTP 200. */
const ASAP_BAND_200_JSON = `{"now":"2026-11-06T19:39:03.914941+00:00","businessDate":"2026-11-07","timeZone":"Asia/Taipei","shopName":"王記麵店","slug":"wangji-noodles","pendingCount":0,"activeCount":1,"latestOrderAt":"2026-11-06T19:39:03.756884+00:00","changedAt":"2026-11-06T19:39:03.85665+00:00","paused":true,"asapBand":"30-45","asap":{"minMinutes":30,"maxMinutes":45},"services":{"takeoutScheduled":true,"takeoutAsap":true,"delivery":true,"deliveryAsap":true,"dineIn":true},"menuVersion":1,"shopVersion":3,"openTableCount":0}`;
/** items-200: HTTP 200. */
const ITEMS_200_JSON = `{"now":"2026-11-06T19:39:03.922649+00:00","menuVersion":1,"categories":[{"id":"noodles","name":"麵食","items":[{"id":"beef-noodle","name":"紅燒牛肉麵","price":150,"availability":"available","resetsAt":null},{"id":"zhajiang","name":"炸醬麵","price":80,"availability":"available","resetsAt":null},{"id":"wonton-soup","name":"鮮肉餛飩湯","price":70,"availability":"available","resetsAt":null}]},{"id":"sides","name":"小菜","items":[{"id":"cucumber","name":"涼拌小黃瓜","price":40,"availability":"available","resetsAt":null},{"id":"braised-platter","name":"滷味拼盤","price":90,"availability":"available","resetsAt":null}]},{"id":"lunch-special","name":"平日午間限定","items":[{"id":"lunch-set","name":"午間乾麵套餐","price":120,"availability":"available","resetsAt":null}]}]}`;
/** item-sold-out-200: HTTP 200. */
const ITEM_SOLD_OUT_200_JSON = `{"id":"zhajiang","name":"炸醬麵","price":80,"availability":"sold-out-today","resetsAt":"2026-11-08T03:00:00+00:00"}`;
/** item-not-found-404: HTTP 404. */
const ITEM_NOT_FOUND_404_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"reason":"item-not-found","message":"目前的菜單沒有這個品項，請重新整理。"}`;
/** customer-summary-403: HTTP 403. */
const CUSTOMER_SUMMARY_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"ordering","message":"只有可處理線上點餐的帳號可以查看或處理訂單，或這筆訂單已不存在。"}`;
/** summary-after: HTTP 200. */
const SUMMARY_AFTER_JSON = `{"now":"2026-11-06T19:39:03.955491+00:00","businessDate":"2026-11-07","timeZone":"Asia/Taipei","shopName":"王記麵店","slug":"wangji-noodles","pendingCount":0,"activeCount":1,"latestOrderAt":"2026-11-06T19:39:03.756884+00:00","changedAt":"2026-11-06T19:39:03.85665+00:00","paused":true,"asapBand":"30-45","asap":{"minMinutes":30,"maxMinutes":45},"services":{"takeoutScheduled":true,"takeoutAsap":true,"delivery":true,"deliveryAsap":true,"dineIn":true},"menuVersion":1,"shopVersion":4,"openTableCount":0}`;
/** orders-pending-delivery: HTTP 200. */
const ORDERS_PENDING_DELIVERY_JSON = `{"now":"2026-11-06T19:39:03.982875+00:00","view":"pending","orders":[{"id":"01a1b25f-2ba8-77dc-be20-7a25fa1fc5d4","number":3,"displayNumber":"#003","status":"pending","version":1,"serviceType":"delivery-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":null,"customerName":"王太太","customerPhone":"0955123456","customerPhoneDisplay":"0955-123-456","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"},{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":false,"subtotal":340,"total":370,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.972143+00:00","statusChangedAt":"2026-11-06T19:39:03.972143+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":30,"delivery":{"zoneId":"xinying","zoneName":"新營區其他里","fee":30,"address":"台南市新營區中山路 1 號 3 樓","note":"請按電鈴"},"table":null,"autoAccepted":false}]}`;
/** delivery-accept-200: HTTP 200. */
const DELIVERY_ACCEPT_200_JSON = `{"id":"01a1b25f-2ba8-77dc-be20-7a25fa1fc5d4","number":3,"displayNumber":"#003","status":"accepted","version":2,"serviceType":"delivery-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":"2026-11-07T04:00:00+00:00","customerName":"王太太","customerPhone":"0955123456","customerPhoneDisplay":"0955-123-456","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"},{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":false,"subtotal":340,"total":370,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.972143+00:00","statusChangedAt":"2026-11-06T19:39:03.993543+00:00","accepted":{"at":"2026-11-06T19:39:03.993543+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":30,"delivery":{"zoneId":"xinying","zoneName":"新營區其他里","fee":30,"address":"台南市新營區中山路 1 號 3 樓","note":"請按電鈴"},"table":null,"autoAccepted":false}`;
/** delivery-ready-200: HTTP 200. */
const DELIVERY_READY_200_JSON = `{"id":"01a1b25f-2ba8-77dc-be20-7a25fa1fc5d4","number":3,"displayNumber":"#003","status":"ready","version":3,"serviceType":"delivery-scheduled","businessDate":"2026-11-07","pickupAt":"2026-11-07T04:00:00+00:00","estimate":null,"promisedAt":"2026-11-07T04:00:00+00:00","customerName":"王太太","customerPhone":"0955123456","customerPhoneDisplay":"0955-123-456","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"},{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":false,"subtotal":340,"total":370,"menuVersion":1,"createdAt":"2026-11-06T19:39:03.972143+00:00","statusChangedAt":"2026-11-06T19:39:04.001363+00:00","accepted":{"at":"2026-11-06T19:39:03.993543+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":{"at":"2026-11-06T19:39:04.001363+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"completed":null,"declined":null,"cancelled":null,"actions":["complete","cancel"],"expiresAt":"2026-11-07T03:15:00+00:00","personalDataClearedAt":null,"deliveryFee":30,"delivery":{"zoneId":"xinying","zoneName":"新營區其他里","fee":30,"address":"台南市新營區中山路 1 號 3 樓","note":"請按電鈴"},"table":null,"autoAccepted":false}`;
/** orders-pending-dinein: HTTP 200. */
const ORDERS_PENDING_DINEIN_JSON = `{"now":"2026-11-06T19:39:04.125976+00:00","view":"pending","orders":[{"id":"01a1b25f-2c2c-7eda-939e-bb0ec80a50e2","number":4,"displayNumber":"#004","status":"pending","version":1,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.094055+00:00","estimate":null,"promisedAt":null,"customerName":"王先生","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"}],"orderComment":"不要香菜","tableware":null,"subtotal":200,"total":200,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.094055+00:00","statusChangedAt":"2026-11-06T19:39:04.094055+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-06T19:54:04.094055+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false},{"id":"01a1b25f-2c38-77a1-921c-60b5b9973ec8","number":5,"displayNumber":"#005","status":"pending","version":1,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.116076+00:00","estimate":null,"promisedAt":null,"customerName":"","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":null,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.116076+00:00","statusChangedAt":"2026-11-06T19:39:04.116076+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-06T19:54:04.116076+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false}]}`;
/** summary-tables: HTTP 200. */
const SUMMARY_TABLES_JSON = `{"now":"2026-11-06T19:39:04.129432+00:00","businessDate":"2026-11-07","timeZone":"Asia/Taipei","shopName":"王記麵店","slug":"wangji-noodles","pendingCount":2,"activeCount":2,"latestOrderAt":"2026-11-06T19:39:04.116076+00:00","changedAt":"2026-11-06T19:39:04.116076+00:00","paused":false,"asapBand":"30-45","asap":{"minMinutes":30,"maxMinutes":45},"services":{"takeoutScheduled":true,"takeoutAsap":true,"delivery":true,"deliveryAsap":true,"dineIn":true},"menuVersion":1,"shopVersion":5,"openTableCount":1}`;
/** tables-200: HTTP 200. */
const TABLES_200_JSON = `{"now":"2026-11-06T19:39:04.137193+00:00","sessions":[{"id":"01a1b25f-2c28-7c00-b0d1-3643cde52302","version":2,"tableId":"a1","tableName":"A1","openedAt":"2026-11-06T19:39:04.094055+00:00","total":340,"pendingCount":2,"orders":[{"id":"01a1b25f-2c2c-7eda-939e-bb0ec80a50e2","number":4,"displayNumber":"#004","status":"pending","version":1,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.094055+00:00","estimate":null,"promisedAt":null,"customerName":"王先生","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"}],"orderComment":"不要香菜","tableware":null,"subtotal":200,"total":200,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.094055+00:00","statusChangedAt":"2026-11-06T19:39:04.094055+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-06T19:54:04.094055+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false},{"id":"01a1b25f-2c38-77a1-921c-60b5b9973ec8","number":5,"displayNumber":"#005","status":"pending","version":1,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.116076+00:00","estimate":null,"promisedAt":null,"customerName":"","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":null,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.116076+00:00","statusChangedAt":"2026-11-06T19:39:04.116076+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-06T19:54:04.116076+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false}],"closedAt":null,"closedBy":null}]}`;
/** close-409-pending: HTTP 409. */
const CLOSE_409_PENDING_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"table-has-pending","message":"這桌還有待確認的訂單，請先接單或婉拒再結帳。","session":{"id":"01a1b25f-2c28-7c00-b0d1-3643cde52302","version":2,"tableId":"a1","tableName":"A1","openedAt":"2026-11-06T19:39:04.094055+00:00","total":340,"pendingCount":2,"orders":[{"id":"01a1b25f-2c2c-7eda-939e-bb0ec80a50e2","number":4,"displayNumber":"#004","status":"pending","version":1,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.094055+00:00","estimate":null,"promisedAt":null,"customerName":"王先生","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"}],"orderComment":"不要香菜","tableware":null,"subtotal":200,"total":200,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.094055+00:00","statusChangedAt":"2026-11-06T19:39:04.094055+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-06T19:54:04.094055+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false},{"id":"01a1b25f-2c38-77a1-921c-60b5b9973ec8","number":5,"displayNumber":"#005","status":"pending","version":1,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.116076+00:00","estimate":null,"promisedAt":null,"customerName":"","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":null,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.116076+00:00","statusChangedAt":"2026-11-06T19:39:04.116076+00:00","accepted":null,"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["accept","decline"],"expiresAt":"2026-11-06T19:54:04.116076+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false}],"closedAt":null,"closedBy":null}}`;
/** dinein-accept-200: HTTP 200. */
const DINEIN_ACCEPT_200_JSON = `{"id":"01a1b25f-2c2c-7eda-939e-bb0ec80a50e2","number":4,"displayNumber":"#004","status":"accepted","version":2,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.094055+00:00","estimate":null,"promisedAt":"2026-11-06T19:39:04.094055+00:00","customerName":"王先生","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"}],"orderComment":"不要香菜","tableware":null,"subtotal":200,"total":200,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.094055+00:00","statusChangedAt":"2026-11-06T19:39:04.16843+00:00","accepted":{"at":"2026-11-06T19:39:04.16843+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-06T19:54:04.094055+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false}`;
/** tables-ready-to-close: HTTP 200. */
const TABLES_READY_TO_CLOSE_JSON = `{"now":"2026-11-06T19:39:04.181465+00:00","sessions":[{"id":"01a1b25f-2c28-7c00-b0d1-3643cde52302","version":2,"tableId":"a1","tableName":"A1","openedAt":"2026-11-06T19:39:04.094055+00:00","total":340,"pendingCount":0,"orders":[{"id":"01a1b25f-2c2c-7eda-939e-bb0ec80a50e2","number":4,"displayNumber":"#004","status":"accepted","version":2,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.094055+00:00","estimate":null,"promisedAt":"2026-11-06T19:39:04.094055+00:00","customerName":"王先生","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"}],"orderComment":"不要香菜","tableware":null,"subtotal":200,"total":200,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.094055+00:00","statusChangedAt":"2026-11-06T19:39:04.16843+00:00","accepted":{"at":"2026-11-06T19:39:04.16843+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-06T19:54:04.094055+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false},{"id":"01a1b25f-2c38-77a1-921c-60b5b9973ec8","number":5,"displayNumber":"#005","status":"accepted","version":2,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.116076+00:00","estimate":null,"promisedAt":"2026-11-06T19:39:04.116076+00:00","customerName":"","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":null,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.116076+00:00","statusChangedAt":"2026-11-06T19:39:04.175177+00:00","accepted":{"at":"2026-11-06T19:39:04.175177+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-06T19:54:04.116076+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false}],"closedAt":null,"closedBy":null}]}`;
/** close-409-changed: HTTP 409. */
const CLOSE_409_CHANGED_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"session-changed","message":"這桌剛剛有新的變動，請確認金額後再結帳。","session":{"id":"01a1b25f-2c28-7c00-b0d1-3643cde52302","version":2,"tableId":"a1","tableName":"A1","openedAt":"2026-11-06T19:39:04.094055+00:00","total":340,"pendingCount":0,"orders":[{"id":"01a1b25f-2c2c-7eda-939e-bb0ec80a50e2","number":4,"displayNumber":"#004","status":"accepted","version":2,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.094055+00:00","estimate":null,"promisedAt":"2026-11-06T19:39:04.094055+00:00","customerName":"王先生","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"}],"orderComment":"不要香菜","tableware":null,"subtotal":200,"total":200,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.094055+00:00","statusChangedAt":"2026-11-06T19:39:04.16843+00:00","accepted":{"at":"2026-11-06T19:39:04.16843+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-06T19:54:04.094055+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false},{"id":"01a1b25f-2c38-77a1-921c-60b5b9973ec8","number":5,"displayNumber":"#005","status":"accepted","version":2,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.116076+00:00","estimate":null,"promisedAt":"2026-11-06T19:39:04.116076+00:00","customerName":"","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":null,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.116076+00:00","statusChangedAt":"2026-11-06T19:39:04.175177+00:00","accepted":{"at":"2026-11-06T19:39:04.175177+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":null,"declined":null,"cancelled":null,"actions":["ready","complete","cancel"],"expiresAt":"2026-11-06T19:54:04.116076+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false}],"closedAt":null,"closedBy":null}}`;
/** close-200: HTTP 200. */
const CLOSE_200_JSON = `{"id":"01a1b25f-2c28-7c00-b0d1-3643cde52302","version":3,"tableId":"a1","tableName":"A1","openedAt":"2026-11-06T19:39:04.094055+00:00","total":340,"pendingCount":0,"orders":[{"id":"01a1b25f-2c2c-7eda-939e-bb0ec80a50e2","number":4,"displayNumber":"#004","status":"completed","version":3,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.094055+00:00","estimate":null,"promisedAt":"2026-11-06T19:39:04.094055+00:00","customerName":"王先生","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"beef-noodle","itemName":"紅燒牛肉麵","itemPrice":150,"quantity":1,"unitPrice":200,"lineTotal":200,"options":[{"groupId":"size","groupName":"份量","optionId":"large","optionName":"大碗","price":20,"count":1,"options":[]},{"groupId":"noodle","groupName":"麵條","optionId":"wide","optionName":"寬麵","price":0,"count":1,"options":[]},{"groupId":"toppings","groupName":"加料","optionId":"egg","optionName":"滷蛋","price":15,"count":2,"options":[]}],"comment":"不要香菜"}],"orderComment":"不要香菜","tableware":null,"subtotal":200,"total":200,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.094055+00:00","statusChangedAt":"2026-11-06T19:39:04.19284+00:00","accepted":{"at":"2026-11-06T19:39:04.16843+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":{"at":"2026-11-06T19:39:04.19284+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"declined":null,"cancelled":null,"actions":[],"expiresAt":"2026-11-06T19:54:04.094055+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false},{"id":"01a1b25f-2c38-77a1-921c-60b5b9973ec8","number":5,"displayNumber":"#005","status":"completed","version":3,"serviceType":"dine-in","businessDate":"2026-11-07","pickupAt":"2026-11-06T19:39:04.116076+00:00","estimate":null,"promisedAt":"2026-11-06T19:39:04.116076+00:00","customerName":"","customerPhone":"","customerPhoneDisplay":"","customerEmail":null,"lines":[{"itemId":"wonton-soup","itemName":"鮮肉餛飩湯","itemPrice":70,"quantity":2,"unitPrice":70,"lineTotal":140,"options":[],"comment":null}],"orderComment":null,"tableware":null,"subtotal":140,"total":140,"menuVersion":1,"createdAt":"2026-11-06T19:39:04.116076+00:00","statusChangedAt":"2026-11-06T19:39:04.19284+00:00","accepted":{"at":"2026-11-06T19:39:04.175177+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"ready":null,"completed":{"at":"2026-11-06T19:39:04.19284+00:00","by":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}},"declined":null,"cancelled":null,"actions":[],"expiresAt":"2026-11-06T19:54:04.116076+00:00","personalDataClearedAt":null,"deliveryFee":0,"delivery":null,"table":{"id":"a1","name":"A1","sessionId":"01a1b25f-2c28-7c00-b0d1-3643cde52302"},"autoAccepted":false}],"closedAt":"2026-11-06T19:39:04.19284+00:00","closedBy":{"id":"01a1b25d-131c-7627-b2dc-fb76d8ad0e62","displayName":"安心商行管理者"}}`;
/** tables-empty: HTTP 200. */
const TABLES_EMPTY_JSON = `{"now":"2026-11-06T19:39:04.20186+00:00","sessions":[]}`;
/** summary-closed: HTTP 200. */
const SUMMARY_CLOSED_JSON = `{"now":"2026-11-06T19:39:04.204508+00:00","businessDate":"2026-11-07","timeZone":"Asia/Taipei","shopName":"王記麵店","slug":"wangji-noodles","pendingCount":0,"activeCount":2,"latestOrderAt":"2026-11-06T19:39:04.116076+00:00","changedAt":"2026-11-06T19:39:04.19284+00:00","paused":false,"asapBand":"30-45","asap":{"minMinutes":30,"maxMinutes":45},"services":{"takeoutScheduled":true,"takeoutAsap":true,"delivery":true,"deliveryAsap":true,"dineIn":true},"menuVersion":1,"shopVersion":5,"openTableCount":0}`;

function apiRepository() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('ignored-in-api-mode') } },
    ],
  });
  return { repository: TestBed.inject(OrderingRepository), http: TestBed.inject(HttpTestingController) };
}

function fail(http: HttpTestingController, url: string, json: string, status: number, statusText: string): void {
  http.expectOne(url).flush(JSON.parse(json), { status, statusText });
}

describe('OrderingRepository (API mode, recorded responses, issues #399, #401 and #402)', () => {
  it('reads the summary before and after orders arrive', async () => {
    const { repository, http } = apiRepository();

    const empty = firstValueFrom(repository.summary());
    const request = http.expectOne(API_ORDERING_SUMMARY_PATH);
    expect(request.request.method).toBe('GET');
    request.flush(JSON.parse(SUMMARY_EMPTY_JSON));
    const before = await empty;
    expect(before.status === 'ready' && [before.data.pendingCount, before.data.latestOrderAt, before.data.asapBand]).toEqual([0, null, '15-30']);

    const pending = firstValueFrom(repository.summary());
    http.expectOne(API_ORDERING_SUMMARY_PATH).flush(JSON.parse(SUMMARY_PENDING_JSON));
    const after = await pending;
    expect(after.status).toBe('ready');
    if (after.status !== 'ready') return;
    expect(after.data.pendingCount).toBe(2);
    expect(after.data.shopName).toBe('王記麵店');
    expect(after.data.timeZone).toBe('Asia/Taipei');
    expect(after.data.services).toEqual({ takeoutScheduled: true, takeoutAsap: true, delivery: true, deliveryAsap: true, dineIn: true });
    expect(after.data.openTableCount).toBe(0);
    expect(Date.parse(after.data.latestOrderAt ?? '')).toBeGreaterThan(Date.parse(after.data.now) - 60_000);
  });

  it('lists each view with the lines, options and actions the cards show', async () => {
    const { repository, http } = apiRepository();

    const pending = firstValueFrom(repository.orders('pending'));
    const request = http.expectOne((candidate) => candidate.url === API_ORDERING_ORDERS_PATH);
    expect(request.request.params.get('view')).toBe('pending');
    request.flush(JSON.parse(ORDERS_PENDING_JSON));
    const view = await pending;
    expect(view.status).toBe('ready');
    if (view.status !== 'ready') return;
    const [chen, lin] = view.data.orders;
    expect([chen.displayNumber, chen.customerName, chen.customerPhone, chen.customerPhoneDisplay, chen.total, chen.actions])
      .toEqual(['#001', '陳小姐', '0912345678', '0912-345-678', 340, ['accept', 'decline']]);
    expect(chen.lines[0].options.map((option) => [option.optionName, option.count])).toEqual([['大碗', 1], ['寬麵', 1], ['滷蛋', 2]]);
    expect(chen.lines[0].comment).toBe('不要香菜');
    expect([lin.displayNumber, lin.customerEmail, lin.tableware]).toEqual(['#002', null, false]);
    // 外帶單：沒有運費，也沒有外送的資料（#401），不是內用（#402）。
    expect([chen.deliveryFee, chen.delivery, lin.deliveryFee, lin.delivery]).toEqual([0, null, 0, null]);
    expect([chen.table, chen.autoAccepted, lin.table, lin.autoAccepted]).toEqual([null, false, null, false]);

    const active = firstValueFrom(repository.orders('active'));
    http.expectOne((candidate) => candidate.url === API_ORDERING_ORDERS_PATH && candidate.params.get('view') === 'active').flush(JSON.parse(ORDERS_ACTIVE_JSON));
    const activeView = await active;
    expect(activeView.status === 'ready' && activeView.data.orders.map((order) => [order.displayNumber, order.status, order.actions]))
      .toEqual([['#001', 'accepted', ['ready', 'complete', 'cancel']]]);

    const done = firstValueFrom(repository.orders('done'));
    http.expectOne((candidate) => candidate.url === API_ORDERING_ORDERS_PATH && candidate.params.get('view') === 'done').flush(JSON.parse(ORDERS_DONE_JSON));
    const doneView = await done;
    expect(doneView.status === 'ready' && doneView.data.orders.map((order) => [order.displayNumber, order.status, order.declined?.reason, order.declined?.note]))
      .toEqual([['#002', 'declined', 'sold-out', '餛飩今天賣完了，抱歉！']]);
  });

  it('accepts with the version it saw, and a second accept from the same version is a 409 with the current order', async () => {
    const { repository, http } = apiRepository();
    const order = JSON.parse(ORDERS_PENDING_JSON).orders[0];

    const accepted = firstValueFrom(repository.act(order.id, { action: 'accept', version: 1, promisedAt: null }));
    const request = http.expectOne(apiOrderingActionPath(order.id, 'accept'));
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ version: 1, promisedAt: null });
    request.flush(JSON.parse(ACCEPT_200_JSON));
    const result = await accepted;
    expect(result.status === 'ready' && [result.data.status, result.data.version, result.data.accepted?.by?.displayName])
      .toEqual(['accepted', 2, '安心商行管理者']);

    const again = firstValueFrom(repository.act(order.id, { action: 'accept', version: 1, promisedAt: null }));
    fail(http, apiOrderingActionPath(order.id, 'accept'), ACCEPT_409_JSON, 409, 'Conflict');
    const conflict = await again;
    expect(conflict.status).toBe('conflict');
    if (conflict.status !== 'conflict') return;
    expect(conflict.reason).toBe('order-changed');
    expect(conflict.message).toBe('這筆訂單剛剛被更新了，請確認目前的狀態。');
    expect([conflict.order.id, conflict.order.status, conflict.order.version, conflict.order.actions]).toEqual([order.id, 'accepted', 2, ['ready', 'complete', 'cancel']]);
  });

  it('returns the 422 of a decline without a note for 其他, then declines with a reason and a note', async () => {
    const { repository, http } = apiRepository();
    const order = JSON.parse(ORDERS_PENDING_JSON).orders[1];

    const invalid = firstValueFrom(repository.act(order.id, { action: 'decline', version: 1, reason: 'other', note: null }));
    fail(http, apiOrderingActionPath(order.id, 'decline'), DECLINE_422_JSON, 422, 'Unprocessable Content');
    expect(await invalid).toEqual({ status: 'validation-failed', message: '選「其他」時請寫明原因。', errors: { note: ['選「其他」時請寫明原因。'] } });

    const declined = firstValueFrom(repository.act(order.id, { action: 'decline', version: 1, reason: 'sold-out', note: '餛飩今天賣完了，抱歉！' }));
    const request = http.expectOne(apiOrderingActionPath(order.id, 'decline'));
    expect(request.request.body).toEqual({ version: 1, reason: 'sold-out', note: '餛飩今天賣完了，抱歉！' });
    request.flush(JSON.parse(DECLINE_200_JSON));
    const result = await declined;
    expect(result.status === 'ready' && [result.data.status, result.data.actions]).toEqual(['declined', []]);
  });

  it('pauses, changes the ASAP band and reads back the summary', async () => {
    const { repository, http } = apiRepository();

    const paused = firstValueFrom(repository.setPaused(true));
    const pause = http.expectOne(API_ORDERING_PAUSE_PATH);
    expect([pause.request.method, pause.request.body]).toEqual(['PUT', { paused: true }]);
    pause.flush(JSON.parse(PAUSE_200_JSON));
    const pausedView = await paused;
    expect(pausedView.status === 'ready' && [pausedView.data.paused, pausedView.data.shopVersion]).toEqual([true, 2]);

    const band = firstValueFrom(repository.setAsapBand('30-45'));
    const put = http.expectOne(API_ORDERING_ASAP_BAND_PATH);
    expect(put.request.body).toEqual({ band: '30-45' });
    put.flush(JSON.parse(ASAP_BAND_200_JSON));
    const bandView = await band;
    expect(bandView.status === 'ready' && [bandView.data.asapBand, bandView.data.asap]).toEqual(['30-45', { minMinutes: 30, maxMinutes: 45 }]);

    const latest = firstValueFrom(repository.summary());
    http.expectOne(API_ORDERING_SUMMARY_PATH).flush(JSON.parse(SUMMARY_AFTER_JSON));
    const summary = await latest;
    expect(summary.status === 'ready' && [summary.data.pendingCount, summary.data.activeCount, summary.data.paused]).toEqual([0, 1, true]);
  });

  it('lists the items, marks one sold out for today until the next opening, and reports an unknown item', async () => {
    const { repository, http } = apiRepository();

    const items = firstValueFrom(repository.items());
    http.expectOne(API_ORDERING_ITEMS_PATH).flush(JSON.parse(ITEMS_200_JSON));
    const menu = await items;
    expect(menu.status === 'ready' && menu.data.categories.map((category) => [category.name, category.items.length]))
      .toEqual([['麵食', 3], ['小菜', 2], ['平日午間限定', 1]]);

    const soldOut = firstValueFrom(repository.setItemState('zhajiang', 'sold-out-today'));
    const put = http.expectOne(apiOrderingItemStatePath('zhajiang'));
    expect([put.request.method, put.request.body]).toEqual(['PUT', { state: 'sold-out-today' }]);
    put.flush(JSON.parse(ITEM_SOLD_OUT_200_JSON));
    expect(await soldOut).toEqual({
      status: 'ready',
      data: { id: 'zhajiang', name: '炸醬麵', price: 80, availability: 'sold-out-today', resetsAt: '2026-11-08T03:00:00+00:00' },
    });

    const unknown = firstValueFrom(repository.setItemState('no-such-item', 'unavailable'));
    fail(http, apiOrderingItemStatePath('no-such-item'), ITEM_NOT_FOUND_404_JSON, 404, 'Not Found');
    expect(await unknown).toEqual({ status: 'item-not-found', message: '目前的菜單沒有這個品項，請重新整理。' });
  });

  it('maps a delivery order (#401): zone, fee, address, note and deliveryFee, and ready keeps the ready status', async () => {
    const { repository, http } = apiRepository();

    const pending = firstValueFrom(repository.orders('pending'));
    http.expectOne((candidate) => candidate.url === API_ORDERING_ORDERS_PATH && candidate.params.get('view') === 'pending')
      .flush(JSON.parse(ORDERS_PENDING_DELIVERY_JSON));
    const view = await pending;
    expect(view.status).toBe('ready');
    if (view.status !== 'ready') return;
    const [wang] = view.data.orders;
    expect([wang.displayNumber, wang.serviceType, wang.customerName, wang.subtotal, wang.deliveryFee, wang.total])
      .toEqual(['#003', 'delivery-scheduled', '王太太', 340, 30, 370]);
    expect(wang.delivery).toEqual({ zoneId: 'xinying', zoneName: '新營區其他里', fee: 30, address: '台南市新營區中山路 1 號 3 樓', note: '請按電鈴' });

    const accepted = firstValueFrom(repository.act(wang.id, { action: 'accept', version: 1, promisedAt: null }));
    http.expectOne(apiOrderingActionPath(wang.id, 'accept')).flush(JSON.parse(DELIVERY_ACCEPT_200_JSON));
    const acceptedResult = await accepted;
    expect(acceptedResult.status === 'ready' && [acceptedResult.data.status, acceptedResult.data.version, acceptedResult.data.promisedAt, acceptedResult.data.actions])
      .toEqual(['accepted', 2, wang.pickupAt, ['ready', 'complete', 'cancel']]);

    const ready = firstValueFrom(repository.act(wang.id, { action: 'ready', version: 2 }));
    const request = http.expectOne(apiOrderingActionPath(wang.id, 'ready'));
    expect(request.request.body).toEqual({ version: 2 });
    request.flush(JSON.parse(DELIVERY_READY_200_JSON));
    const readyResult = await ready;
    expect(readyResult.status).toBe('ready');
    if (readyResult.status !== 'ready') return;
    // 外送單沒有新的狀態：「已出發」就是 ready（畫面上換說法）。
    expect([readyResult.data.status, readyResult.data.version, readyResult.data.actions, readyResult.data.ready?.by?.displayName])
      .toEqual(['ready', 3, ['complete', 'cancel'], '安心商行管理者']);
    expect([readyResult.data.deliveryFee, readyResult.data.delivery?.zoneName]).toEqual([30, '新營區其他里']);
  });

  it('maps dine-in orders (#402): table A1, an optional name, no phone or Email, and the open table session', async () => {
    const { repository, http } = apiRepository();

    const pending = firstValueFrom(repository.orders('pending'));
    http.expectOne((candidate) => candidate.url === API_ORDERING_ORDERS_PATH && candidate.params.get('view') === 'pending')
      .flush(JSON.parse(ORDERS_PENDING_DINEIN_JSON));
    const view = await pending;
    expect(view.status).toBe('ready');
    if (view.status !== 'ready') return;
    const [wang, nameless] = view.data.orders.filter((order) => order.serviceType === 'dine-in');
    expect([wang.displayNumber, wang.customerName, wang.customerPhone, wang.customerPhoneDisplay, wang.customerEmail, wang.total, wang.orderComment, wang.autoAccepted])
      .toEqual(['#004', '王先生', '', '', null, 200, '不要香菜', false]);
    expect([nameless.displayNumber, nameless.customerName, nameless.total]).toEqual(['#005', '', 140]);
    expect([wang.table?.id, wang.table?.name, wang.table?.sessionId === nameless.table?.sessionId]).toEqual(['a1', 'A1', true]);
    // 內用不選時間：取餐時間就是送出的時候。
    expect(wang.pickupAt).toBe(wang.createdAt);

    const summary = firstValueFrom(repository.summary());
    http.expectOne(API_ORDERING_SUMMARY_PATH).flush(JSON.parse(SUMMARY_TABLES_JSON));
    const summaryView = await summary;
    expect(summaryView.status === 'ready' && [summaryView.data.openTableCount, summaryView.data.pendingCount, summaryView.data.services.dineIn]).toEqual([1, 2, true]);

    const tables = firstValueFrom(repository.tables());
    const request = http.expectOne(API_ORDERING_TABLES_PATH);
    expect(request.request.method).toBe('GET');
    request.flush(JSON.parse(TABLES_200_JSON));
    const tablesView = await tables;
    expect(tablesView.status).toBe('ready');
    if (tablesView.status !== 'ready') return;
    const [a1] = tablesView.data.sessions;
    expect([tablesView.data.sessions.length, a1.tableName, a1.version, a1.total, a1.pendingCount, a1.closedAt, a1.orders.map((order) => order.displayNumber)])
      .toEqual([1, 'A1', 2, 340, 2, null, ['#004', '#005']]);
    expect(a1.id).toBe(wang.table?.sessionId);
  });

  it('refuses 〔已結帳〕 while an order is pending, accepts a dine-in order without a time, and closes the table (#402)', async () => {
    const { repository, http } = apiRepository();
    const [a1] = JSON.parse(TABLES_200_JSON).sessions;
    const wang = a1.orders[0];

    const refused = firstValueFrom(repository.closeTable(a1.id, 2));
    const close = http.expectOne(apiOrderingCloseTablePath(a1.id));
    expect([close.request.method, close.request.body]).toEqual(['POST', { version: 2 }]);
    close.flush(JSON.parse(CLOSE_409_PENDING_JSON), { status: 409, statusText: 'Conflict' });
    const pending = await refused;
    expect(pending.status).toBe('table-conflict');
    if (pending.status !== 'table-conflict') return;
    expect([pending.reason, pending.message, pending.session.pendingCount, pending.session.version])
      .toEqual(['table-has-pending', '這桌還有待確認的訂單，請先接單或婉拒再結帳。', 2, 2]);

    const accepted = firstValueFrom(repository.act(wang.id, { action: 'accept', version: 1 }));
    const accept = http.expectOne(apiOrderingActionPath(wang.id, 'accept'));
    // 內用單不帶 promisedAt（伺服器對內用單的任何時間都回 422）。
    expect(accept.request.body).toEqual({ version: 1 });
    accept.flush(JSON.parse(DINEIN_ACCEPT_200_JSON));
    const acceptedView = await accepted;
    expect(acceptedView.status === 'ready' && [acceptedView.data.status, acceptedView.data.promisedAt, acceptedView.data.autoAccepted, acceptedView.data.accepted?.by?.displayName])
      .toEqual(['accepted', wang.pickupAt, false, '安心商行管理者']);

    const ready = firstValueFrom(repository.tables());
    http.expectOne(API_ORDERING_TABLES_PATH).flush(JSON.parse(TABLES_READY_TO_CLOSE_JSON));
    const readyView = await ready;
    expect(readyView.status === 'ready' && readyView.data.sessions.map((session) => [session.version, session.pendingCount, session.total]))
      .toEqual([[2, 0, 340]]);

    const stale = firstValueFrom(repository.closeTable(a1.id, 1));
    fail(http, apiOrderingCloseTablePath(a1.id), CLOSE_409_CHANGED_JSON, 409, 'Conflict');
    const changed = await stale;
    expect(changed.status === 'table-conflict' && [changed.reason, changed.message, changed.session.version, changed.session.closedAt])
      .toEqual(['session-changed', '這桌剛剛有新的變動，請確認金額後再結帳。', 2, null]);

    const closing = firstValueFrom(repository.closeTable(a1.id, 2));
    http.expectOne(apiOrderingCloseTablePath(a1.id)).flush(JSON.parse(CLOSE_200_JSON));
    const closed = await closing;
    expect(closed.status).toBe('ready');
    if (closed.status !== 'ready') return;
    expect([closed.data.version, closed.data.total, closed.data.closedBy?.displayName, closed.data.closedAt !== null])
      .toEqual([3, 340, '安心商行管理者', true]);
    expect(closed.data.orders.map((order) => [order.displayNumber, order.status, order.actions])).toEqual([['#004', 'completed', []], ['#005', 'completed', []]]);

    const empty = firstValueFrom(repository.tables());
    http.expectOne(API_ORDERING_TABLES_PATH).flush(JSON.parse(TABLES_EMPTY_JSON));
    expect(await empty).toMatchObject({ status: 'ready', data: { sessions: [] } });
    const summary = firstValueFrom(repository.summary());
    http.expectOne(API_ORDERING_SUMMARY_PATH).flush(JSON.parse(SUMMARY_CLOSED_JSON));
    const summaryView = await summary;
    expect(summaryView.status === 'ready' && [summaryView.data.openTableCount, summaryView.data.changedAt]).toEqual([0, closed.data.closedAt]);
    http.verify();
  });

  it('is permission-denied for an account without manage-orders', async () => {
    const { repository, http } = apiRepository();
    const summary = firstValueFrom(repository.summary());
    fail(http, API_ORDERING_SUMMARY_PATH, CUSTOMER_SUMMARY_403_JSON, 403, 'Forbidden');
    expect(await summary).toEqual({
      status: 'permission-denied',
      reason: 'ordering',
      message: '只有可處理線上點餐的帳號可以查看或處理訂單，或這筆訂單已不存在。',
    });
  });

  it('is not-configured for an organization without ordering, and other failures are thrown', async () => {
    const { repository, http } = apiRepository();
    // Not recorded: the seed's only organization has ordering after the import. Same shape as `ApiErrors.WithReason`.
    const summary = firstValueFrom(repository.summary());
    http.expectOne(API_ORDERING_SUMMARY_PATH).flush(
      { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5', title: 'Not Found', status: 404, reason: 'ordering-not-configured', message: '這個組織還沒有設定線上點餐。' },
      { status: 404, statusText: 'Not Found' },
    );
    expect(await summary).toEqual({ status: 'not-configured', message: '這個組織還沒有設定線上點餐。' });

    const broken = firstValueFrom(repository.orders('pending'));
    http.expectOne((candidate) => candidate.url === API_ORDERING_ORDERS_PATH).flush('oops', { status: 500, statusText: 'Server Error' });
    await expect(broken).rejects.toMatchObject({ status: 500 });
    http.verify();
  });
});
