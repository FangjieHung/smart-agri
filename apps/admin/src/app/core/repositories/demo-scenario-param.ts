import { DEMO_SCENARIOS, type DemoScenario } from './demo-repository';

/**
 * Demo：用網址參數預覽狀態，例如
 * `/app/knowledge?demoScenario=loading`、`?demoScenario=partial-failure`、
 * `?demoScenario=permission-denied`、`?demoScenario=disconnected-channel`。
 * 只影響這個 mock repository，不代表真實後端行為。
 */
export const DEMO_SCENARIO_PARAM = 'demoScenario';

export function readDemoScenario(search: string): DemoScenario | null {
  const value = new URLSearchParams(search).get(DEMO_SCENARIO_PARAM);
  if (value === null) return null;
  return (DEMO_SCENARIOS as readonly string[]).includes(value)
    ? (value as DemoScenario)
    : null;
}
