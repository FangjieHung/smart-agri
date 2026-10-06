import { loginAs } from '../support/a11y';

/**
 * 團隊與權限（`/app/settings`）與資料庫的「權限」頁籤。
 *
 * 兩個畫面各管一層，兩層都通過才看得到收集紀錄：
 * 帳號層級的「查看同意提交的紀錄」在團隊設定，資料庫層級的資料管理者指定在權限頁籤。
 * 權限存在 `localStorage`（`sme-demo:` 開頭），所以切換身分後仍然生效。
 */
describe('team management and data access', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('lists the team with each role and what that role can do', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/settings');

    cy.contains('h2', '團隊與權限').should('be.visible');
    cy.contains('不是真實的身分管理').should('be.visible');
    cy.get('.member').should('have.length', 3);
    cy.contains('.member', '安心商行管理者').should('contain', '目前的身分');
    cy.contains('.member', '安心商行客服同仁').should('contain', '使用團隊分享的助理');
    cy.contains('.member', '外部客戶').should('contain', '查看自己的紀錄');
  });

  it('refuses the team editor to a non-admin persona without naming anyone', () => {
    loginAs('內部使用者');
    cy.visit('/app/settings');

    cy.contains('無法查看團隊設定').should('be.visible');
    cy.get('.member').should('not.exist');
    cy.get('#permission-account-smb-admin-manage-assistants').should('not.exist');
    cy.contains('安心商行管理者').should('not.exist');
    // 外觀設定不需要權限，仍然在（「對話保存」之後已在可視範圍外，先捲過去）。
    cy.contains('h2', '外觀設定').scrollIntoView().should('be.visible');
  });

  it('shows the single mock chat model to every persona without a menu (issue #240)', () => {
    for (const persona of ['SMB 管理者', '內部使用者']) {
      loginAs(persona);
      cy.visit('/app/settings');
      // 設定頁在捲動容器裡：先捲到區塊，否則 `be.visible` 會因為被裁切而假失敗。
      cy.get('[data-chat-model-panel]').scrollIntoView().within(() => {
        cy.contains('h2', '對話模型').should('be.visible');
        cy.contains('目前使用：fake-chat-dev').should('be.visible');
        cy.get('select').should('not.exist');
      });
    }
  });

  it('lets only the manager manage case groups, with internal accounts as the only members (issue #246)', () => {
    loginAs('內部使用者');
    cy.visit('/app/settings');
    cy.contains('h2', '外觀設定').should('exist');
    cy.get('[data-case-groups-panel]').should('not.exist');

    loginAs('SMB 管理者');
    cy.visit('/app/settings');
    cy.get('[data-case-groups-panel]').scrollIntoView().within(() => {
      cy.contains('h2', '承辦組').should('be.visible');
      cy.get('[data-case-group="舊倉儲組"]').should('contain', '已封存');

      cy.get('#case-group-new-name').type('品保組');
      cy.contains('button', '建立承辦組').click();
      cy.get('[role="status"]').should('contain', '已建立承辦組「品保組」。');

      cy.contains('button', '編輯「品保組」的成員').click();
      cy.get('[data-case-group-members-editor]').within(() => {
        cy.get('label').should('have.length', 2);
        cy.contains('外部客戶').should('not.exist');
        cy.contains('label', '安心商行客服同仁').click();
        cy.contains('button', '儲存成員').click();
      });
      cy.get('[data-case-group="品保組"]').should('contain', '成員：安心商行客服同仁');

      cy.contains('button', '「品保組」的成員異動').click();
      cy.get('[data-case-group-history]').should('contain', '加入了 安心商行客服同仁');

      cy.contains('button', '封存「品保組」').click();
      cy.get('[data-case-group="品保組"]').should('contain', '已封存');
      cy.contains('button', '取消封存「品保組」').click();
      cy.get('[data-case-group="品保組"]').should('not.contain', '已封存');
    });
  });

  it('shortens the retention through a confirmation, shows the buffer and goes back (issue #243)', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/settings');
    cy.get('[data-conversation-retention]').scrollIntoView().within(() => {
      cy.contains('h2', '對話保存').should('be.visible');
      cy.contains('已轉給專人的問答會保留在處理事項中').should('be.visible');
      cy.get('#retention-days-select').should('have.value', 'forever').select('30 天');
    });

    cy.get('[role="dialog"]').should('be.visible').within(() => {
      cy.contains('把保存期限縮短為 30 天？').should('be.visible');
      cy.get('[data-retention-preview-count]').should('contain', '大約會刪除').and('contain', '串對話');
      cy.contains('button', '縮短為 30 天').click();
    });
    cy.get('[role="dialog"]').should('not.exist');
    cy.get('[data-retention-pending]').scrollIntoView().should('be.visible').and('contain', '起改為 30 天');
    cy.get('#retention-status').should('contain', '起生效');

    cy.contains('[data-retention-pending] button', '改回').click();
    cy.get('[data-retention-pending]').should('not.exist');
    cy.get('#retention-days-select').should('have.value', 'forever');
    cy.get('#retention-status').should('contain', '已改回 永久');
  });

  it('shows the retention read-only to a member (issue #243)', () => {
    loginAs('內部使用者');
    cy.visit('/app/settings');
    cy.get('[data-conversation-retention]').scrollIntoView().within(() => {
      cy.contains('h3', '保存期限：永久').should('be.visible');
      cy.contains('只有管理者可以變更保存期限').should('be.visible');
      cy.contains('已轉給專人的問答會保留在處理事項中').should('be.visible');
      cy.get('select').should('not.exist');
      cy.get('button').should('not.exist');
    });
  });

  it('will not let the admin lock themselves out of the team screen', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/settings');

    cy.contains('button', '變更 安心商行管理者 的權限').click();
    cy.get('#permission-account-smb-admin-manage-assistants')
      .should('be.disabled')
      .and('be.checked');
    cy.contains('.member__editor', '不可移除').should('be.visible');
  });

  it('revokes an employee’s record permission and the employee sees it on their own database', () => {
    loginAs('內部使用者');
    cy.visit('/app/databases/database-staff-checkins/records');
    cy.contains('還沒有收集紀錄').should('be.visible');
    cy.visit('/app/databases/database-staff-checkins/access');
    cy.get('.access-list').should('contain', '可查看收集紀錄與趨勢比較');

    loginAs('SMB 管理者');
    cy.visit('/app/settings');
    cy.contains('button', '變更 安心商行客服同仁 的權限').click();
    cy.get('#permission-account-internal-employee-read-consented-submissions')
      .should('be.checked')
      .click();
    cy.contains('button', '儲存 安心商行客服同仁 的權限').click();
    cy.get('[aria-live="polite"]').should('contain', '已更新 安心商行客服同仁 的權限');

    loginAs('內部使用者');
    cy.visit('/app/databases/database-staff-checkins/records');
    cy.contains('無法查看收集紀錄').should('be.visible');
    cy.contains('還沒有收集紀錄').should('not.exist');
    cy.visit('/app/databases/database-staff-checkins/access');
    cy.get('.access-list').should('contain', '還沒有「查看同意提交的紀錄」權限');

    // 加回來就恢復：權限變更沒有刪掉任何東西。
    loginAs('SMB 管理者');
    cy.visit('/app/settings');
    cy.contains('button', '變更 安心商行客服同仁 的權限').click();
    cy.get('#permission-account-internal-employee-read-consented-submissions').click();
    cy.contains('button', '儲存 安心商行客服同仁 的權限').click();

    loginAs('內部使用者');
    cy.visit('/app/databases/database-staff-checkins/records');
    cy.contains('還沒有收集紀錄').should('be.visible');
  });

  it('gates 收集紀錄 from the database access tab both ways, keeping the records intact', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-customer-records/records');
    cy.get('ol.timeline > li').should('have.length', 4);

    cy.contains('nav.tabs a', '權限').click();
    cy.location('pathname').should('eq', '/app/databases/database-customer-records/access');
    cy.contains('誰可以查看收集紀錄').should('be.visible');
    cy.get('#data-manager-account-smb-admin').should('be.checked').click();
    cy.contains('button', '儲存資料管理者').click();
    cy.get('[aria-live="polite"]')
      .should('contain', '已更新資料管理者')
      .and('contain', '沒有被刪除');
    // 按下儲存後焦點留在按鈕上、頁面已往下捲，所以驗內容而不是驗可見。
    cy.get('.access-list').should('contain', '可管理表單設定，不能查看收集紀錄');

    cy.visit('/app/databases/database-customer-records/records');
    cy.contains('無法查看收集紀錄').should('be.visible');
    cy.contains('王小姐').should('not.exist');
    // 拒絕時也不透露還有幾筆。
    cy.visit('/app/databases');
    // 列表已從卡片改成可整列點開的 data-table。
    cy.contains('tr', '客戶資料庫')
      .should('contain', '僅指定資料管理者可查看')
      .and('not.contain', '筆紀錄');

    cy.visit('/app/databases/database-customer-records/access');
    cy.get('#data-manager-account-smb-admin').should('not.be.checked').click();
    cy.contains('button', '儲存資料管理者').click();
    cy.visit('/app/databases/database-customer-records/records');
    cy.get('ol.timeline > li').should('have.length', 4);
  });

  it('warns when a designated data manager still lacks the account permission', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/databases/database-customer-records/access');

    cy.contains('label', '外部客戶').should('contain', '指定了也看不到');
    cy.get('#data-manager-account-external-customer').click();
    cy.contains('button', '儲存資料管理者').click();
    cy.get('[aria-live="polite"]').should('contain', '還沒有「查看同意提交的紀錄」權限');
  });

  it('takes publishing away from the admin without evicting the people already using it', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/channels');
    cy.contains('客服助理').should('be.visible');

    cy.visit('/app/settings');
    cy.contains('button', '變更 安心商行管理者 的權限').click();
    cy.get('#permission-account-smb-admin-manage-publishing').should('be.checked').click();
    cy.contains('button', '儲存 安心商行管理者 的權限').click();
    cy.get('[aria-live="polite"]').should('contain', '已更新 安心商行管理者 的權限');

    cy.visit('/app/channels');
    cy.contains('目前沒有可設定發布管道的助理').should('be.visible');
    cy.visit('/app/assistants/assistant-customer-service/publishing');
    cy.contains('無法檢視發布設定').should('be.visible');

    // 已分享的帳號照常使用：收回的是設定權限，不是使用權限。
    loginAs('內部使用者');
    cy.visit('/app/chat/assistant-customer-service');
    cy.contains('客服助理').should('be.visible');
  });
});
