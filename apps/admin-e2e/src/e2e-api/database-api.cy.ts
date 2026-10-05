import { loginToApi } from '../support/api-mode';

/**
 * API 模式的數據庫（M4 issue #142）：對真實 API 與 PostgreSQL 跑，不需要任何示範資料庫。
 * 從模板建立 → 清單與詳情顯示擁有者、用途、欄位與更新時間 → 沒有資料來源管理權限的帳號不能建立
 * → 另一個組織直接開網址看不到。
 *
 * 編輯表單與試填（#143）：存成新版本、欄位錯誤顯示在對應欄位、試填不建立紀錄、重新整理後仍是新版。
 *
 * 第四個 `it`（M4 issue #144）：擁有者指定資料管理者 → 被指定且具備權限的同仁看得到（唯讀）→
 * 撤銷指定後立刻看不到，不必重新登入。
 *
 * 第五個 `it`（M4 issue #145）：外部客戶從表單連結填寫 → 欄位錯誤不送出 → 確認同意前表單被改版，
 * 送出得到 409、重新載入 → 伺服器已寫入但回應在路上失敗，以同一個提交編號重送只得到同一張回執
 * （第二次是 200 不是 201）→ 回執可重新開啟；沒有填寫授權的帳號打不開表單；資料管理者讀到剛好一筆。
 *
 * 第六個 `it`（M4 issue #146）：資料管理者在「收集紀錄」看到上一個 `it` 送出的那一筆 → 外部客戶在
 * 「對話與回報紀錄」看到自己的回執並撤回（先確認）→ 回執與時間軸都只剩不含內容的撤回軌跡，紀錄清單
 * 是 0 筆 → 再撤回一次得到同一個撤回時間 → 資料管理者不能代為撤回（403 `submission-withdrawal`）。
 *
 * 數據庫目前沒有刪除功能，每次執行以不同名稱建立一個新的，可以對同一個資料庫重跑。
 * 六個 `it` 依序共用第一個建立的數據庫網址。
 */

const DATABASE_PATH = /^\/app\/databases\/[0-9a-f-]{36}\/form$/;

describe('databases against the real API', () => {
  const databaseName = `E2E 滿意度 ${Date.now()}`;
  let databasePath = '';

  it('creates a database from a template and shows its owner, purpose and initial form', () => {
    loginToApi('anxin', 'admin');

    cy.visit('/app/databases');
    cy.contains('h1', '數據庫').should('be.visible');
    cy.contains('正在載入資料庫').should('not.exist');
    cy.contains('目前無法載入資料庫').should('not.exist');

    cy.get('button[page-header-actions]').contains('新增資料庫').click();
    cy.get('fieldset.template-picker label.template-option').should('have.length', 5);
    cy.get('input[type="radio"][value="template-satisfaction"]').check({ force: true });
    cy.get('#database-name').should('have.value', '滿意度調查');

    // 可恢復的驗證錯誤：名稱留白不送出，改好後再建立。
    cy.get('#database-name').clear();
    cy.get('.create-panel button[type="submit"]').click();
    cy.get('#database-name-error').should('contain', '請輸入資料庫名稱。');
    cy.get('#database-name').should('have.attr', 'aria-invalid', 'true').type(databaseName);
    cy.get('.create-panel button[type="submit"]').click();

    cy.location('pathname', { timeout: 20000 })
      .should('match', DATABASE_PATH)
      .then((pathname) => {
        databasePath = pathname;
      });
    cy.contains('h1', databaseName).should('be.visible');
    cy.get('app-page-header').should('contain', '收集客戶對服務的評分與建議。');
    cy.get('.database-meta .database-owner').invoke('text').should('not.be.empty');
    cy.get('.database-meta').should('contain', '滿意度調查').and('contain', '3 個');
    cy.get('.database-meta time').should('have.attr', 'datetime');

    // 表單編輯與試填（#143）已開放：模板的三個欄位可以直接編輯。
    cy.get('app-form-designer').should('be.visible');
    cy.get('app-form-trial').scrollIntoView().should('be.visible');
    cy.get('.field-editor').should('have.length', 3);
    cy.get('.field-editor[data-field-id="field-overall-satisfaction"]').should('contain', '量尺');

    // 收集紀錄（#146）與趨勢比較（#147）都已開放：剛建立的數據庫沒有紀錄，兩個頁籤都是空白狀態。
    cy.get('nav.tabs').contains('a', '收集紀錄').click();
    cy.contains('還沒有收集紀錄').should('be.visible');
    cy.get('.upcoming-notice').should('not.exist');
    cy.get('nav.tabs').contains('a', '趨勢比較').click();
    cy.contains('還沒有收集紀錄').should('be.visible');
    cy.get('app-trend-chart').should('not.exist');

    cy.visit('/app/databases');
    cy.contains('tr', databaseName)
      .should('contain', '收集客戶對服務的評分與建議。')
      .and('contain', '3 個欄位・滿意度調查')
      .within(() => cy.get('time').should('have.attr', 'datetime'));
  });

  it('edits the form into a new version, marks a rejected field, and tries the saved form without saving a record', () => {
    expect(databasePath, 'the database created by the previous test').to.match(DATABASE_PATH);
    loginToApi('anxin', 'admin');
    cy.visit(databasePath);
    cy.get('app-form-designer').should('be.visible');

    // 欄位錯誤（伺服器 422）：名稱留白，錯誤標在那個欄位上，其他修改不丟。
    cy.get('#field-label-field-liked-services').clear();
    cy.get('#field-label-field-overall-satisfaction').clear().type('這次整體滿意度');
    cy.contains('button', '儲存表單').click();
    cy.get('app-form-designer [role="alert"]').should('contain', '還有欄位需要修正');
    cy.get('#field-error-field-liked-services').should('contain', '請填寫欄位名稱。');
    cy.get('#field-label-field-liked-services').should('have.attr', 'aria-invalid', 'true');
    cy.get('#field-label-field-overall-satisfaction').should('have.value', '這次整體滿意度');

    // 改好、再新增一個數字欄位，存成新版本。
    cy.get('#field-label-field-liked-services').type('喜歡的服務');
    cy.contains('button', '新增欄位').click();
    cy.get('.field-editor').should('have.length', 4);
    cy.get('.field-editor').last().find('input[type="text"]').first().type('消費金額');
    cy.get('.field-editor').last().find('select').select('數字');
    cy.get('.field-editor').last().find('input[id^="field-unit-"]').type('元');
    cy.get('.field-editor').last().find('input[id^="field-required-"]').check();
    cy.contains('button', '儲存表單').click();
    cy.get('.designer-feedback').should('contain', '表單已儲存：共 4 個欄位。');
    cy.get('app-form-designer [role="alert"]').should('not.exist');

    // 重新整理仍是新版；摘要的欄位數跟著變。
    cy.reload();
    cy.get('.field-editor').should('have.length', 4);
    cy.get('#field-label-field-overall-satisfaction').should('have.value', '這次整體滿意度');
    cy.get('.database-meta').should('contain', '4 個');

    // 試填：伺服器用與正式提交相同的規則驗證，只預覽、不建立紀錄。
    cy.contains('button', '送出試填').click();
    cy.get('app-form-trial').should('contain', '「這次整體滿意度」為必填。').and('contain', '「消費金額」為必填。');
    cy.contains('app-form-trial fieldset', '這次整體滿意度').within(() => cy.contains('label', '5').click());
    cy.contains('app-form-trial label', '消費金額').click();
    cy.focused().type('1200');
    cy.contains('button', '送出試填').click();
    cy.get('.trial-preview').should('contain', '5 / 5').and('contain', '1,200 元').and('contain', '不會儲存');
  });

  it('is not creatable without manage-data-sources and not visible to another organization', () => {
    expect(databasePath, 'the database created by the previous test').to.match(DATABASE_PATH);

    loginToApi('anxin', 'internal');
    cy.visit('/app/databases');
    cy.contains('正在載入資料庫').should('not.exist');
    cy.get('button[page-header-actions]').should('not.exist');
    cy.get('.notice').should('contain', '只有可管理資料來源的帳號可以建立資料庫');
    cy.contains(databaseName).should('not.exist');
    cy.visit(databasePath);
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains(databaseName).should('not.exist');

    loginToApi('control', 'admin');
    cy.visit('/app/databases');
    cy.contains('正在載入資料庫').should('not.exist');
    cy.contains(databaseName).should('not.exist');
    // 直接開網址：API 回 403 database，畫面不透露名稱或欄位。
    cy.visit(databasePath);
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains(databaseName).should('not.exist');
    cy.contains('整體滿意度').should('not.exist');
  });

  it('lets the owner designate a data manager who then sees the database read-only, until the designation is revoked', () => {
    expect(databasePath, 'the database created by the first test').to.match(DATABASE_PATH);
    const accessPath = databasePath.replace(/\/form$/, '/access');
    const toggleEmployee = (checked: boolean) => {
      cy.visit(accessPath);
      cy.contains('h3', '誰可以查看收集紀錄').should('be.visible');
      cy.contains('.choice', '內部同仁').find('input[type="checkbox"]')[checked ? 'check' : 'uncheck']();
      cy.contains('button', '儲存資料管理者').click();
      cy.get('[aria-live="polite"]').should('contain', '已更新資料管理者');
    };

    loginToApi('anxin', 'admin');
    cy.visit(accessPath);
    // 建立者一開始就是唯一的資料管理者；「已指定」與「目前可讀紀錄」分開顯示。
    cy.contains('.access-list > div', '已指定資料管理者').should('contain', '安心商行管理者');
    cy.contains('.access-list > div', '目前可讀紀錄').should('contain', '安心商行管理者');
    toggleEmployee(true);
    cy.contains('.access-list > div', '目前可讀紀錄').should('contain', '安心商行客服同仁');
    cy.contains('.access-list > div', '最後變更').should('contain', '由 安心商行管理者 變更');

    // 被指定、帳號也有「查看同意提交的紀錄」的同仁：清單與詳情看得到，唯讀。
    loginToApi('anxin', 'internal');
    cy.visit('/app/databases');
    cy.contains('正在載入資料庫').should('not.exist');
    cy.contains('tr', databaseName).should('be.visible');
    cy.visit(accessPath);
    cy.contains('.access-list > div', '你的權限').should('contain', '可查看收集紀錄與趨勢比較');
    // 詳情摘要多了「表單連結」（#145），提示可能在捲動容器的可視範圍外。
    cy.contains('只有這個資料庫的擁有者可以變更資料管理者').scrollIntoView().should('be.visible');
    cy.get('.choice input[type="checkbox"]').should('not.exist');
    cy.visit(databasePath);
    cy.get('ol.field-summary > li').should('have.length', 4);
    cy.get('app-form-designer').should('not.exist');

    // 擁有者撤銷指定：同一個工作階段重新整理就看不到，也不會透露名稱。
    loginToApi('anxin', 'admin');
    toggleEmployee(false);
    loginToApi('anxin', 'internal');
    cy.visit('/app/databases');
    cy.contains('正在載入資料庫').should('not.exist');
    cy.contains(databaseName).should('not.exist');
    cy.visit(databasePath);
    cy.contains('無法查看這個資料庫').should('be.visible');
    cy.contains(databaseName).should('not.exist');
  });

  it('lets a member submit through the form link only with consent, once, with a receipt, and refuses stale forms (#145)', () => {
    expect(databasePath, 'the database created by the first test').to.match(DATABASE_PATH);
    const databaseId = databasePath.split('/')[3];
    const formPath = `/app/forms/${databaseId}`;
    const apiPath = `/api/v1/databases/${databaseId}`;
    let adminAuth = '';

    const fillAnswers = () => {
      cy.contains('app-inline-form fieldset', '這次整體滿意度').within(() => cy.contains('label', '4').click());
      cy.contains('app-inline-form label', '消費金額').click();
      cy.focused().clear().type('1200');
      cy.contains('button', '下一步：確認同意').click();
      cy.get('app-consent-confirmation').should('be.visible');
    };

    // 擁有者沒有「填寫授權表單」權限：看得到表單連結，但打不開表單。順便取得擁有者的 token 來改表單。
    loginToApi('anxin', 'admin');
    cy.intercept('GET', apiPath).as('detail');
    cy.visit(databasePath);
    cy.wait('@detail').then((interception) => {
      adminAuth = String(interception.request.headers['authorization']);
    });
    cy.get('.database-meta a.form-link').should('have.attr', 'href', formPath);
    cy.visit(formPath);
    cy.contains('無法填寫這份表單').should('be.visible');
    cy.contains(databaseName).should('not.exist');

    loginToApi('anxin', 'customer');
    cy.visit(formPath);
    cy.contains('h1', databaseName).should('be.visible');
    cy.get('.intro').should('contain', `安心商行（${databaseName}）`).and('contain', '安心商行管理者');
    cy.get('.sensitive-notice').should('contain', '敏感');

    // 拒絕：必填沒填，伺服器的錯誤標在欄位上，不會進到同意步驟。
    cy.contains('button', '下一步：確認同意').click();
    cy.get('app-inline-form [role="alert"]').should('contain', '「這次整體滿意度」為必填。').and('contain', '「消費金額」為必填。');
    cy.get('app-consent-confirmation').should('not.exist');

    fillAnswers();
    cy.get('app-consent-confirmation').should('contain', '4 / 5').and('contain', '1,200 元');
    cy.contains('button', '同意並送出').should('be.disabled');
    cy.get('#consent-agree').check();

    // 成員還在考慮時，擁有者改了表單：送出得到 409，什麼都沒寫入，重新載入後是新版。
    cy.then(() =>
      cy.request({ url: apiPath, headers: { authorization: adminAuth } }).then((response) => {
        const form = response.body.form as { versionNumber: number; fields: { id: string; label: string }[] };
        const fields = form.fields.map((field) => (field.id === 'field-suggestion' ? { ...field, label: '其他建議（新版）' } : field));
        cy.request({
          method: 'PUT',
          url: `${apiPath}/form`,
          headers: { authorization: adminAuth },
          body: { baseVersionNumber: form.versionNumber, fields },
        });
      }),
    );
    cy.contains('button', '同意並送出').click();
    cy.get('.conflict[role="alert"]').should('contain', '這份表單已更新');
    cy.contains('button', '重新載入最新表單').click();
    cy.contains('app-inline-form label', '其他建議（新版）').scrollIntoView().should('be.visible');

    // 伺服器已經寫入、回應卻在路上失敗：同一個提交編號重送，只得到同一張回執（201 之後是 200）。
    const statuses: number[] = [];
    cy.intercept('POST', `${apiPath}/submissions`, (req) => {
      req.continue((res) => {
        statuses.push(res.statusCode);
        if (statuses.length === 1) res.send(503, {});
      });
    }).as('submit');
    fillAnswers();
    cy.get('#consent-agree').check();
    cy.contains('button', '同意並送出').click();
    cy.wait('@submit');
    cy.get('app-consent-confirmation [role="alert"]').should('contain', '不會重複建立紀錄');
    cy.contains('button', '同意並送出').click();
    cy.wait('@submit');
    cy.get('[data-kind="submission-receipt"]')
      .should('contain', '已送出')
      .and('contain', '4 / 5')
      .and('contain', '1,200 元')
      .and('contain', '表單連結')
      .and('contain', '安心商行管理者');
    cy.then(() => expect(statuses).to.deep.equal([201, 200]));
    cy.get('.receipt-number').invoke('text').should('match', /^R-\d{8}-[0-9A-F]{10}$/);
    cy.reload();
    cy.get('[data-kind="submission-receipt"]').should('contain', '1,200 元');

    // 指定且有權限的資料管理者（擁有者本人）讀到剛好一筆。
    cy.then(() =>
      cy
        .request({ url: `${apiPath}/records`, headers: { authorization: adminAuth } })
        .its('body.records')
        .should('have.length', 1),
    );
  });

  it('shows the record on the timeline, lets only its submitter withdraw it, and keeps a content-free trail (#146)', () => {
    expect(databasePath, 'the database created by the first test').to.match(DATABASE_PATH);
    const databaseId = databasePath.split('/')[3];
    const recordsPath = `/app/databases/${databaseId}/records`;
    let adminAuth = '';
    let customerAuth = '';
    let submissionId = '';

    // 資料管理者（擁有者本人）：上一個 `it` 的那一筆在時間軸上，追蹤對象是提交的帳號。
    loginToApi('anxin', 'admin');
    cy.intercept('GET', `/api/v1/databases/${databaseId}/tracking`).as('tracking');
    cy.visit(recordsPath);
    cy.wait('@tracking').then((interception) => {
      adminAuth = String(interception.request.headers['authorization']);
    });
    cy.get('#subject-select').should('contain', '外部客戶（1 筆）');
    cy.get('app-records-table').should('contain', '1,200 元').and('contain', '表單連結');
    cy.get('app-records-table .withdrawn').should('not.exist');

    // 趨勢比較（#147）：只有一筆有效紀錄，所以「紀錄不足」、不畫趨勢；期間統計由伺服器的固定查詢算好。
    // 定期報表（#150）在自己的頁籤，趨勢比較裡不再有「將於後續版本開放」。
    cy.intercept('GET', `/api/v1/databases/${databaseId}/queries/period-summary*`).as('summary');
    cy.visit(`/app/databases/${databaseId}/trends`);
    cy.wait('@summary').its('response.statusCode').should('eq', 200);
    cy.get('.insufficient-records').should('contain', '目前只有 1 筆紀錄').and('contain', '累積 2 筆以上');
    cy.get('.trend-conclusion').should('not.exist');
    cy.get('app-trend-chart').should('not.exist');
    cy.get('app-period-summary tr[data-row="record-count"]').should('contain', '1 筆');
    cy.get('app-period-summary').should('contain', '1,200 元');
    cy.get('.upcoming-notice').should('not.exist');
    cy.contains('a.records-link', '外部客戶的原始紀錄').should('have.attr', 'href').and('include', `/databases/${databaseId}/records`);

    // 定期報表（#150）：沒有助理設定排程的數據庫沒有報表，也不是錯誤；指定且有權限的管理者看得到這個頁籤。
    cy.intercept('GET', `/api/v1/databases/${databaseId}/reports`).as('reports');
    cy.visit(`/app/databases/${databaseId}/reports`);
    cy.wait('@reports').its('response.statusCode').should('eq', 200);
    cy.get('app-database-reports')
      .should('contain', '目前沒有助理為這個資料庫設定定期報表')
      .and('contain', '還沒有報表')
      .and('contain', '不會寄 Email 或 LINE');

    // 提交者：在「對話與回報紀錄」看到自己的回執，先確認再撤回。
    loginToApi('anxin', 'customer');
    cy.intercept('GET', '/api/v1/submissions').as('own');
    cy.visit('/app/activity');
    cy.wait('@own').then((interception) => {
      customerAuth = String(interception.request.headers['authorization']);
    });
    cy.contains('app-own-submissions li', databaseName)
      .should('contain', '有效')
      .then(($item) => {
        submissionId = String($item.attr('data-submission-id'));
      })
      .within(() => {
        cy.contains('button', '撤回').click();
        cy.get('.confirm').should('contain', '無法復原');
        cy.contains('button', '確認撤回').click();
      });
    cy.get('app-own-submissions [role="status"]').should('contain', '內容已刪除');
    cy.contains('app-own-submissions li', databaseName)
      .should('have.attr', 'data-withdrawn', 'true')
      .and('contain', '已撤回')
      .within(() => cy.contains('a', '查看回執').click());
    cy.get('[data-kind="submission-receipt"]')
      .should('have.attr', 'data-withdrawn', 'true')
      .and('contain', '你已在')
      .and('not.contain', '1,200 元')
      .and('not.contain', '4 / 5');

    // 再撤回一次：同一個撤回時間；資料管理者不能代為撤回。
    cy.then(() =>
      cy
        .request({ method: 'POST', url: `/api/v1/submissions/${submissionId}/withdrawal`, headers: { authorization: customerAuth }, body: {} })
        .then((first) =>
          cy
            .request({ method: 'POST', url: `/api/v1/submissions/${submissionId}/withdrawal`, headers: { authorization: customerAuth }, body: {} })
            .then((second) => {
              expect(second.status).to.equal(200);
              expect(second.body.withdrawnAt).to.equal(first.body.withdrawnAt);
              expect(second.body.entries).to.deep.equal([]);
            }),
        ),
    );
    cy.then(() =>
      cy
        .request({
          method: 'POST',
          url: `/api/v1/submissions/${submissionId}/withdrawal`,
          headers: { authorization: adminAuth },
          body: {},
          failOnStatusCode: false,
        })
        .then((response) => {
          expect(response.status).to.equal(403);
          expect(response.body.reason).to.equal('submission-withdrawal');
        }),
    );

    // 資料管理者：時間軸只剩撤回軌跡、沒有內容；紀錄清單是 0 筆。
    loginToApi('anxin', 'admin');
    cy.visit(recordsPath);
    cy.get('#subject-select').should('contain', '外部客戶（0 筆）');
    cy.get('app-records-table .no-usable-records').should('be.visible');
    cy.get('app-records-table .withdrawn').should('contain', '已撤回的紀錄（1 筆）').and('contain', '內容已移除');
    cy.get('app-records-table').should('not.contain', '1,200 元');
    cy.then(() =>
      cy
        .request({ url: `/api/v1/databases/${databaseId}/records`, headers: { authorization: adminAuth } })
        .its('body.records')
        .should('have.length', 0),
    );

    // 撤回後重新查詢就排除那一筆：期間統計是 0 筆、加總沒有 1,200 元，比較也沒有任何數字。
    cy.intercept('GET', `/api/v1/databases/${databaseId}/queries/period-summary*`).as('summaryAfter');
    cy.visit(`/app/databases/${databaseId}/trends`);
    cy.wait('@summaryAfter').its('response.statusCode').should('eq', 200);
    cy.get('app-period-summary tr[data-row="record-count"]').should('contain', '0 筆');
    cy.get('app-period-summary').should('not.contain', '1,200 元');
    cy.get('.insufficient-records').should('contain', '目前只有 0 筆紀錄');
  });
});

