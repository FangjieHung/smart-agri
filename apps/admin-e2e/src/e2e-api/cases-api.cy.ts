import { archiveDatabase, loginToApi } from '../support/api-mode';

/**
 * M7 Slice 11（issue #256）：案件的整條流程，對真實 API、PostgreSQL 與 Fake 模型跑。
 *
 * 1. 管理者新增四位內部同仁（一般同仁、設備組的甲與乙、採購組的丙），建立兩個承辦組與一個案件類型；
 *    新同仁以一次性密碼登入並設定自己的密碼（設成 `SEED_DEMO_PASSWORD`，之後就能用 `loginToApi` 登入）。
 * 2. 手動建立 → 設備組甲受理 → 轉給採購組 → 採購組丙受理、完成並填處理結果；沒受理過的設備組乙開啟案件網址
 *    看到無權限畫面，曾受理過的甲仍看得到。
 * 3. 對話提議（CI 是關鍵字模式：問題含「報修」，助理只有一個可提議的類型）→ 修改標題後確認 → 案件在清單、連結對話串。
 * 4. 轉給專人的問答 → 管理者在處理事項「另開案件」→ 處理事項以「非助理問題」結案，案件連回處理事項。
 * 5. 表單連結送出 → 自動開案，詳情顯示「由數據庫「X」自動建立」；送出者看不到這件案件。
 * 6. 逾期：`cy.exec` 執行 `case-set-due --due <一小時前>`（只在 Development／Testing 可用；API 不接受早於現在的
 *    時限，決定 H）→ 設備組乙的側欄數字、首頁卡片與「逾期」篩選都出現；管理者的瓶頸統計看到逾期 1 件。
 *
 * 組織資料隔離：全部在 對照組織（`control`）。其他 spec 只拿它檢查「看不到 安心商行 的資料」，以及 org-settings-api.cy.ts
 * 自己的助理與設定；這裡的帳號、承辦組、類型、數據庫名稱都帶這次執行的後綴，案件只在這些新的承辦組裡，所以：
 * - 安心商行 的同仁、助理、數據庫完全不受影響（database-api.cy.ts、chat-api.cy.ts 等都用 安心商行）。
 * - 對照組織管理者 不在任何承辦組裡，管理者也不因為是管理者而多算逾期（決定 D），側欄數字不變。
 * - 可提議的案件類型是每個助理各自的設定（預設是空的），org-settings-api.cy.ts 的助理即使問了含「申請」的問題也不會被提議。
 * - 側欄數字、首頁卡片、瓶頸統計都是這次新建的帳號與類型自己的數字，所以可以斷言精確的件數，重跑也一樣。
 *
 * 帳號、承辦組、類型與案件不能刪除（帳號只停用、承辦組只封存、案件永久保存），本機重跑會一直累積在 對照組織；
 * CI 每次都是新的資料庫。最後一個 test 刪掉助理與知識庫、封存數據庫，和其他 API 模式 spec 一致。
 * 跨 test 的值存在 `rememberValue` task（cypress.api.config.ts）；一次性密碼只留在第一個 test 的變數裡，不存、不印。
 */
const GUID = '[0-9a-f-]{36}';
const ORGANIZATION = 'control';
const STREAM_TIMEOUT = 20000;
/** `case-set-due` 是 API 的一次性子指令；`cy.exec` 的工作目錄是 apps/admin-e2e。 */
const SET_DUE_COMMAND = `dotnet run --no-build --project ../api/src/SmartAgri.Api -- case-set-due --organization ${ORGANIZATION}`;
const HOUR_MS = 60 * 60 * 1000;

type MemberKey = 'staff' | 'equipmentA' | 'equipmentB' | 'purchasing';

interface Member {
  readonly login: string;
  readonly name: string;
}

interface CasesRun {
  readonly suffix: string;
  readonly manualCaseId: string;
  readonly knowledgeId: string;
  readonly assistantId: string;
  readonly databaseId: string;
  readonly overdueCaseId: string;
}

const RUN_KEY = 'cases-api';

function names(suffix: string) {
  const member = (role: string, label: string): Member => ({ login: `e2e${suffix}${role}`, name: `E2E ${label} ${suffix}` });
  return {
    members: {
      staff: member('staff', '同仁'),
      equipmentA: member('eqa', '設備甲'),
      equipmentB: member('eqb', '設備乙'),
      purchasing: member('pur', '採購丙'),
    } satisfies Record<MemberKey, Member>,
    equipment: `設備組 ${suffix}`,
    purchasingGroup: `採購組 ${suffix}`,
    type: `設備報修 ${suffix}`,
    manualTitle: `冷藏庫溫度降不下來 ${suffix}`,
    resolution: `已更換壓縮機繼電器 ${suffix}`,
    proposalQuestion: `三號冷藏庫的壓縮機一直跳電，需要報修（${suffix}）`,
    proposalTitle: `三號冷藏庫壓縮機跳電 ${suffix}`,
    handoffQuestion: `今天台北天氣如何（${suffix}）`,
    knowledge: `E2E 案件知識庫 ${suffix}`,
    assistant: `E2E 案件助理 ${suffix}`,
    database: `E2E 報修登記 ${suffix}`,
    overdueTitle: `溫室感測器離線 ${suffix}`,
  };
}

function caseIdFromLocation(): Cypress.Chainable<string> {
  return cy.location('search', { timeout: 20000 })
    .should('match', new RegExp(`^\\?case=${GUID}$`))
    .then((search) => search.slice('?case='.length));
}

/** 選取 `<select>` 裡文字包含 `text` 的選項（選項文字可能帶有承辦組、時限等說明）。 */
function selectOptionContaining(select: string, text: string): void {
  cy.contains(`${select} option`, text).then(($option) => {
    cy.get(select).select(String($option.val()));
  });
}

/** 清單讀完後才檢查有沒有某件案件（沒有符合的案件時是空狀態）。 */
function listSettled(): void {
  cy.get('.cases-list .case-items, .cases-list [data-state="empty"]', { timeout: 20000 }).should('exist');
}

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
  cy.get('[role="log"]', { timeout: STREAM_TIMEOUT }).should('not.have.attr', 'aria-busy', 'true');
}

/** 管理者在「團隊與權限」新增一位內部同仁，回傳這次唯一一次顯示的一次性密碼（不寫進指令紀錄）。 */
function addMember(member: Member, permissions: readonly string[]): Cypress.Chainable<string> {
  cy.get('#team-title').scrollIntoView();
  cy.contains('button', '新增成員').click();
  cy.get('form.create-panel[aria-labelledby="add-member-title"]').should('be.visible').within(() => {
    cy.get('#new-member-login-name').type(member.login);
    cy.get('#new-member-display-name').type(member.name);
    // 明確選「內部同仁」：選單一開啟時顯示的是第一個選項，不一定是元件預設的角色（見 #256 回報）。
    cy.get('#new-member-role').select('internal-employee').should('have.value', 'internal-employee');
    // 權限是畫成開關的 checkbox（本身看不到），與 assistant-acceptance-api.cy.ts 一樣用 force。
    for (const permission of permissions) cy.get(`#new-member-permission-${permission}`).check({ force: true }).should('be.checked');
    cy.get('button[type="submit"]').click();
  });
  cy.contains('#new-member-password-title', `已新增「${member.name}」`).should('be.visible');
  return cy.get('#new-member-password').invoke({ log: false }, 'val').then((value) => {
    const password = String(value ?? '');
    expect(password.length, 'one-time password length').to.be.greaterThan(11);
    cy.contains('button', '關閉').click();
    cy.get('#new-member-password').should('not.exist');
    return cy.wrap(password, { log: false });
  });
}

/** 新同仁第一次登入：一次性密碼 → 設定新密碼（設成 `SEED_DEMO_PASSWORD`）→ 首頁。 */
function signInFirstTime(member: Member, oneTimePassword: string): void {
  cy.env<{ demoPassword?: string }>(['demoPassword'], { log: false }).then(({ demoPassword }) => {
    if (typeof demoPassword !== 'string' || demoPassword === '') {
      throw new Error('SEED_DEMO_PASSWORD is not set: the new members set it as their own password.');
    }
    const secret = { log: false, parseSpecialCharSequences: false };
    cy.visit('/login');
    cy.get('#login-organization-code').clear().type(ORGANIZATION);
    cy.get('#demo-username').clear().type(member.login);
    cy.get('#demo-password').clear().type(oneTimePassword, secret);
    cy.contains('form button[type="submit"]', '登入').click();
    cy.location('pathname', { timeout: 20000 }).should('eq', '/change-password');
    cy.get('#change-password-current').type(oneTimePassword, secret);
    cy.get('#change-password-new').type(demoPassword, secret);
    cy.get('#change-password-confirm').type(demoPassword, secret);
    cy.contains('button', '設定新密碼').click();
    cy.location('pathname', { timeout: 20000 }).should('eq', '/app/home');
  });
}

function createGroup(name: string, members: readonly Member[]): void {
  cy.get('#case-group-new-name').scrollIntoView().clear().type(name);
  cy.contains('[data-case-group-create] button', '建立承辦組').click();
  cy.get(`[data-case-group="${name}"]`).should('contain', '尚無成員');
  cy.get(`[data-case-group="${name}"]`).contains('button', `編輯「${name}」的成員`).click();
  cy.get(`[data-case-group="${name}"] [data-case-group-members-editor]`).within(() => {
    for (const member of members) cy.contains('.choice', member.name).find('input[type="checkbox"]').check();
    cy.contains('button', '儲存成員').click();
  });
  for (const member of members) cy.get(`[data-case-group="${name}"] .group__members`).should('contain', member.name);
}

describe('cases: groups, the case flow, proposals, issues, automatic cases and overdue (API mode)', () => {
  let run: CasesRun = { suffix: '', manualCaseId: '', knowledgeId: '', assistantId: '', databaseId: '', overdueCaseId: '' };
  const n = () => names(run.suffix);
  const login = (key: MemberKey) => loginToApi(ORGANIZATION, n().members[key].login);

  function remember(changes: Partial<CasesRun>): void {
    run = { ...run, ...changes };
    cy.task('rememberValue', { key: RUN_KEY, value: run }, { log: false });
  }

  beforeEach(() => {
    cy.task<CasesRun | null>('recallValue', RUN_KEY, { log: false }).then((saved) => {
      if (saved !== null) run = saved;
    });
  });

  it('lets the manager add four members, two case groups and a case type; the new members set their own passwords', () => {
    remember({ suffix: Date.now().toString(36), manualCaseId: '', knowledgeId: '', assistantId: '', databaseId: '', overdueCaseId: '' });
    const { members, equipment, purchasingGroup, type } = n();
    const passwords: Partial<Record<MemberKey, string>> = {};
    loginToApi(ORGANIZATION, 'admin');
    // 「新增成員」對話框比預設的 660px 視窗高，而且不能捲動（送出鈕被裁掉，見 #256 回報）：新增成員時用高一點的視窗。
    cy.viewport(1000, 1400);
    cy.visit('/app/settings');
    cy.get('#team-title', { timeout: 20000 }).should('exist');
    // 一般同仁會用分享的助理、會從表單連結送出紀錄；承辦組的成員只需要能登入。
    addMember(members.staff, ['use-shared-assistants', 'submit-authorized-forms']).then((value) => {
      passwords.staff = value;
    });
    addMember(members.equipmentA, ['use-shared-assistants']).then((value) => {
      passwords.equipmentA = value;
    });
    addMember(members.equipmentB, ['use-shared-assistants']).then((value) => {
      passwords.equipmentB = value;
    });
    addMember(members.purchasing, ['use-shared-assistants']).then((value) => {
      passwords.purchasing = value;
    });
    for (const member of Object.values(members)) cy.contains('li.member', member.name).should('exist');
    cy.viewport(1000, 660);

    // 重新載入：承辦組的成員候選名單包含剛新增的同仁。
    cy.visit('/app/settings');
    cy.get('[data-case-groups-panel]', { timeout: 20000 }).should('exist');
    createGroup(equipment, [members.equipmentA, members.equipmentB]);
    createGroup(purchasingGroup, [members.purchasing]);

    cy.get('[data-case-types-panel]').contains('button', '新增案件類型').scrollIntoView().click();
    cy.get('[data-case-type-form]').within(() => {
      cy.get('#case-type-name').type(type);
      cy.get('#case-type-description').type('冷藏庫、溫室設備故障需要派人維修。');
      cy.get('#case-type-group').select(equipment);
      cy.get('#case-type-due').should('have.value', '3');
      cy.get('#case-type-due-unit').should('have.value', 'days');
      cy.contains('button', '建立案件類型').click();
    });
    cy.get(`[data-case-type="${type}"]`).should('contain', `預設承辦組：${equipment}`).and('contain', '3 天');

    cy.then(() => {
      for (const key of Object.keys(members) as MemberKey[]) signInFirstTime(members[key], passwords[key] ?? '');
    });
  });

  it('creates a case by hand; 設備組 accepts and transfers it; 採購組 accepts and completes it with a resolution', () => {
    expect(run.suffix).not.to.eq('');
    const { members, equipment, purchasingGroup, type, manualTitle, resolution } = n();

    login('staff');
    cy.visit('/app/cases');
    cy.get('[data-case-create]').click();
    cy.get('[data-case-form]').within(() => {
      cy.get('#case-type').select(type);
      cy.get('#case-group').find('option:selected').should('have.text', equipment);
      cy.get('#case-due').invoke('val').should('match', /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
      cy.get('#case-title').type(manualTitle);
      cy.get('#case-description').type('二號冷藏庫從早上開始溫度一直在 12 度。');
      cy.contains('button', '建立案件').click();
    });
    caseIdFromLocation().then((id) => remember({ manualCaseId: id }));
    cy.get('[data-case-detail]').within(() => {
      cy.contains('h3', manualTitle).should('be.visible');
      cy.contains('.case-status', '待受理').should('be.visible');
      cy.contains('.case-facts div', '承辦組').should('contain', equipment);
      cy.get('[data-case-created-by]').should('have.text', members.staff.name);
      cy.contains('.case-facts div', '來源').should('contain', '平台內建立');
    });

    login('equipmentA');
    cy.then(() => cy.visit(`/app/cases?case=${run.manualCaseId}`));
    cy.contains('[data-case-detail] h3', manualTitle).should('be.visible');
    cy.get('[data-case-action="accept"]').click();
    cy.get('[data-case-detail]').within(() => {
      cy.contains('.case-status', '處理中').scrollIntoView().should('be.visible');
      cy.contains('.case-facts div', '案件負責人').should('contain', members.equipmentA.name);
    });
    cy.get('[data-case-action="transfer"]').click();
    cy.get('[data-case-action-form] #case-action-group').select(purchasingGroup);
    cy.get('[data-case-action-form] #case-action-text').type('需要採購繼電器');
    cy.get('[data-case-action-form]').contains('button', '確認轉組').click();
    cy.get('[data-case-detail]').within(() => {
      cy.contains('.case-status', '待受理').scrollIntoView().should('be.visible');
      cy.contains('.case-facts div', '承辦組').should('contain', purchasingGroup);
      cy.contains('.case-facts div', '案件負責人').should('contain', '尚未受理');
      cy.contains('[data-case-history] li', `轉組（${equipment} → ${purchasingGroup}）：需要採購繼電器`).scrollIntoView().should('be.visible');
    });
    // 轉出後甲不能再受理（他不在採購組），只剩下看得到。
    cy.get('[data-case-action="accept"]').should('not.exist');

    login('purchasing');
    cy.visit('/app/cases?scope=my-groups&status=pending');
    cy.contains('.case-item', manualTitle).should('contain', purchasingGroup).click();
    cy.get('[data-case-action="accept"]').click();
    cy.contains('[data-case-detail] .case-status', '處理中').scrollIntoView().should('be.visible');
    cy.get('[data-case-action="complete"]').click();
    cy.get('[data-case-action-form] #case-action-text').type(resolution);
    cy.get('[data-case-action-form]').contains('button', '確認完成').click();
    cy.get('[data-case-detail]').within(() => {
      cy.contains('.case-status', '已完成').scrollIntoView().should('be.visible');
      cy.contains('.case-facts div', '案件負責人').should('contain', members.purchasing.name);
      cy.contains('.case-facts div', '處理結果').should('contain', resolution);
      cy.contains('[data-case-history] li', '完成').scrollIntoView().should('contain', resolution);
    });
    cy.get('[data-case-actions]').should('not.exist');
  });

  it('shows the case to its former owner, but the 設備組 member who never accepted it gets the no-permission screen', () => {
    expect(run.manualCaseId).to.match(new RegExp(`^${GUID}$`));
    const { members, manualTitle, resolution } = n();

    login('equipmentB');
    cy.then(() => cy.visit(`/app/cases?case=${run.manualCaseId}`));
    cy.get('[data-state="permission-denied"]', { timeout: 20000 })
      .should('be.visible')
      .and('contain.text', '無法查看這件案件')
      .and('contain.text', '你沒有這個案件的存取權限，或它已不存在。');
    cy.get('[data-case-detail]').should('not.exist');
    cy.contains(manualTitle).should('not.exist');
    cy.visit('/app/cases?status=all');
    listSettled();
    cy.contains('.case-item', manualTitle).should('not.exist');

    // 曾受理過的甲：轉組後仍是「曾任負責人」，看得到最後的結果。
    login('equipmentA');
    cy.then(() => cy.visit(`/app/cases?case=${run.manualCaseId}`));
    cy.get('[data-case-detail]').within(() => {
      cy.contains('h3', manualTitle).should('be.visible');
      cy.contains('.case-status', '已完成').scrollIntoView().should('be.visible');
      cy.contains('.case-facts div', '處理結果').should('contain', resolution);
      cy.contains('[data-case-history] li', '受理').should('contain', members.equipmentA.name);
    });
  });

  it('prepares an assistant with an empty knowledge base that may propose the case type, shared with the member', () => {
    expect(run.suffix).not.to.eq('');
    const { members, type, knowledge, assistant } = n();
    loginToApi(ORGANIZATION, 'admin');

    cy.visit('/app/knowledge');
    cy.get('button[page-header-actions]').contains('建立知識庫').click();
    cy.get('#knowledge-name').type(knowledge);
    cy.get('.create-panel').contains('button', '建立知識庫').click();
    cy.contains('tr', knowledge).contains('a', knowledge).click();
    cy.location('pathname').should('match', new RegExp(`^/app/knowledge/${GUID}/content$`)).then((path) => {
      remember({ knowledgeId: path.split('/')[3] });
    });

    cy.visit('/app/assistants/new/purpose');
    cy.contains('label', '回答客戶問題').click();
    cy.get('#assistant-name').clear().type(assistant);
    cy.get('#audience-internal').check();
    cy.contains('button', '下一步').click();
    cy.contains('.source-row', knowledge, { timeout: 20000 }).contains('button', '加入').click();
    cy.contains('button', '下一步').click();
    cy.contains('button', '下一步').click();
    cy.get('#trial-question-input').type('營業時間');
    cy.get('.ask-submit').click();
    cy.get('.trial-answer', { timeout: STREAM_TIMEOUT }).should('exist');
    cy.contains('button', '建立助理').click();
    cy.location('pathname', { timeout: 20000 }).should('match', new RegExp(`^/app/assistants/${GUID}/overview$`)).then((path) => {
      remember({ assistantId: path.split('/')[3] });
    });

    cy.then(() => cy.visit(`/app/assistants/${run.assistantId}/data-sources`));
    cy.intercept('PUT', /\/api\/v1\/assistants\/[^/]+\/sources\/case-type\/[^/]+$/).as('proposable');
    cy.contains('app-proposable-case-types label', type, { timeout: 20000 }).find('input[type="checkbox"]').should('not.be.checked').check();
    cy.wait('@proposable').its('response.statusCode').should('eq', 200);
    cy.reload();
    cy.contains('app-proposable-case-types label', type, { timeout: 20000 }).find('input[type="checkbox"]').should('be.checked');

    cy.then(() => cy.visit(`/app/assistants/${run.assistantId}/publishing?channel=platform`));
    cy.get('app-platform-sharing', { timeout: 20000 }).within(() => {
      cy.contains('label', members.staff.name).click();
      cy.contains('button', '儲存可使用的帳號').click();
      cy.get('[aria-live="polite"]').should('contain', '已更新可使用的帳號');
    });
  });

  it('proposes a case in the conversation; the member edits the title and confirms, and the case links the thread', () => {
    expect(run.assistantId).to.match(new RegExp(`^${GUID}$`));
    const { members, equipment, type, proposalQuestion, proposalTitle } = n();

    login('staff');
    cy.then(() => cy.visit(`/app/chat/${run.assistantId}`));
    ask(proposalQuestion);
    // 第一則問題建立對話串：網址換成串的網址、訊息換成伺服器的版本後，卡片才不會再重畫。
    cy.location('pathname', { timeout: STREAM_TIMEOUT }).should('match', new RegExp(`^/app/chat/${GUID}/${GUID}$`));
    cy.get('[role="log"] [data-kind="case-proposal"] .kind').should('contain', '建議開案');
    const card = 'app-case-proposal-card';
    // 對話紀錄是自己捲動的區塊：先捲到卡片再檢查可見。
    cy.get(card, { timeout: STREAM_TIMEOUT }).should('have.length', 1).scrollIntoView().should('be.visible')
      .and('contain', type).and('contain', equipment).and('contain', '3 天');
    cy.get(`${card} input[type="text"]`).should('have.value', proposalQuestion);
    cy.get(`${card} input[type="text"]`).clear();
    cy.get(`${card} input[type="text"]`).type(proposalTitle);
    cy.get(`${card} input[type="text"]`).should('have.value', proposalTitle);
    cy.get(`${card} textarea`).type('壓縮機下午跳電三次，請派人檢查。');
    cy.contains(`${card} button`, '建立案件').click();
    cy.get('[role="dialog"]').should('be.visible').within(() => {
      cy.contains('建立這件案件？').should('exist');
      cy.contains(proposalTitle).should('exist');
      cy.get('.confirm-case-proposal').click();
    });
    cy.get('[role="dialog"]').should('not.exist');
    cy.get('app-case-proposal-card').last().should('contain', `已建立案件「${proposalTitle}」`);
    cy.location('pathname').should('match', new RegExp(`^/app/chat/${GUID}/${GUID}$`)).then((chatPath) => {
      cy.get('app-case-proposal-card').last().contains('a', '查看案件').click();
      caseIdFromLocation();
      cy.get('[data-case-detail]').within(() => {
        cy.contains('h3', proposalTitle).should('be.visible');
        cy.contains('.case-status', '待受理').should('be.visible');
        cy.contains('.case-facts div', '承辦組').should('contain', equipment);
        cy.get('[data-case-created-by]').should('have.text', members.staff.name);
        cy.contains('.case-facts div', '來源').should('contain', '對話中提議');
        cy.get('[data-case-link="thread"]').scrollIntoView().should('contain', '對話')
          .find('a').should('have.attr', 'href', chatPath);
      });
      cy.contains('.case-item', proposalTitle).should('contain', equipment);
    });
  });

  it('opens a case from a handed-off answer; the issue is closed as 非助理問題 and the case links back to it', () => {
    expect(run.assistantId).to.match(new RegExp(`^${GUID}$`));
    const { equipment, type, handoffQuestion } = n();

    login('staff');
    cy.then(() => cy.visit(`/app/chat/${run.assistantId}`));
    ask(handoffQuestion);
    cy.get('[role="log"] [data-kind="no-result"]', { timeout: STREAM_TIMEOUT }).last().should('exist');
    cy.get('[role="log"] .handoff-trigger').last().click();
    cy.get('[role="dialog"]').should('contain', handoffQuestion).contains('button', '確認轉交').click();
    cy.contains('[role="status"]', '已轉交這一則問答').should('be.visible');

    loginToApi(ORGANIZATION, 'admin');
    cy.visit('/app/issues');
    cy.contains('.issue-item', handoffQuestion, { timeout: 20000 }).click();
    cy.get('[data-issue-open-case]').scrollIntoView().click();
    cy.get('[role="dialog"]').should('be.visible').and('contain', '非助理問題');
    cy.get('#issue-case-title').should('have.value', handoffQuestion);
    cy.get('#issue-case-type').select(type);
    cy.get('#issue-case-group').find('option:selected').should('have.text', equipment);
    cy.get('.confirm-open-case').click();
    cy.get('[role="dialog"]').should('not.exist');
    cy.get('[data-issue-case-notice]').should('contain', '非助理問題');
    cy.get('[data-issue-resolution-kind]').should('contain', '非助理問題');
    cy.contains('.issue-item', handoffQuestion).should('contain', '已解決（非助理問題）');
    cy.get('[data-issue-open-case]').should('not.exist');

    cy.get('[data-issue-case-notice] a').click();
    cy.location('pathname').should('eq', '/app/cases');
    caseIdFromLocation();
    cy.get('[data-case-detail]').within(() => {
      cy.contains('h3', handoffQuestion).should('be.visible');
      cy.contains('.case-facts div', '來源').should('contain', '處理事項另開');
      cy.contains('.case-facts div', '承辦組').should('contain', equipment);
      cy.get('[data-case-link="issue"]').scrollIntoView().contains('a', '開啟處理事項').click();
    });
    cy.location('pathname').should('eq', '/app/issues');
    cy.get('[data-issue-linked-case] a').should('have.attr', 'href').and('include', '/app/cases?case=');
  });

  it('opens a case automatically when a record is submitted through the form link', () => {
    expect(run.suffix).not.to.eq('');
    const { equipment, type, database } = n();

    loginToApi(ORGANIZATION, 'admin');
    cy.visit('/app/databases');
    cy.get('button[page-header-actions]').contains('新增資料庫').click();
    cy.get('input[type="radio"][value="template-blank"]').check({ force: true });
    cy.get('#database-name').clear().type(database);
    cy.get('.create-panel button[type="submit"]').click();
    cy.location('pathname', { timeout: 20000 }).should('match', new RegExp(`^/app/databases/${GUID}/form$`)).then((path) => {
      remember({ databaseId: path.split('/')[3] });
    });
    cy.then(() => cy.visit(`/app/databases/${run.databaseId}/access`));
    cy.get('[data-database-auto-case]', { timeout: 20000 }).scrollIntoView().within(() => {
      cy.get('#auto-case-type').find('option:selected').should('have.text', '不自動開案');
    });
    selectOptionContaining('[data-database-auto-case] #auto-case-type', type);
    cy.get('[data-database-auto-case]').within(() => {
      // 設備組的兩位同仁都不是這個數據庫的資料管理者。
      cy.get('[data-auto-case-read-hint]').should('contain', '承辦組中有 2 人無法讀取這個數據庫的紀錄。');
      cy.contains('button', '儲存自動開案設定').click();
      cy.get('.feedback').should('contain', `已設定：每筆新紀錄送出後，自動建立一件「${type}」案件。`);
    });

    login('staff');
    cy.then(() => cy.visit(`/app/forms/${run.databaseId}`));
    cy.contains('h1', database, { timeout: 20000 }).should('be.visible');
    cy.get('#chat-field-field-item').type('二號溫室噴霧馬達');
    cy.contains('button', '下一步：確認同意').click();
    cy.get('app-consent-confirmation').should('be.visible');
    cy.get('#consent-agree').check();
    cy.contains('button', '同意並送出').click();
    cy.get('[data-kind="submission-receipt"]', { timeout: 20000 }).should('contain', '已送出').and('contain', '二號溫室噴霧馬達');
    // 送出者不因為送出而看得到自動開的案件（決定 M）。
    cy.visit('/app/cases?status=all');
    listSettled();
    cy.contains('.case-item', `${database}：新紀錄`).should('not.exist');

    login('equipmentA');
    cy.visit('/app/cases?scope=my-groups&status=pending');
    cy.contains('.case-item', `${database}：新紀錄`, { timeout: 20000 }).should('contain', equipment).click();
    cy.get('[data-case-detail]').within(() => {
      cy.contains('h3', `${database}：新紀錄`).should('be.visible');
      cy.get('[data-case-created-by]').should('have.text', `由數據庫「${database}」自動建立`);
      cy.contains('.case-facts div', '來源').should('contain', '數據庫送出後自動建立');
      cy.contains('[data-case-history] li', '建立案件').should('contain', '系統');
      // 案件的可見性不會讓承辦組讀到紀錄（ADR：可見性與紀錄權限分開判斷）。
      cy.get('[data-case-link="record"]').scrollIntoView()
        .should('contain', '紀錄可查看')
        .and('contain', '你沒有讀取這筆紀錄的權限。');
      cy.get('[data-case-link="record"] a').should('not.exist');
    });
  });

  it('makes a case overdue with case-set-due: the side navigation, the home card, the overdue filter and the statistics show it', () => {
    expect(run.suffix).not.to.eq('');
    const { equipment, type, overdueTitle } = n();
    const casesLink = '.app-sidenav a[href="/app/cases"]';

    login('staff');
    cy.visit('/app/cases');
    cy.get('[data-case-create]').click();
    cy.get('[data-case-form]').within(() => {
      cy.get('#case-type').select(type);
      cy.get('#case-title').type(overdueTitle);
      cy.contains('button', '建立案件').click();
    });
    caseIdFromLocation().then((id) => remember({ overdueCaseId: id }));
    cy.contains('[data-case-detail] h3', overdueTitle).should('be.visible');

    // 還沒逾期：設備組的乙只有「待我受理」，側欄沒有數字。
    login('equipmentB');
    cy.get(casesLink).should('not.have.attr', 'aria-label');
    cy.get(`${casesLink} .nav-count`).should('not.exist');
    cy.get('[data-home-cases-summary]', { timeout: 20000 }).should('contain', '已逾期 0 件');

    cy.then(() => {
      const due = new Date(Date.now() - HOUR_MS).toISOString();
      cy.exec(`${SET_DUE_COMMAND} --case ${run.overdueCaseId} --due ${due}`, { timeout: 120000, failOnNonZeroExit: false }).then((result) => {
        expect(result.exitCode, result.stderr).to.eq(0);
        expect(result.stdout).to.contain(run.overdueCaseId).and.contain('處理時限');
      });
    });

    // 數字在換頁時讀取：重新登入（回到首頁）後才看到。
    login('equipmentB');
    cy.get(`${casesLink} .nav-count`, { timeout: 20000 }).should('have.text', '1');
    cy.get(casesLink).should('have.attr', 'aria-label', '案件，1 件逾期');
    cy.get('[data-home-cases]').scrollIntoView().should('be.visible');
    cy.get('[data-home-cases-summary]').should('contain', '已逾期 1 件');
    cy.get('[data-home-cases-link="group-overdue"]').should('contain', '承辦組待受理的逾期案件 1 件').click();
    cy.location('search').should('eq', '?scope=my-groups&status=pending&overdue=true');
    cy.get('[data-case-overdue-filter]').should('be.checked');
    cy.get('.cases-list .case-item', { timeout: 20000 }).should('have.length', 1).first()
      .should('contain', overdueTitle)
      .find('.case-overdue').should('have.text', '已逾期');
    cy.get('.cases-list .case-item').first().click();
    cy.contains('[data-case-detail] [data-case-history] li', '調整時限').scrollIntoView().should('contain', '系統');

    // 管理者的瓶頸統計：這個類型在設備組的那一列逾期 1 件，點開就是這件案件。
    loginToApi(ORGANIZATION, 'admin');
    cy.get(`${casesLink} .nav-count`).should('not.exist');
    cy.visit('/app/cases?view=statistics');
    cy.get('[data-case-statistics]', { timeout: 20000 }).should('exist');
    cy.get('[data-case-statistics-row]')
      .filter((_, row) => (row.textContent ?? '').includes(type) && (row.textContent ?? '').includes(equipment))
      .should('have.length', 1)
      .find('[data-case-statistics-link="overdue"]')
      .should('have.text', '1')
      .click();
    cy.get('[data-case-statistics]').should('not.exist');
    cy.get('.cases-list .case-item', { timeout: 20000 }).should('have.length', 1).and('contain', overdueTitle);
  });

  it('removes its assistant and knowledge base and archives its database', () => {
    expect(run.assistantId).to.match(new RegExp(`^${GUID}$`));
    expect(run.knowledgeId).to.match(new RegExp(`^${GUID}$`));
    expect(run.databaseId).to.match(new RegExp(`^${GUID}$`));
    loginToApi(ORGANIZATION, 'admin');
    cy.visit(`/app/assistants/${run.assistantId}/overview`);
    cy.contains('button', '刪除助理').click();
    cy.get('.delete-panel').contains('button', '刪除助理與所有對話').click();
    cy.location('pathname', { timeout: 20000 }).should('eq', '/app/assistants');
    cy.visit(`/app/knowledge/${run.knowledgeId}/content`);
    cy.contains('button', '刪除知識庫').click();
    cy.get('.delete-panel').contains('button', /^刪除$/).click();
    cy.location('pathname').should('eq', '/app/knowledge');
    archiveDatabase(run.databaseId);
  });
});
