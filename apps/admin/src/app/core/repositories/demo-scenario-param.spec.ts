import { readDemoScenario } from './demo-scenario-param';

describe('readDemoScenario', () => {
  it('reads a supported demo scenario from the query string', () => {
    expect(readDemoScenario('?demoScenario=loading')).toBe('loading');
    expect(readDemoScenario('?tab=form&demoScenario=partial-failure')).toBe('partial-failure');
    expect(readDemoScenario('?demoScenario=permission-denied')).toBe('permission-denied');
    expect(readDemoScenario('?demoScenario=disconnected-channel')).toBe('disconnected-channel');
  });

  it('ignores a missing or unknown scenario so the demo stays on the ready state', () => {
    expect(readDemoScenario('')).toBeNull();
    expect(readDemoScenario('?tab=form')).toBeNull();
    expect(readDemoScenario('?demoScenario=drop-database')).toBeNull();
  });
});
