import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { describe, expect, it } from 'vitest';
import type { AccountId } from '../domain/account.model';
import type { CaseDetailView } from '../domain/case.model';
import { DemoSessionService } from '../session/demo-session.service';
import { API_CASES_PATH, apiCaseActionPath, apiCasePath, CasesRepository } from './cases.repository';
import { API_DEMO_REPOSITORY_FACTORY } from './tokens';

// Recorded 2026-10-07 from the real API (issue #249): `dotnet run` of this branch on port 5262 against a
// throw-away PostgreSQL database `m7_249` (migrated, development seed `anxin`), signed in as `admin`,
// `internal` and `customer` through /connect/authorize + /connect/token, by `scratchpad/249/record.py`.
// Setup through the API: 設備組 (member: internal), 採購組 (member: admin), type 設備故障報修. One case
// created by `internal` walks the action table: accept → request-info → resume → set-due → transfer to
// 採購組 → admin accept → admin request-info → internal's comment (resumes it) → admin complete; a second
// case is cancelled by its creator before acceptance. The bodies are pasted unchanged; never edit them
// to match the types.
/** internal-detail-pending: HTTP 200. */
const DETAIL_PENDING_JSON = `{"case":{"id":"01a11241-7710-7b9d-bef1-28903256d80a","title":"冷藏庫溫度降不下來","description":"二號冷藏庫從早上開始維持在 9 度。","status":"pending","origin":"manual","type":{"id":"01a11241-76fa-7fa8-8043-ac3007089a84","name":"設備故障報修"},"group":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"createdBy":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-09T17:27:22.630119+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T17:27:22.634534+00:00","updatedAt":"2026-10-06T17:27:22.634534+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a11241-7710-75c0-87ef-66c8ec5a8825","ordinal":1,"action":"created","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.634534+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"dueAt":"2026-10-09T17:27:22.630119+00:00"}],"links":{"record":null,"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":["accept","cancel","comment"],"cancelReasonRequired":false}`;
/** internal-accept-stale-409: HTTP 409. */
const ACCEPT_STALE_409_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"case-changed","message":"這件案件剛被其他人更新，請重新整理後再試。"}`;
/** internal-accept: HTTP 200. */
const ACCEPT_JSON = `{"case":{"id":"01a11241-7710-7b9d-bef1-28903256d80a","title":"冷藏庫溫度降不下來","description":"二號冷藏庫從早上開始維持在 9 度。","status":"in-progress","origin":"manual","type":{"id":"01a11241-76fa-7fa8-8043-ac3007089a84","name":"設備故障報修"},"group":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"createdBy":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"owner":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"dueAt":"2026-10-09T17:27:22.630119+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T17:27:22.634534+00:00","updatedAt":"2026-10-06T17:27:22.736894+00:00","acceptedAt":"2026-10-06T17:27:22.736894+00:00","completedAt":null,"cancelledAt":null,"eventCount":2},"events":[{"id":"01a11241-7710-75c0-87ef-66c8ec5a8825","ordinal":1,"action":"created","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.634534+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"dueAt":"2026-10-09T17:27:22.630119+00:00"},{"id":"01a11241-7771-765d-95cd-1c4b5777f4d6","ordinal":2,"action":"accepted","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.736894+00:00","note":null,"status":"in-progress","owner":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"fromGroup":null,"toGroup":null,"dueAt":null}],"links":{"record":null,"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":["request-info","complete","cancel","transfer","set-due","comment"],"cancelReasonRequired":true}`;
/** admin-complete-not-owner-403: HTTP 403. */
const COMPLETE_NOT_OWNER_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case-action","message":"你不能對這件案件執行這個動作。"}`;
/** internal-complete-blank-422: HTTP 422. */
const COMPLETE_BLANK_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"resolution-required","message":"請填寫處理結果。","errors":{"resolution":["請填寫處理結果。"]}}`;
/** internal-request-info: HTTP 200. */
const REQUEST_INFO_JSON = `{"case":{"id":"01a11241-7710-7b9d-bef1-28903256d80a","title":"冷藏庫溫度降不下來","description":"二號冷藏庫從早上開始維持在 9 度。","status":"awaiting-info","origin":"manual","type":{"id":"01a11241-76fa-7fa8-8043-ac3007089a84","name":"設備故障報修"},"group":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"createdBy":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"owner":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"dueAt":"2026-10-09T17:27:22.630119+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T17:27:22.634534+00:00","updatedAt":"2026-10-06T17:27:22.756202+00:00","acceptedAt":"2026-10-06T17:27:22.736894+00:00","completedAt":null,"cancelledAt":null,"eventCount":3},"events":[{"id":"01a11241-7710-75c0-87ef-66c8ec5a8825","ordinal":1,"action":"created","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.634534+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"dueAt":"2026-10-09T17:27:22.630119+00:00"},{"id":"01a11241-7771-765d-95cd-1c4b5777f4d6","ordinal":2,"action":"accepted","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.736894+00:00","note":null,"status":"in-progress","owner":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-7784-7fee-979b-5dfc58fc7be4","ordinal":3,"action":"info-requested","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.756202+00:00","note":"請補上溫度紀錄的照片","status":"awaiting-info","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null}],"links":{"record":null,"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":["resume","complete","cancel","transfer","set-due","comment"],"cancelReasonRequired":true}`;
/** internal-set-due-past-422: HTTP 422. */
const SET_DUE_PAST_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"due-in-past","message":"時限不能早於現在。","errors":{"dueAt":["時限不能早於現在。"]}}`;
/** internal-transfer-same-422: HTTP 422. */
const TRANSFER_SAME_422_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.21","title":"Unprocessable Content","status":422,"reason":"case-group-unchanged","message":"案件已經在這個承辦組，請選擇其他承辦組。","errors":{"groupId":["案件已經在這個承辦組，請選擇其他承辦組。"]}}`;
/** internal-transfer: HTTP 200. */
const TRANSFER_JSON = `{"case":{"id":"01a11241-7710-7b9d-bef1-28903256d80a","title":"冷藏庫溫度降不下來","description":"二號冷藏庫從早上開始維持在 9 度。","status":"pending","origin":"manual","type":{"id":"01a11241-76fa-7fa8-8043-ac3007089a84","name":"設備故障報修"},"group":{"id":"01a11241-76d2-79ac-90bd-323606fe52ed","name":"採購組","archived":false},"createdBy":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-11T17:27:22.774865+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T17:27:22.634534+00:00","updatedAt":"2026-10-06T17:27:22.797397+00:00","acceptedAt":"2026-10-06T17:27:22.736894+00:00","completedAt":null,"cancelledAt":null,"eventCount":6},"events":[{"id":"01a11241-7710-75c0-87ef-66c8ec5a8825","ordinal":1,"action":"created","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.634534+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"dueAt":"2026-10-09T17:27:22.630119+00:00"},{"id":"01a11241-7771-765d-95cd-1c4b5777f4d6","ordinal":2,"action":"accepted","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.736894+00:00","note":null,"status":"in-progress","owner":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-7784-7fee-979b-5dfc58fc7be4","ordinal":3,"action":"info-requested","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.756202+00:00","note":"請補上溫度紀錄的照片","status":"awaiting-info","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-778f-71c1-9c93-9318dd91d4d1","ordinal":4,"action":"resumed","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.76727+00:00","note":"對方已用電話補件","status":"in-progress","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-7799-7143-9682-c8fecb2210af","ordinal":5,"action":"due-changed","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.777622+00:00","note":"等廠商報價","status":null,"owner":null,"fromGroup":null,"toGroup":null,"dueAt":"2026-10-11T17:27:22.774865+00:00"},{"id":"01a11241-77ae-722c-b966-24464ff46dd9","ordinal":6,"action":"transferred","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.797397+00:00","note":"需要採購壓縮機","status":"pending","owner":null,"fromGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"toGroup":{"id":"01a11241-76d2-79ac-90bd-323606fe52ed","name":"採購組","archived":false},"dueAt":null}],"links":{"record":null,"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":["cancel","comment"],"cancelReasonRequired":false}`;
/** internal-comment-resumes: HTTP 200. */
const COMMENT_RESUMES_JSON = `{"case":{"id":"01a11241-7710-7b9d-bef1-28903256d80a","title":"冷藏庫溫度降不下來","description":"二號冷藏庫從早上開始維持在 9 度。","status":"in-progress","origin":"manual","type":{"id":"01a11241-76fa-7fa8-8043-ac3007089a84","name":"設備故障報修"},"group":{"id":"01a11241-76d2-79ac-90bd-323606fe52ed","name":"採購組","archived":false},"createdBy":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"owner":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"dueAt":"2026-10-11T17:27:22.774865+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T17:27:22.634534+00:00","updatedAt":"2026-10-06T17:27:22.819391+00:00","acceptedAt":"2026-10-06T17:27:22.805611+00:00","completedAt":null,"cancelledAt":null,"eventCount":9},"events":[{"id":"01a11241-7710-75c0-87ef-66c8ec5a8825","ordinal":1,"action":"created","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.634534+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"dueAt":"2026-10-09T17:27:22.630119+00:00"},{"id":"01a11241-7771-765d-95cd-1c4b5777f4d6","ordinal":2,"action":"accepted","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.736894+00:00","note":null,"status":"in-progress","owner":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-7784-7fee-979b-5dfc58fc7be4","ordinal":3,"action":"info-requested","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.756202+00:00","note":"請補上溫度紀錄的照片","status":"awaiting-info","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-778f-71c1-9c93-9318dd91d4d1","ordinal":4,"action":"resumed","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.76727+00:00","note":"對方已用電話補件","status":"in-progress","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-7799-7143-9682-c8fecb2210af","ordinal":5,"action":"due-changed","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.777622+00:00","note":"等廠商報價","status":null,"owner":null,"fromGroup":null,"toGroup":null,"dueAt":"2026-10-11T17:27:22.774865+00:00"},{"id":"01a11241-77ae-722c-b966-24464ff46dd9","ordinal":6,"action":"transferred","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.797397+00:00","note":"需要採購壓縮機","status":"pending","owner":null,"fromGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"toGroup":{"id":"01a11241-76d2-79ac-90bd-323606fe52ed","name":"採購組","archived":false},"dueAt":null},{"id":"01a11241-77b5-760a-9985-59fc44f83cdf","ordinal":7,"action":"accepted","actor":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"at":"2026-10-06T17:27:22.805611+00:00","note":null,"status":"in-progress","owner":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-77bb-7679-b9e3-0326f4f8c317","ordinal":8,"action":"info-requested","actor":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"at":"2026-10-06T17:27:22.811821+00:00","note":"請提供壓縮機型號","status":"awaiting-info","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-77c3-7cd7-852c-39e37b0cb493","ordinal":9,"action":"commented","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.819391+00:00","note":"型號是 RX-200","status":"in-progress","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null}],"links":{"record":null,"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":["comment"],"cancelReasonRequired":true}`;
/** admin-complete: HTTP 200. */
const COMPLETE_JSON = `{"case":{"id":"01a11241-7710-7b9d-bef1-28903256d80a","title":"冷藏庫溫度降不下來","description":"二號冷藏庫從早上開始維持在 9 度。","status":"completed","origin":"manual","type":{"id":"01a11241-76fa-7fa8-8043-ac3007089a84","name":"設備故障報修"},"group":{"id":"01a11241-76d2-79ac-90bd-323606fe52ed","name":"採購組","archived":false},"createdBy":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"owner":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"dueAt":"2026-10-11T17:27:22.774865+00:00","resolution":"已採購並更換壓縮機","cancelReason":null,"createdAt":"2026-10-06T17:27:22.634534+00:00","updatedAt":"2026-10-06T17:27:22.826172+00:00","acceptedAt":"2026-10-06T17:27:22.805611+00:00","completedAt":"2026-10-06T17:27:22.826172+00:00","cancelledAt":null,"eventCount":10},"events":[{"id":"01a11241-7710-75c0-87ef-66c8ec5a8825","ordinal":1,"action":"created","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.634534+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"dueAt":"2026-10-09T17:27:22.630119+00:00"},{"id":"01a11241-7771-765d-95cd-1c4b5777f4d6","ordinal":2,"action":"accepted","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.736894+00:00","note":null,"status":"in-progress","owner":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-7784-7fee-979b-5dfc58fc7be4","ordinal":3,"action":"info-requested","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.756202+00:00","note":"請補上溫度紀錄的照片","status":"awaiting-info","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-778f-71c1-9c93-9318dd91d4d1","ordinal":4,"action":"resumed","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.76727+00:00","note":"對方已用電話補件","status":"in-progress","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-7799-7143-9682-c8fecb2210af","ordinal":5,"action":"due-changed","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.777622+00:00","note":"等廠商報價","status":null,"owner":null,"fromGroup":null,"toGroup":null,"dueAt":"2026-10-11T17:27:22.774865+00:00"},{"id":"01a11241-77ae-722c-b966-24464ff46dd9","ordinal":6,"action":"transferred","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.797397+00:00","note":"需要採購壓縮機","status":"pending","owner":null,"fromGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"toGroup":{"id":"01a11241-76d2-79ac-90bd-323606fe52ed","name":"採購組","archived":false},"dueAt":null},{"id":"01a11241-77b5-760a-9985-59fc44f83cdf","ordinal":7,"action":"accepted","actor":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"at":"2026-10-06T17:27:22.805611+00:00","note":null,"status":"in-progress","owner":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-77bb-7679-b9e3-0326f4f8c317","ordinal":8,"action":"info-requested","actor":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"at":"2026-10-06T17:27:22.811821+00:00","note":"請提供壓縮機型號","status":"awaiting-info","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-77c3-7cd7-852c-39e37b0cb493","ordinal":9,"action":"commented","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.819391+00:00","note":"型號是 RX-200","status":"in-progress","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null},{"id":"01a11241-77ca-74c2-bd91-6180e03d794a","ordinal":10,"action":"completed","actor":{"id":"01a11239-0c4c-7280-be00-9b2805170520","displayName":"安心商行管理者"},"at":"2026-10-06T17:27:22.826172+00:00","note":"已採購並更換壓縮機","status":"completed","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null}],"links":{"record":null,"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":[],"cancelReasonRequired":true}`;
/** admin-comment-closed-409: HTTP 409. */
const COMMENT_CLOSED_409_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Conflict","status":409,"reason":"case-changed","message":"這件案件剛被其他人更新，請重新整理後再試。"}`;
/** customer-accept-403: HTTP 403. */
const CUSTOMER_ACCEPT_403_JSON = `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.4","title":"Forbidden","status":403,"reason":"case","message":"你沒有這個案件的存取權限，或它已不存在。案件功能只開放組織內部帳號使用。"}`;
/** internal-create-follow-up: HTTP 201. */
const CREATE_FOLLOW_UP_JSON = `{"case":{"id":"01a11241-77ec-7436-9f82-d0fead711a77","title":"冷藏庫溫度降不下來","description":"更換後又開始升溫。","status":"pending","origin":"manual","type":{"id":"01a11241-76fa-7fa8-8043-ac3007089a84","name":"設備故障報修"},"group":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"createdBy":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-09T17:27:22.850467+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T17:27:22.852686+00:00","updatedAt":"2026-10-06T17:27:22.852686+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":null,"eventCount":1},"events":[{"id":"01a11241-77ec-7043-b7a3-ac9d5b809644","ordinal":1,"action":"created","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.852686+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"dueAt":"2026-10-09T17:27:22.850467+00:00"}],"links":{"record":null,"thread":null,"assistantIssue":null,"previousCase":{"caseId":"01a11241-7710-7b9d-bef1-28903256d80a","canOpen":true}},"allowedActions":["accept","cancel","comment"],"cancelReasonRequired":false}`;
/** internal-cancel-before-accept: HTTP 200. */
const CANCEL_BEFORE_ACCEPT_JSON = `{"case":{"id":"01a11241-77f5-7fbe-891e-c00961714fd6","title":"重複建立的案件","description":"","status":"cancelled","origin":"manual","type":{"id":"01a11241-76fa-7fa8-8043-ac3007089a84","name":"設備故障報修"},"group":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"createdBy":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"owner":null,"dueAt":"2026-10-09T17:27:22.867092+00:00","resolution":null,"cancelReason":null,"createdAt":"2026-10-06T17:27:22.869132+00:00","updatedAt":"2026-10-06T17:27:22.876841+00:00","acceptedAt":null,"completedAt":null,"cancelledAt":"2026-10-06T17:27:22.876841+00:00","eventCount":2},"events":[{"id":"01a11241-77f5-79a5-9764-9350bd59488e","ordinal":1,"action":"created","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.869132+00:00","note":null,"status":"pending","owner":null,"fromGroup":null,"toGroup":{"id":"01a11241-7699-77cc-b4d7-4819f8483bf5","name":"設備組","archived":false},"dueAt":"2026-10-09T17:27:22.867092+00:00"},{"id":"01a11241-77fc-7713-8d9a-ae0d992814d3","ordinal":2,"action":"cancelled","actor":{"id":"01a11239-0ca6-773a-949c-2a0c666c802e","displayName":"安心商行客服同仁"},"at":"2026-10-06T17:27:22.876841+00:00","note":null,"status":"cancelled","owner":null,"fromGroup":null,"toGroup":null,"dueAt":null}],"links":{"record":null,"thread":null,"assistantIssue":null,"previousCase":null},"allowedActions":[],"cancelReasonRequired":true}`;

function apiRepository() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: API_DEMO_REPOSITORY_FACTORY, useValue: () => null },
      { provide: DemoSessionService, useValue: { activeAccountId: signal<AccountId | null>('ignored-in-api-mode') } },
    ],
  });
  return { repository: TestBed.inject(CasesRepository), http: TestBed.inject(HttpTestingController) };
}

const pending = JSON.parse(DETAIL_PENDING_JSON) as CaseDetailView;
const caseId = pending.case.id;

function ready(result: { status: string; data?: CaseDetailView }): CaseDetailView {
  if (result.status !== 'ready' || !result.data) throw new Error(`expected ready, got ${result.status}`);
  return result.data;
}

describe('CasesRepository actions (API mode, recorded responses)', () => {
  it('reads what the caller may do now from the detail', async () => {
    const { repository, http } = apiRepository();
    const get = firstValueFrom(repository.get(caseId));
    http.expectOne(apiCasePath(caseId)).flush(JSON.parse(DETAIL_PENDING_JSON));
    const detail = ready(await get);
    expect(detail.allowedActions).toEqual(['accept', 'cancel', 'comment']);
    expect(detail.cancelReasonRequired).toBe(false);
    expect(detail.case).toMatchObject({ status: 'pending', eventCount: 1, owner: null });
    http.verify();
  });

  it('accepts with the shown eventCount and the caller becomes the case owner', async () => {
    const { repository, http } = apiRepository();
    const act = firstValueFrom(repository.act(caseId, 'accept', { eventCount: 1 }));
    const post = http.expectOne(`${apiCasePath(caseId)}:accept`);
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual({ eventCount: 1 });
    post.flush(JSON.parse(ACCEPT_JSON));
    const detail = ready(await act);
    expect(detail.case).toMatchObject({ status: 'in-progress', eventCount: 2, owner: { displayName: '安心商行客服同仁' } });
    expect(detail.allowedActions).toEqual(['request-info', 'complete', 'cancel', 'transfer', 'set-due', 'comment']);
    expect(detail.events.at(-1)).toMatchObject({ ordinal: 2, action: 'accepted', status: 'in-progress', actor: { displayName: '安心商行客服同仁' } });
    http.verify();
  });

  it('turns 409 case-changed into changed, and 403 case-action and 403 case into permission-denied', async () => {
    const { repository, http } = apiRepository();
    const stale = firstValueFrom(repository.act(caseId, 'accept', { eventCount: 0 }));
    http.expectOne(`${apiCasePath(caseId)}:accept`).flush(JSON.parse(ACCEPT_STALE_409_JSON), { status: 409, statusText: 'Conflict' });
    expect(await stale).toEqual({ status: 'changed', message: '這件案件剛被其他人更新，請重新整理後再試。' });

    const closed = firstValueFrom(repository.act(caseId, 'comment', { eventCount: 10, note: '補充' }));
    http.expectOne(`${apiCasePath(caseId)}/comments`).flush(JSON.parse(COMMENT_CLOSED_409_JSON), { status: 409, statusText: 'Conflict' });
    expect(await closed).toEqual({ status: 'changed', message: '這件案件剛被其他人更新，請重新整理後再試。' });

    const notOwner = firstValueFrom(repository.act(caseId, 'complete', { eventCount: 2, resolution: '已修好' }));
    http.expectOne(`${apiCasePath(caseId)}:complete`).flush(JSON.parse(COMPLETE_NOT_OWNER_403_JSON), { status: 403, statusText: 'Forbidden' });
    expect(await notOwner).toEqual({ status: 'permission-denied', reason: 'case-action', message: '你不能對這件案件執行這個動作。' });

    const customer = firstValueFrom(repository.act(caseId, 'accept', { eventCount: 10 }));
    http.expectOne(`${apiCasePath(caseId)}:accept`).flush(JSON.parse(CUSTOMER_ACCEPT_403_JSON), { status: 403, statusText: 'Forbidden' });
    expect(await customer).toMatchObject({ status: 'permission-denied', reason: 'case' });
    http.verify();
  });

  it('turns each 422 into validation-failed with its reason under its field', async () => {
    const { repository, http } = apiRepository();
    const cases = [
      ['complete', COMPLETE_BLANK_422_JSON, 'resolution-required', 'resolution', '請填寫處理結果。'],
      ['set-due', SET_DUE_PAST_422_JSON, 'due-in-past', 'dueAt', '時限不能早於現在。'],
      ['transfer', TRANSFER_SAME_422_JSON, 'case-group-unchanged', 'groupId', '案件已經在這個承辦組，請選擇其他承辦組。'],
    ] as const;
    for (const [action, body, reason, field, message] of cases) {
      const act = firstValueFrom(repository.act(caseId, action, { eventCount: 2 }));
      http.expectOne(apiCaseActionPath(caseId, action)).flush(JSON.parse(body), { status: 422, statusText: 'Unprocessable Content' });
      expect(await act).toEqual({ status: 'validation-failed', reason, message, fieldErrors: { [field]: message } });
    }
    http.verify();
  });

  it('records each hand-over: requesting information, a transfer that clears the owner, the creator\'s answer that resumes it', async () => {
    const { repository, http } = apiRepository();
    const requested = JSON.parse(REQUEST_INFO_JSON) as CaseDetailView;
    expect(requested.case.status).toBe('awaiting-info');
    expect(requested.events.at(-1)).toMatchObject({ action: 'info-requested', note: '請補上溫度紀錄的照片', status: 'awaiting-info' });

    const transfer = firstValueFrom(repository.act(caseId, 'transfer', { eventCount: 5, groupId: 'group-id', note: '需要採購壓縮機' }));
    const post = http.expectOne(`${apiCasePath(caseId)}:transfer`);
    expect(post.request.body).toEqual({ eventCount: 5, groupId: 'group-id', note: '需要採購壓縮機' });
    post.flush(JSON.parse(TRANSFER_JSON));
    const moved = ready(await transfer);
    expect(moved.case).toMatchObject({ status: 'pending', owner: null, group: { name: '採購組' } });
    expect(moved.allowedActions).toEqual(['cancel', 'comment']);
    expect(moved.events.at(-1)).toMatchObject({
      action: 'transferred', fromGroup: { name: '設備組' }, toGroup: { name: '採購組' }, note: '需要採購壓縮機', status: 'pending',
    });

    const comment = firstValueFrom(repository.act(caseId, 'comment', { eventCount: 8, note: '型號是 RX-200' }));
    http.expectOne(`${apiCasePath(caseId)}/comments`).flush(JSON.parse(COMMENT_RESUMES_JSON));
    const answered = ready(await comment);
    expect(answered.case.status).toBe('in-progress');
    expect(answered.events.at(-1)).toMatchObject({ action: 'commented', status: 'in-progress', actor: { displayName: '安心商行客服同仁' } });
    http.verify();
  });

  it('shows a completed case without actions, its timeline with who and when, and a new case linking it', async () => {
    const { repository, http } = apiRepository();
    const done = JSON.parse(COMPLETE_JSON) as CaseDetailView;
    expect(done.case).toMatchObject({ status: 'completed', resolution: '已採購並更換壓縮機', eventCount: 10 });
    expect(done.allowedActions).toEqual([]);
    expect(done.events.map((event) => [event.ordinal, event.action, event.actor?.displayName])).toEqual([
      [1, 'created', '安心商行客服同仁'],
      [2, 'accepted', '安心商行客服同仁'],
      [3, 'info-requested', '安心商行客服同仁'],
      [4, 'resumed', '安心商行客服同仁'],
      [5, 'due-changed', '安心商行客服同仁'],
      [6, 'transferred', '安心商行客服同仁'],
      [7, 'accepted', '安心商行管理者'],
      [8, 'info-requested', '安心商行管理者'],
      [9, 'commented', '安心商行客服同仁'],
      [10, 'completed', '安心商行管理者'],
    ]);
    expect(done.events.every((event) => !Number.isNaN(Date.parse(event.at)))).toBe(true);

    const followUp = JSON.parse(CREATE_FOLLOW_UP_JSON) as CaseDetailView;
    const create = firstValueFrom(repository.create({
      typeId: done.case.type.id, groupId: followUp.case.group.id, dueAt: followUp.case.dueAt, title: done.case.title,
      description: followUp.case.description, previousCaseId: caseId,
    }));
    http.expectOne(API_CASES_PATH).flush(followUp);
    const created = ready(await create);
    expect(created.links.previousCase).toEqual({ caseId, canOpen: true });
    expect(created.case.title).toBe(done.case.title);
    http.verify();
  });

  it('lets the creator cancel before acceptance with only the eventCount', async () => {
    const { repository, http } = apiRepository();
    const cancelled = JSON.parse(CANCEL_BEFORE_ACCEPT_JSON) as CaseDetailView;
    const act = firstValueFrom(repository.act(cancelled.case.id, 'cancel', { eventCount: 1 }));
    const post = http.expectOne(`${apiCasePath(cancelled.case.id)}:cancel`);
    expect(post.request.body).toEqual({ eventCount: 1 });
    post.flush(cancelled);
    expect(ready(await act).case).toMatchObject({ status: 'cancelled', cancelReason: null });
    http.verify();
  });
});
