function loginAs(persona: string): void {
  cy.visit('/login');
  cy.contains('button', persona).click();
  cy.location('pathname').should('eq', '/app/home');
}

const VALID_TOKEN = 'demo-token-not-for-production-0123456789abcdefghij';
const ASSISTANT = 'assistant-customer-service';
const EMPLOYEE_NAME = '安心商行客服同仁';

/** 在目前的 LINE 設定面板裡執行一組指令；每次呼叫都重新查詢，避免抓到已被重新渲染掉的節點。 */
function inLineSetup(steps: () => void): void {
  cy.get('app-line-setup').within(steps);
}

function ask(question: string): void {
  cy.get('#chat-input').clear().type(question);
  cy.get('form.composer button[type="submit"]').click();
}

function togglePlatformAccount(label: string): void {
  cy.visit(`/app/assistants/${ASSISTANT}/publishing?channel=platform`);
  cy.get('app-platform-sharing').within(() => {
    cy.contains('label', label).click();
    cy.contains('button', '儲存可使用的帳號').click();
    cy.get('[aria-live="polite"]').should('contain', '已更新可使用的帳號');
  });
}

describe('publishing channels', () => {
  beforeEach(() => {
    cy.clearLocalStorage();
  });

  it('shows three channel cards per assistant with five unified statuses and an isolated failure', () => {
    loginAs('SMB 管理者');
    cy.contains('nav a', '發布管道').click();
    cy.location('pathname').should('eq', '/app/channels');
    cy.contains('h1', '發布管道').should('be.visible');
    cy.contains('Demo，不會連接外部服務').should('be.visible');

    cy.contains('section.assistant-channels', '客服助理').within(() => {
      cy.get('app-channel-card').should('have.length', 3);
      cy.contains('app-channel-card', '平台內分享').should('contain', '已發布');
      cy.contains('app-channel-card', '官網嵌入').should('contain', '已發布');
      cy.contains('app-channel-card', 'LINE').should('contain', '需要處理').and('contain', '其他管道不受影響');
    });
    cy.contains('section.assistant-channels', '內部教育訓練助理').within(() => {
      cy.get('app-channel-card').should('have.length', 3);
    });
    for (const label of ['尚未設定', '測試中', '已發布', '需要處理', '已暫停']) {
      cy.contains('.channel-status__label', label).scrollIntoView().should('be.visible');
    }

    cy.contains('app-channel-card', 'LINE').contains('a', '前往處理').click();
    cy.location('pathname').should('eq', '/app/assistants/assistant-customer-service/publishing');
    cy.location('search').should('eq', '?channel=line');
    cy.get('app-line-setup').scrollIntoView().should('be.visible');
  });

  it('restricts platform sharing to chosen accounts', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/assistants/assistant-customer-service/publishing?channel=platform');
    cy.get('app-platform-sharing').within(() => {
      cy.contains('legend', '可使用的帳號');
      cy.contains('label', '外部客戶').click();
      cy.contains('button', '儲存可使用的帳號').click();
      cy.get('[aria-live="polite"]').should('contain', '已更新可使用的帳號');
    });
    cy.contains('app-channel-card', '平台內分享').should('contain', '已發布').and('contain', '1 個帳號');
  });

  it('previews the website widget on desktop and mobile, validates domains and copies demo embed code', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/assistants/assistant-customer-service/publishing?channel=website');
    cy.get('app-website-embed').within(() => {
      cy.get('.embed-preview').should('have.attr', 'data-device', 'desktop');
      cy.contains('button', '手機').click().should('have.attr', 'aria-pressed', 'true');
      cy.get('.embed-preview').should('have.attr', 'data-device', 'mobile');

      cy.get('#website-domain-input').type('https://shop.example.com/about');
      cy.contains('button', '加入網域').click();
      cy.get('#website-domain-input').should('have.attr', 'aria-invalid', 'true');
      cy.get('#website-domain-error').should('contain', '不要包含 https://');
      cy.get('#website-domain-input').clear().type('blog.anxin-demo.example');
      cy.contains('button', '加入網域').click();
      cy.get('.domain-list').should('contain', 'blog.anxin-demo.example');
      cy.contains('button', '儲存官網設定').click();
      cy.get('.save-status').should('contain', '已儲存');

      cy.get('.embed-code').should('contain', '不可用於正式環境');
      cy.window().then((win) => {
        cy.stub(win.navigator.clipboard, 'writeText').resolves();
      });
      cy.contains('button', '複製嵌入碼').click();
      cy.get('.copy-status').should('contain', '已複製');
      cy.contains('button', '檢查安裝狀態').click();
      cy.get('.install-status').should('contain', '模擬');
    });
    cy.contains('app-channel-card', '官網嵌入').should('contain', '已發布');
  });

  it('checks LINE fields item by item, masks secrets and sends a simulated test message', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/assistants/assistant-customer-service/publishing?channel=line');
    // 每一步都重新查詢 app-line-setup：儲存／測試／啟用都會讓面板重新渲染，
    // 長 within() 會讓後續指令跑在舊的（已從 DOM 卸下的）節點上，偶發失敗。
    inLineSetup(() => {
      cy.get('#line-accessToken').should('have.attr', 'type', 'password');
      cy.get('#line-channelSecret').should('have.attr', 'type', 'password');
      cy.get('button[aria-controls="line-accessToken"]').click();
    });
    inLineSetup(() => {
      cy.get('button[aria-controls="line-accessToken"]').should('have.attr', 'aria-pressed', 'true');
      cy.get('#line-accessToken').should('have.attr', 'type', 'text');

      cy.contains('.checklist li', 'Channel access token').should('have.attr', 'data-state', 'failed');
      cy.get('.error-summary').should('contain', 'Channel access token');
      cy.contains('button', '傳送測試訊息').should('be.disabled');

      // 打完字先確認欄位真的收到完整內容，再送出；否則掉字會讓下一個斷言誤報。
      cy.get('#line-accessToken').clear().type(VALID_TOKEN).should('have.value', VALID_TOKEN);
      cy.contains('button', '儲存並檢查').click();
    });
    inLineSetup(() => {
      cy.get('.checklist li[data-state="passed"]').should('have.length', 4);
      cy.get('.error-summary').should('not.exist');
      cy.contains('button', '傳送測試訊息').click();
    });
    inLineSetup(() => {
      cy.get('.test-result').should('contain', '測試訊息已送達').and('contain', '模擬');
      cy.contains('button', '確認啟用').click();
    });
    inLineSetup(() => {
      cy.contains('LINE 管道已啟用').should('be.visible');
    });
    cy.contains('app-channel-card', 'LINE').should('contain', '已發布');
    cy.contains('app-channel-card', '官網嵌入').should('contain', '已發布');
  });

  it('makes the ticked accounts decide who can open the assistant, and keeps their conversations', () => {
    // 一開始內部同仁在清單內：開得了助理，也留下一段對話。
    loginAs('內部使用者');
    cy.contains('section[aria-labelledby="usable-title"]', '客服助理').should('be.visible');
    cy.visit(`/app/chat/${ASSISTANT}`);
    ask('收到商品後幾天內可以退貨？');
    cy.get('ul.thread-list > li').should('have.length', 1);

    // 擁有者取消勾選，畫面說明對話不會被刪除。
    loginAs('SMB 管理者');
    togglePlatformAccount(EMPLOYEE_NAME);
    cy.get('app-platform-sharing [aria-live="polite"]').should('contain', '不會刪除');

    // 同仁立刻失去權限，拒絕訊息不提助理名稱。
    loginAs('內部使用者');
    cy.get('section[aria-labelledby="usable-title"]').should('not.exist');
    cy.visit(`/use/${ASSISTANT}`);
    cy.contains('無法使用這個助理').should('be.visible');
    cy.contains('客服助理').should('not.exist');
    cy.get('#chat-input').should('not.exist');

    // 重新勾選：權限回來，先前的對話原封不動。
    loginAs('SMB 管理者');
    togglePlatformAccount(EMPLOYEE_NAME);

    loginAs('內部使用者');
    cy.visit(`/app/chat/${ASSISTANT}`);
    cy.get('ul.thread-list > li').should('have.length', 1);
    cy.get('button.thread-open').should('contain', '退貨');
    cy.get('[role="log"]').should('contain', '7 天');
  });

  it("does not reveal another account's channel settings", () => {
    loginAs('內部使用者');
    cy.visit('/app/channels');
    cy.contains('h1', '發布管道').should('be.visible');
    cy.contains('目前沒有可設定發布管道的助理').should('be.visible');
    cy.get('app-channel-card').should('not.exist');

    cy.visit('/app/assistants/assistant-customer-service/publishing?channel=line');
    cy.contains('你沒有這個助理的設定權限').should('be.visible');
    cy.get('app-line-setup').should('not.exist');
    cy.contains('客服助理').should('not.exist');
  });
});
