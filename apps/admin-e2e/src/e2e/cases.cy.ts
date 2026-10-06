import { loginAs } from '../support/a11y';

/**
 * 案件頁（issue #248，mock 模式）：內部同仁從側欄進入、只看得到自己能看的案件、建立案件
 * （選類型帶入承辦組與時限，時限早於現在時送出前就提示），以及看不到別人的案件。
 * mock 的範例：設備組（成員：客服同仁）、採購組（成員：管理者）；「冷藏庫溫度降不下來」由同仁建立、
 * 連結的對話已被刪除；「採購備用壓縮機」由管理者建立在採購組。流轉（issue #249）：同仁是設備組成員，
 * 可以受理「冷藏庫溫度降不下來」、要求補件、轉給採購組（之後仍看得到）、完成，以及從已結案的案件另開新案。
 */
describe('cases', () => {
  beforeEach(() => loginAs('內部使用者'));

  it('lists only the cases the employee may see and shows a deleted conversation as not openable', () => {
    cy.visit('/app/home');
    cy.contains('.app-sidenav a', '案件').click();
    cy.location('pathname').should('eq', '/app/cases');
    cy.contains('h1', '案件').should('be.visible');

    cy.contains('.case-item', '冷藏庫溫度降不下來').should('be.visible');
    cy.contains('.case-item', '採購備用壓縮機').should('not.exist');
    cy.contains('.case-item', '灌溉馬達異音').should('not.exist');

    cy.contains('.case-item', '冷藏庫溫度降不下來').click();
    cy.location('search').should('eq', '?case=case-cold-room');
    cy.get('[data-case-detail]').within(() => {
      cy.contains('h3', '冷藏庫溫度降不下來').should('be.visible');
      cy.contains('待受理').should('be.visible');
      cy.get('[data-case-link="thread"]').should('contain.text', '這個對話無法開啟');
      cy.get('[data-case-link="thread"] a').should('not.exist');
    });

    cy.get('.cases-filters select').eq(1).select('已結案');
    cy.contains('.case-item', '灌溉馬達異音').should('be.visible');
    cy.contains('.case-item', '冷藏庫溫度降不下來').should('not.exist');
  });

  it('creates a case from a type, refusing a due time in the past before sending', () => {
    cy.visit('/app/cases');
    cy.get('[data-case-create]').click();
    cy.get('[data-case-form]').within(() => {
      cy.get('#case-type').select('設備故障報修');
      cy.get('#case-group').find('option:selected').should('have.text', '設備組');
      cy.get('#case-due').invoke('val').should('match', /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);

      cy.get('#case-due').clear().type('2020-01-01T09:00');
      cy.contains('#case-due-error', '時限不能早於現在。').should('be.visible');
      cy.get('#case-title').type('溫控器顯示錯誤代碼');
      cy.contains('button', '建立案件').click();
      cy.contains('#case-due-error', '時限不能早於現在。').should('be.visible');
    });
    cy.contains('.case-item', '溫控器顯示錯誤代碼').should('not.exist');

    cy.get('[data-case-form] #case-due').clear().type('2099-12-31T09:00');
    cy.get('#case-due-error').should('not.exist');
    cy.get('[data-case-form] #case-description').type('三號溫室溫控器顯示 E3。');
    cy.get('[data-case-form]').contains('button', '建立案件').click();

    cy.get('[data-case-detail]').within(() => {
      cy.contains('h3', '溫控器顯示錯誤代碼').should('be.visible');
      cy.contains('待受理').should('be.visible');
      cy.contains('設備組').should('be.visible');
      cy.contains('三號溫室溫控器顯示 E3。').scrollIntoView().should('be.visible');
      cy.contains('.case-history li', '建立案件').scrollIntoView().should('be.visible');
    });
    cy.contains('.case-item', '溫控器顯示錯誤代碼').scrollIntoView().should('be.visible');
  });

  it('does not show a case the employee may not see', () => {
    cy.visit('/app/cases?case=case-compressor-purchase');
    cy.get('[data-state="permission-denied"]')
      .should('be.visible')
      .and('contain.text', '無法查看這件案件')
      .and('contain.text', '你沒有這個案件的存取權限，或它已不存在。');
    cy.contains('採購備用壓縮機').should('not.exist');
  });

  it('accepts a case, asks for more information and transfers it, still seeing it afterwards', () => {
    cy.visit('/app/cases?case=case-cold-room');
    cy.get('[data-case-actions] [data-case-action]').should('have.length', 3);
    cy.get('[data-case-action="accept"]').click();

    cy.get('[data-case-detail]').within(() => {
      cy.contains('.case-status', '處理中').scrollIntoView().should('be.visible');
      cy.contains('.case-facts div', '案件負責人').should('contain.text', '安心商行客服同仁');
      cy.contains('[data-case-history] li', '受理').scrollIntoView().should('contain.text', '安心商行客服同仁');
    });

    cy.get('[data-case-action="request-info"]').click();
    cy.get('[data-case-action-form] #case-action-text').type('請補上溫度紀錄的照片');
    cy.get('[data-case-action-form]').contains('button', '送出補件要求').click();
    cy.contains('[data-case-detail] .case-status', '待補件').scrollIntoView().should('be.visible');

    cy.get('[data-case-action="transfer"]').click();
    cy.get('[data-case-action-form] #case-action-group').select('採購組');
    cy.get('[data-case-action-form] #case-action-text').type('需要採購壓縮機');
    cy.get('[data-case-action-form]').contains('button', '確認轉組').click();

    cy.get('[data-case-detail]').within(() => {
      cy.contains('.case-status', '待受理').scrollIntoView().should('be.visible');
      cy.contains('.case-facts div', '承辦組').should('contain.text', '採購組');
      cy.contains('.case-facts div', '案件負責人').should('contain.text', '尚未受理');
      cy.contains('[data-case-history] li', '轉組（設備組 → 採購組）：需要採購壓縮機').scrollIntoView().should('be.visible');
    });
    cy.get('[data-case-action="accept"]').should('not.exist');
    cy.get('[data-case-actions] [data-case-action]').should('have.length', 2);
    cy.contains('.case-item', '冷藏庫溫度降不下來').should('contain.text', '採購組');
  });

  it('completes a case with a resolution and opens a new case that links the old one', () => {
    cy.visit('/app/cases?case=case-cold-room');
    cy.get('[data-case-action="accept"]').click();
    cy.contains('[data-case-detail] .case-status', '處理中').scrollIntoView().should('be.visible');

    cy.get('[data-case-action="complete"]').click();
    cy.get('[data-case-action-form]').contains('button', '確認完成').click();
    cy.contains('#case-action-text-error', '請填寫處理結果。').scrollIntoView().should('be.visible');
    cy.get('[data-case-action-form] #case-action-text').type('已更換溫控器');
    cy.get('[data-case-action-form]').contains('button', '確認完成').click();

    cy.get('[data-case-detail]').within(() => {
      cy.contains('.case-status', '已完成').scrollIntoView().should('be.visible');
      cy.contains('.case-facts div', '處理結果').should('contain.text', '已更換溫控器');
      cy.contains('[data-case-history] li', '完成').scrollIntoView().should('contain.text', '已更換溫控器');
    });
    cy.get('[data-case-actions]').should('not.exist');

    cy.get('[data-case-follow-up]').scrollIntoView().click();
    cy.contains('[data-case-follow-up-note]', '冷藏庫溫度降不下來').scrollIntoView().should('be.visible');
    cy.get('[data-case-form] #case-type').find('option:selected').should('have.text', '設備故障報修');
    cy.get('[data-case-form] #case-title').should('have.value', '冷藏庫溫度降不下來');
    cy.get('[data-case-form] #case-description').type('更換後又開始升溫。');
    cy.get('[data-case-form]').contains('button', '建立案件').click();

    cy.get('[data-case-detail]').within(() => {
      cy.contains('.case-status', '待受理').scrollIntoView().should('be.visible');
      cy.get('[data-case-link="previous"]').scrollIntoView().should('contain.text', '接續的舊案件');
    });
  });

  /**
   * 逾期提示（issue #250）：mock 的「溫室感測器離線」在設備組待受理、時限已過，所以同仁的側欄數字至少是 1。
   * 數字取決於今天的日期（其他範例案件之後也會逾期），因此這裡比對「側欄＝兩個篩選的件數合計」。
   */
  it('shows the overdue number beside 案件, equal to the two overdue filters the home page card links to', () => {
    cy.visit('/app/home');
    cy.get('.app-sidenav a[href="/app/cases"] .nav-count').invoke('text').then((text) => {
      const sideNav = Number(text.trim());
      expect(sideNav).to.be.at.least(1);
      cy.get('.app-sidenav a[href="/app/cases"]').should('have.attr', 'aria-label', `案件，${sideNav} 件逾期`);

      cy.get('[data-home-cases]').scrollIntoView().should('be.visible')
        .and('contain.text', `已逾期 ${sideNav} 件`);
      cy.get('[data-home-cases-link="owned-overdue"]').click();
      cy.location('search').should('eq', '?scope=owned&overdue=true');
      cy.get('[data-case-overdue-filter]').should('be.checked');
      listedCount().then((owned) => {
        cy.visit('/app/home');
        cy.get('[data-home-cases-link="group-overdue"]').scrollIntoView().click();
        cy.location('search').should('eq', '?scope=my-groups&status=pending&overdue=true');
        cy.contains('.case-item', '溫室感測器離線').should('contain.text', '已逾期');
        listedCount().then((groupPending) => {
          expect(owned + groupPending).to.eq(sideNav);
        });
      });
    });

    cy.get('[data-case-overdue-filter]').uncheck();
    cy.contains('.case-item', '冷藏庫溫度降不下來').should('be.visible');
  });

  it('shows an external customer no overdue number and no 案件 card', () => {
    loginAs('外部客戶');
    cy.visit('/app/home');
    cy.contains('h2', '待處理事項').scrollIntoView().should('be.visible');
    cy.get('[data-home-cases]').should('not.exist');
    cy.get('.app-sidenav a[href="/app/cases"]').should('exist').find('.nav-count').should('not.exist');
  });

  /**
   * 瓶頸統計（issue #251）：只有管理者有這個分頁；每個數字點開的清單件數與該格相同。數字取決於今天的日期
   * （範例案件會陸續逾期，完成的範例會離開最近 30 天），所以比對「格子＝清單件數」，不寫死件數。
   */
  it('shows the manager the bottleneck statistics, each number opening a list of the same length', () => {
    cy.visit('/app/cases?view=statistics');
    cy.get('[data-cases-tab]').should('not.exist');
    cy.get('[data-case-statistics]').should('not.exist');
    cy.contains('.case-item', '冷藏庫溫度降不下來').should('be.visible');

    loginAs('SMB 管理者');
    cy.visit('/app/cases');
    cy.get('[data-cases-tab="statistics"]').click();
    cy.location('search').should('eq', '?view=statistics');
    cy.get('[data-case-statistics]').should('contain.text', '日期以 UTC 計算');
    cy.get('[data-case-statistics-period]').should('contain.text', '（UTC）');
    // 採購組只有一件未結案、沒有完成件數：平均處理時間是「—」。
    cy.contains('[data-case-statistics-row]', '採購組').find('[data-case-statistics-average]').should('have.text', '—');

    for (const measure of ['open', 'overdue', 'completed', 'cancelled'] as const) {
      cy.visit('/app/cases?view=statistics');
      cy.contains('[data-case-statistics-row]', '設備組').find(`[data-case-statistics-link="${measure}"]`).then(($link) => {
        const expected = Number($link.text().trim());
        cy.wrap($link).click();
        cy.location('search').should('contain', 'typeId=case-type-equipment-repair').and('contain', 'groupId=case-group-equipment');
        cy.get('[data-case-statistics]').should('not.exist');
        listedCount().should('eq', expected);
      });
    }
    cy.get('[data-case-closed-range]').should('contain.text', '完成或取消的案件');
    cy.get('[data-cases-tab="statistics"]').click();
    cy.get('[data-case-statistics]').should('be.visible');
  });
});

/** 清單讀完後的件數（沒有符合的案件時是 0）。 */
function listedCount(): Cypress.Chainable<number> {
  cy.get('.cases-list .case-items, .cases-list [data-state="empty"]').should('exist');
  return cy.get('.cases-list').then(($list) => $list.find('.case-item').length);
}
