import { loginAs } from '../support/a11y';

const ASSISTANT = 'assistant-customer-service';


function openTab(tab: string): void {
  cy.visit(`/app/assistants/${ASSISTANT}/${tab}`);
}

function sourceRow(name: string) {
  return cy.contains('.source-row', name);
}

describe('editing an assistant after it exists', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  describe('as the owner', () => {
    beforeEach(() => loginAs('SMB 管理者'));

    it('edits the audience on the overview tab and keeps it after a reload', () => {
      openTab('overview');
      cy.get('#assistant-name').should('have.value', '客服助理');
      cy.contains('尚未編輯過').should('be.visible');

      // 種子助理的使用對象是「內部員工與外部客戶」；取消內部員工後只剩外部客戶。
      cy.get('#audience-internal').should('be.checked').uncheck();
      cy.get('.autosave').should('contain', '已自動儲存');

      cy.reload();
      cy.get('#audience-internal').should('not.be.checked');
      cy.get('#audience-external').should('be.checked');
      cy.get('.autosave').should('contain', '上次儲存');
      cy.get('.rest-grid').scrollIntoView().should('be.visible').and('contain', '外部客戶');
    });

    it('renames the assistant and shows the new name in the list', () => {
      openTab('overview');
      cy.get('#assistant-name').clear().type('售後服務助理');
      cy.get('.autosave').should('contain', '已自動儲存');

      cy.contains('a', '返回我的助理').click();
      cy.contains('售後服務助理').should('be.visible');
    });

    it('refuses to leave the assistant without a name or an audience', () => {
      openTab('overview');

      cy.get('#audience-internal').uncheck();
      cy.get('.autosave').should('contain', '已自動儲存');

      cy.get('#assistant-name').clear();
      cy.get('#assistant-name-error').should('be.visible').and('contain', '請輸入助理名稱');
      cy.get('.autosave').should('contain', '請輸入助理名稱');

      cy.get('#audience-external').uncheck();
      cy.get('.settings-notice')
        .scrollIntoView()
        .should('be.visible')
        .and('contain', '至少要保留一種使用對象');
      cy.get('#audience-external').should('be.checked');
    });

    it('disconnects and reconnects a knowledge base from the data source tab', () => {
      openTab('data-sources');
      cy.get('.source-summary').should('contain', '3 個知識庫、2 個資料庫');

      sourceRow('商品使用指南').within(() => {
        cy.get('button').should('have.attr', 'aria-pressed', 'true').click();
        cy.get('button').should('have.attr', 'aria-pressed', 'false');
      });
      cy.get('.source-summary').should('contain', '2 個知識庫、2 個資料庫');

      cy.reload();
      sourceRow('商品使用指南').find('button').should('have.attr', 'aria-pressed', 'false');

      sourceRow('商品使用指南').find('button').click();
      cy.get('.source-summary').should('contain', '3 個知識庫、2 個資料庫');
    });

    it('refuses to disconnect the last remaining data source', () => {
      openTab('data-sources');
      for (const name of ['商品使用指南', '退換貨政策', '配送常見問題', '訂單資料庫']) {
        sourceRow(name).find('button').click();
      }
      cy.get('.source-summary').should('contain', '已連接 0 個知識庫、1 個資料庫');

      cy.contains('.source-row', '客戶資料庫').find('button').click();

      cy.get('app-assistant-sources-tab .error')
        .should('be.visible')
        .and('contain', '至少要連接一個知識庫或資料庫');
      cy.get('.source-summary').should('contain', '已連接 0 個知識庫、1 個資料庫');
    });

    it('toggles 嚴格回答 and 保存自己的對話, and both take effect in the chat', () => {
      openTab('rules');
      cy.get('#scope-general').should('be.checked');

      cy.get('#scope-strict').check();
      cy.get('#keep-conversations').should('be.checked').uncheck();
      cy.get('.autosave').should('contain', '已自動儲存');
      cy.contains('已經保存的對話不會被刪除').should('be.visible');

      cy.reload();
      cy.get('#scope-strict').should('be.checked');
      cy.get('#keep-conversations').should('not.be.checked');

      // 關掉之後開一段對話，紀錄欄不會留下這段對話。
      cy.visit(`/app/chat/${ASSISTANT}`);
      cy.get('#chat-input').type('保養皮革要注意什麼？');
      cy.get('form.composer button[type="submit"]').click();
      cy.get('[role="log"] [data-kind="general-knowledge"]').should('not.exist');

      // 重新打開保存後，先前保存過的對話會再次出現。
      openTab('rules');
      cy.get('#keep-conversations').check();
      cy.visit(`/app/chat/${ASSISTANT}`);
      cy.get('#chat-input').type('收到商品後幾天內可以退貨？');
      cy.get('form.composer button[type="submit"]').click();
      cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
    });

    it('turns 顯示引用出處 off and the chat stops offering the citation drawer', () => {
      openTab('test');
      cy.contains('會附上「查看引用來源」').should('be.visible');

      cy.visit(`/app/chat/${ASSISTANT}`);
      cy.get('#chat-input').type('收到商品後幾天內可以退貨？');
      cy.get('form.composer button[type="submit"]').click();
      cy.get('[role="log"] [data-kind="company-data"]').should('be.visible');
      // 訊息區是可捲動的容器，按鈕可能在視窗外；這裡只確認它存在。
      cy.contains('button', '查看引用來源').should('exist');

      openTab('rules');
      cy.get('#show-citations').should('be.checked').uncheck();
      cy.get('.autosave').should('contain', '已自動儲存');

      openTab('test');
      cy.contains('不會附上引用來源').should('be.visible');

      cy.visit(`/app/chat/${ASSISTANT}`);
      cy.get('#chat-input').type('收到商品後幾天內可以退貨？');
      cy.get('form.composer button[type="submit"]').click();
      // 仍然標示成組織資料，只是不再提供引用出處。
      // 先前已保存的回答不會被改寫，所以只看最新那一則。
      cy.get('[role="log"] [data-kind="company-data"]').last().within(() => {
        cy.contains('根據你的資料').should('exist');
        cy.get('button.citation-toggle').should('not.exist');
        cy.get('p.citation-notice').should('contain', '不顯示引用出處');
      });
    });

    it('turns 定期回報 off and the database trend tab stops showing the report', () => {
      cy.visit('/app/databases/database-customer-records/trends');
      cy.get('.periodic-report').should('contain', '客服助理').and('contain', '2026-10-15');

      openTab('rules');
      cy.get('#periodic-report').should('have.value', 'monthly').select('不需要');
      cy.get('.autosave').should('contain', '已自動儲存');

      cy.visit('/app/databases/database-customer-records/trends');
      cy.get('.periodic-report').should('not.exist');
      // 趨勢比較本身不受影響。
      cy.get('.trend-conclusion').should('be.visible');
    });

    it('only offers databases that are still connected as the write target', () => {
      openTab('rules');
      cy.get('#data-write-database option').should('contain', '訂單資料庫');

      openTab('data-sources');
      sourceRow('訂單資料庫').find('button').click();

      openTab('rules');
      cy.get('#data-write-database option').should('not.contain', '訂單資料庫');
    });
  });

  describe('as an account that does not own the assistant', () => {
    beforeEach(() => loginAs('內部使用者'));

    for (const tab of ['overview', 'data-sources', 'rules']) {
      it(`refuses the ${tab} editor without naming the assistant`, () => {
        openTab(tab);

        cy.contains('你沒有這個助理的設定權限').should('be.visible');
        cy.contains('客服助理').should('not.exist');
        cy.get('#assistant-name').should('not.exist');
        cy.get('app-source-connection-list').should('not.exist');
        cy.get('#keep-conversations').should('not.exist');
      });
    }
  });
});
