/** 單元測試以 `with { loader: 'text' }` 匯入 AG-UI 錄製檔（`tools/agui-contract/fixtures/*.sse`）。 */
declare module '*.sse' {
  const content: string;
  export default content;
}
