import { provideApiMode } from '../app/core/session/api-mode/provide-api-mode';
import type { AdminEnvironment } from './environment.model';

/** `--configuration=api` 以 fileReplacements 換上這個檔案。 */
export const environment: AdminEnvironment = {
  apiMode: true,
  providers: [provideApiMode()],
};
