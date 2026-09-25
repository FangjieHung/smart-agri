import type { AccountPermission, AccountRole } from '../domain/account.model';
import type { components } from './api-schema';

/**
 * Compile-time drift check (M1 skeleton plan, Slice 7): the API's OpenAPI document
 * (`apps/api/openapi/v1.json`, generated at build time from `AccountRole`/`AccountPermission`
 * in `SmartAgri.Domain`) and the frontend's own `AccountRole`/`AccountPermission` unions
 * (`account.model.ts`) must name exactly the same wire values. `openapi-typescript` turns
 * each enum into a string-literal union from its `[JsonStringEnumMemberName]` values, so a
 * mismatch here means either side renamed or reordered a member without updating the other.
 *
 * `IsExactUnion` is `true` only when both unions are assignable to each other; `AssertTrue`
 * only accepts `true`, so a mismatch is a compile error here, not a runtime failure. This
 * file must be typechecked by whatever runs `apps/admin`'s `test` target — a plain
 * transpile-only test runner would let a mismatch through silently (see this slice's
 * verification notes for how that was confirmed).
 */
type IsExactUnion<Generated, Frontend> = [Generated] extends [Frontend]
  ? [Frontend] extends [Generated]
    ? true
    : false
  : false;

type AssertTrue<T extends true> = T;

type GeneratedAccountRole = components['schemas']['AccountRole'];
type GeneratedAccountPermission = components['schemas']['AccountPermission'];

// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _AccountRoleMatchesGeneratedSchema = AssertTrue<IsExactUnion<GeneratedAccountRole, AccountRole>>;
// eslint-disable-next-line @typescript-eslint/no-unused-vars
type _AccountPermissionMatchesGeneratedSchema = AssertTrue<
  IsExactUnion<GeneratedAccountPermission, AccountPermission>
>;

describe('generated OpenAPI schema vs. account.model.ts', () => {
  it('keeps AccountRole assignable both ways with the generated schema', () => {
    // The real check is the compile-time assertions above (_AccountRoleMatchesGeneratedSchema
    // etc.); this runtime assertion just gives the drift check a visible test result.
    const roleSample: AccountRole = 'smb-admin';
    const generatedRoleSample: GeneratedAccountRole = roleSample;
    expect(generatedRoleSample).toBe(roleSample);
  });

  it('keeps AccountPermission assignable both ways with the generated schema', () => {
    const permissionSample: AccountPermission = 'manage-assistants';
    const generatedPermissionSample: GeneratedAccountPermission = permissionSample;
    expect(generatedPermissionSample).toBe(permissionSample);
  });
});
