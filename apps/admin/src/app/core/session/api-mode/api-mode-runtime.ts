/**
 * API 模式在 app initializer 才動態載入的部分（issue #308）：`HybridDemoRepository` 繼承
 * `MockDemoRepository`，兩者連同 seed 約 230 kB；AG-UI 的 ChatRunner 只在對話頁使用。
 * 只能由 `provide-api-mode.ts` 以 `import()` 載入，靜態匯入會讓它們回到初始 bundle。
 */
export { HybridDemoRepository, toAdminChatReply } from '../../repositories/hybrid-demo-repository';
export { AgUiChatRunner } from '@smart-agri/chat';
