import { loginAs } from '../support/a11y';

/**
 * 轉真人通知（issue #355，mock 模式）：官網或 LINE 的訪客收到助理的「找不到資料時的回覆」時通知助理的擁有者。
 * mock 的範例：客服助理（擁有者：管理者）在 LINE 與官網各有一則待處理、官網一則已處理；客服同仁不是擁有者，什麼都看不到。
 */
describe('handoffs', () => {
  const navLink = '.app-sidenav a[href="/app/handoffs"]';

  it('shows the owner the pending number beside 轉真人 and the list, and marks one handled', () => {
    loginAs('SMB 管理者');
    cy.get(`${navLink} .nav-count`).invoke('text').should((text) => expect(text.trim()).to.eq('2'));
    cy.get(navLink).should('have.attr', 'aria-label', '轉真人，2 則待處理').click();

    cy.location('pathname').should('eq', '/app/handoffs');
    cy.get('#main-content').contains('h1', '轉真人通知').should('be.visible');
    cy.get('[data-handoff]').should('have.length', 2);
    cy.get('[data-handoff]').first().within(() => {
      cy.get('[data-channel="line"]').should('have.text', 'LINE');
      cy.contains('客服助理').should('be.visible');
      cy.contains('a', '開啟 LINE 聊天').should('have.attr', 'href', 'https://chat.line.biz/');
      cy.contains('a', '查看 LINE 發布設定').should('have.attr', 'href', '/app/assistants/assistant-customer-service/publishing?channel=line');
      cy.contains('button', '標記已處理').click();
    });

    cy.contains('[role="status"]', '已將「客服助理」LINE 的轉真人標記為已處理。').scrollIntoView().should('be.visible');
    cy.get('[data-handoff]').should('have.length', 1);
    cy.get('[data-handoff] [data-channel="website"]').should('be.visible');
    cy.get(`${navLink} .nav-count`).invoke('text').should((text) => expect(text.trim()).to.eq('1'));

    cy.get('[data-handoff-filter="handled"]').click().should('have.attr', 'aria-pressed', 'true');
    cy.get('[data-handoff]').should('have.length', 2);
    cy.get('[data-handoff]').first().find('[data-handoff-handled]').should('contain.text', '已處理：安心商行管理者');
    cy.get('[data-handoff] button').should('not.exist');
  });

  it('shows a member who owns no published assistant nothing to handle', () => {
    loginAs('內部使用者');
    cy.get(`${navLink} .nav-count`).should('not.exist');
    cy.get(navLink).click();
    cy.get('#main-content').contains('目前沒有待處理的轉真人').should('be.visible');
    cy.get('[data-handoff]').should('not.exist');
  });

  it('lets an account set its notification Email and turn the 轉真人 Email off', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/settings');
    cy.get('[data-notification-settings]').scrollIntoView().within(() => {
      cy.contains('h2', '我的通知').scrollIntoView().should('be.visible');
      cy.get('[data-handoff-emails]').should('be.checked');
      cy.get('#notification-email').type('a@b');
      cy.contains('button', '儲存通知設定').click();
      // The settings page scrolls inside the workspace's content area: scroll before checking visibility.
      cy.contains('#notification-email-error', '請輸入有效的 Email 地址').scrollIntoView().should('be.visible');
      cy.get('#notification-email').should('have.attr', 'aria-invalid', 'true');

      cy.get('#notification-email').clear().type('owner@shop.example.org');
      cy.get('#notification-email-error').should('not.exist');
      cy.get('[data-handoff-emails]').uncheck();
      cy.contains('button', '儲存通知設定').click();
      cy.contains('[role="status"]', '已儲存。不會寄轉真人的 Email 給你。').scrollIntoView().should('be.visible');
      cy.get('#notification-email').should('have.value', 'owner@shop.example.org');
      cy.get('[data-handoff-emails]').should('not.be.checked');
    });
  });
});
