import { loadMockRepositoryModules } from './app/core/repositories/tokens';

// The app loads the mock repository through an app initializer (see app.config.ts);
// specs that inject DEMO_REPOSITORY directly never run it, so preload it here.
await loadMockRepositoryModules();
