import { auditA11y, forEachColorTheme, loginAs } from '../support/a11y';
import { SHORT_WINDOWS, expectFullyOnScreen, expectScrollingBody } from '../support/on-screen';

const VALID_TOKEN = 'demo-token-not-for-production-0123456789abcdefghij';
const ASSISTANT = 'assistant-customer-service';
const EMPLOYEE_NAME = '安心商行客服同仁';
/** #291：擁有者自己的「收到非文字訊息時的回覆」，含換行與 emoji。 */
const OWN_NON_TEXT_REPLY = '收到您的照片了 📷\n目前只能看懂文字，\n請用文字描述問題 🙏';

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
    // 首頁卡片現在連到處理事項；此情境直接開啟發布管道頁驗證管道狀態。
    cy.visit('/app/channels');
    cy.location('pathname').should('eq', '/app/channels');
    cy.contains('h1', '發布管道').should('be.visible');
    // 頁面說明已改寫，不再顯示「Demo，不會連接外部服務」字樣（ChannelOverviewPageComponent
    // 自己的 spec 也明確斷言這段文字已移除），改成描述管道彼此獨立的行為。
    cy.contains('管道各自獨立').should('be.visible');

    cy.contains('section.assistant-channels', '客服助理').within(() => {
      cy.get('app-channel-card').should('have.length', 3);
      // 管道名稱已從「平台內分享」改成「組織內部分享」，呼應「組織建立的／組織資料」的用詞。
      cy.contains('app-channel-card', '組織內部分享').should('contain', '已發布');
      cy.contains('app-channel-card', '官網嵌入').should('contain', '已發布');
      cy.contains('app-channel-card', 'LINE').should('contain', '需要處理').and('contain', '連線測試未通過');
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
    cy.contains('app-channel-card', '組織內部分享').should('contain', '已發布').and('contain', '1 個帳號');
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
      cy.get('.embed-code').should('not.contain', 'data-brand');
      cy.get('#website-color-ocean').check();
      cy.contains('button', '儲存官網設定').click();
      cy.get('.save-status').should('contain', '已儲存');

      cy.get('.embed-code').should('contain', '不可用於正式環境');
      // 啟動按鈕的品牌色由載入器從網頁讀取，嵌入碼要帶出來（#225）。
      cy.get('.embed-code').should('contain', 'data-brand="ocean"');
      cy.window().then((win) => {
        cy.stub(win.navigator.clipboard, 'writeText').resolves();
      });
      cy.contains('button', '複製嵌入碼').click();
      cy.get('.copy-status').should('contain', '已複製');
      // 安裝偵測是被動的：只顯示最後一次偵測到的時間，沒有「檢查安裝狀態」按鈕。
      cy.contains('button', '檢查安裝狀態').should('not.exist');
      cy.get('.seen-list')
        .should('contain', '最後一次在 shop.anxin-demo.example 偵測到')
        .and('contain', 'blog.anxin-demo.example：尚未偵測到');
    });
    cy.contains('app-channel-card', '官網嵌入').should('contain', '已發布');
  });

  it('publishes the website channel only after a confirmation that lists what visitors can ask, and can pause and unpublish it', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/assistants/assistant-customer-service/publishing?channel=website');
    cy.get('app-website-embed').within(() => {
      cy.get('.serving__label').should('contain', '已發布');
      cy.get('.serving__reason').should('contain', '服務中');
      cy.contains('button', '取消發布').click();
      cy.get('.action-status').should('contain', '已取消發布');
      cy.get('.serving__label').should('contain', '測試中');
      cy.contains('button', '發布官網嵌入').click();
    });
    // 發布確認對話框在元件外（overlay）：列出訪客可以問到的知識庫。
    cy.get('.publish-panel').within(() => {
      cy.contains('訪客可以問到下列知識庫');
      cy.contains('li', '商品使用指南');
      cy.contains('button', '確認發布').click();
    });
    cy.get('.publish-panel').should('not.exist');
    cy.get('app-website-embed').within(() => {
      cy.get('.action-status').should('contain', '已發布官網嵌入');
      cy.contains('button', '暫停服務').click();
      cy.get('.serving__label').should('contain', '已暫停');
      cy.contains('button', '恢復服務').click();
      cy.get('.serving__label').should('contain', '已發布');
    });
  });

  // issue #288：發布確認對話框的知識庫清單由 @for 產生，連接的知識庫很多時會把按鈕推出矮視窗。
  // 現在只有說明那段捲動；寫法比照 team-and-access.cy.ts 的 #284。
  for (const [width, height] of SHORT_WINDOWS) {
    it(`keeps the website publish confirmation on screen with many knowledge bases in a ${width}×${height} window (issue #288)`, () => {
      const names = Array.from({ length: 16 }, (_, index) => `矮視窗知識庫 ${String(index + 1).padStart(2, '0')}`);
      loginAs('SMB 管理者');
      // 在這台瀏覽器建立的知識庫（mock 的 `sme-demo:created-knowledge-bases`），再從資料來源頁逐一連接到助理。
      cy.window().then((win) =>
        win.localStorage.setItem(
          'sme-demo:created-knowledge-bases',
          JSON.stringify(
            names.map((name, index) => ({
              id: `knowledge-short-window-${index + 1}`,
              ownerAccountId: 'account-smb-admin',
              name,
              purpose: '矮視窗測試用的知識庫',
              lastSyncedAt: '2026-10-07T00:00:00.000Z',
            })),
          ),
        ),
      );
      cy.visit(`/app/assistants/${ASSISTANT}/data-sources`);
      for (const name of names) {
        cy.contains('.source-row', name).find('button.source-toggle').click().should('have.attr', 'aria-pressed', 'true');
      }

      cy.viewport(width, height);
      cy.visit(`/app/assistants/${ASSISTANT}/publishing?channel=website`);
      cy.get('app-website-embed').within(() => {
        cy.contains('button', '取消發布').click();
        cy.get('.action-status').should('contain', '已取消發布');
        cy.contains('button', '發布官網嵌入').click();
      });

      const confirm = '.publish-panel .dialog-actions button.ui-button:not(.secondary)';
      cy.get('.publish-panel ul[aria-label="訪客可以檢索的知識庫"] li').should('have.length.at.least', names.length);
      expectFullyOnScreen('#website-publish-title');
      expectFullyOnScreen('.publish-panel .dialog-actions button.secondary');
      expectFullyOnScreen(confirm);
      expectScrollingBody('.publish-panel .publish-detail');
      // 捲動區裡沒有可聚焦的元素，它自己要能聚焦，鍵盤使用者才捲得動（axe 的 scrollable-region-focusable）。
      cy.get('.publish-panel .publish-detail').should('have.attr', 'tabindex', '0');
      // 完整的 axe 檢查（issue #294），淺色與深色主題都跑。先前只跑 scrollable-region-focusable，因為 axe 在
      // 對話框 150ms 的 opacity 轉場中取色，報出 1.3–2.7:1 的假違規；auditA11y 現在先讓動畫結束再掃描。
      forEachColorTheme(() => auditA11y('.mat-mdc-dialog-container'));

      cy.get(confirm).should('contain', '確認發布').click({ scrollBehavior: false });
      cy.get('.publish-panel').should('not.exist');
      cy.get('app-website-embed .action-status').should('contain', '已發布官網嵌入');
    });
  }

  it('refuses to publish a draft whose acceptance has not passed and links to the assistant’s test-set tab', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/assistants/assistant-internal-onboarding/publishing?channel=website');
    cy.get('app-website-embed').within(() => {
      cy.contains('button', '發布官網嵌入').click();
    });
    cy.get('.publish-panel').contains('button', '確認發布').click();
    cy.get('app-website-embed .error-summary[role="alert"]')
      .should('contain', '驗收狀態必須是「通過」')
      .within(() => {
        cy.contains('a', '前往驗收題組頁').click();
      });
    cy.location('pathname').should('eq', '/app/assistants/assistant-internal-onboarding/acceptance');
  });

  it('shows credentials as 已設定・末四碼 only, replaces the token, tests the connection and enables the channel', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/assistants/assistant-customer-service/publishing?channel=line');
    // 每一步都重新查詢 app-line-setup：儲存／測試／啟用都會讓面板重新渲染，
    // 長 within() 會讓後續指令跑在舊的（已從 DOM 卸下的）節點上，偶發失敗。
    inLineSetup(() => {
      // 憑證只寫不讀：只有「已設定・末四碼」與「更換」，沒有輸入框，也沒有「顯示」切換。
      cy.get('#line-channelSecret-status').should('contain', '已設定・末四碼');
      cy.get('#line-accessToken-status').should('contain', '已設定・末四碼 0000');
      cy.get('#line-channelSecret').should('not.exist');
      cy.get('#line-accessToken').should('not.exist');
      cy.contains('button', '顯示').should('not.exist');
      // within() 的範圍就是 app-line-setup 本身，所以用 cy.root()，不能再 cy.get('app-line-setup')。
      cy.root().should('not.contain.text', VALID_TOKEN);

      // 示範資料：Token 已失效，最近一次測試連線第一項未通過、後兩項略過；頻道還在啟用狀態但不會服務。
      cy.get('.checklist li').should('have.length', 3);
      cy.contains('.checklist li', 'Channel access token').should('have.attr', 'data-state', 'failed');
      cy.contains('.checklist li', '設定 Webhook 網址').should('have.attr', 'data-state', 'skipped');
      // 狀態列在面板下方，可能被捲出捲動容器的可視範圍：先捲過去再檢查可見。
      cy.contains('.serving__label', '需要處理').scrollIntoView().should('be.visible');
      cy.get('#line-webhook-url').should('contain', '/line/assistant-customer-service');
      cy.contains('本月補送次數').should('contain', '3');

      cy.get('#line-accessToken-replace').click();
    });
    inLineSetup(() => {
      // 打完字先確認欄位真的收到完整內容，再送出；否則掉字會讓下一個斷言誤報。
      cy.get('#line-accessToken').should('have.attr', 'type', 'password').and('have.value', '');
      cy.get('#line-accessToken').type(VALID_TOKEN).should('have.value', VALID_TOKEN);
      cy.contains('button', '儲存').click();
    });
    inLineSetup(() => {
      cy.get('.feedback').should('contain', '已儲存');
      // 更換憑證後測試結果清掉、已啟用的頻道退回草稿，要重新測試。
      cy.get('.checklist li[data-state="pending"]').should('have.length', 3);
      cy.get('#line-accessToken-status').should('contain', `末四碼 ${VALID_TOKEN.slice(-4)}`);
      // within() 的範圍就是 app-line-setup 本身，所以用 cy.root()，不能再 cy.get('app-line-setup')。
      cy.root().should('not.contain.text', VALID_TOKEN);
      cy.contains('button', '測試連線').click();
    });
    inLineSetup(() => {
      cy.get('.checklist li[data-state="passed"]').should('have.length', 3);
      cy.get('[role="status"]').should('contain', '三項檢查都通過');
      cy.contains('.block--status button', '啟用').click();
    });
    inLineSetup(() => {
      cy.contains('已啟用 LINE 頻道').scrollIntoView().should('be.visible');
      cy.contains('.serving__reason', '服務中').scrollIntoView().should('be.visible');
    });
    cy.contains('app-channel-card', 'LINE').should('contain', '已發布');
    cy.contains('app-channel-card', '官網嵌入').should('contain', '已發布');

    // #291：只改「收到非文字訊息時的回覆」不影響連線測試與啟用狀態；空白會被擋下。
    inLineSetup(() => {
      cy.get('#line-nonTextReply').should('have.value', '目前只能回答文字問題。').clear();
      cy.contains('button', '儲存').click();
    });
    inLineSetup(() => {
      cy.get('#line-nonTextReply-error').should('contain', '請填寫收到非文字訊息時的回覆');
      cy.get('.error-summary a[href="#line-nonTextReply"]').should('exist');
      // 用 invoke('val') 一次放入整段（換行與 emoji），再觸發 input 讓元件收到。
      cy.get('#line-nonTextReply').invoke('val', OWN_NON_TEXT_REPLY).trigger('input');
      cy.get('#line-nonTextReply').should('have.value', OWN_NON_TEXT_REPLY);
      cy.get('#line-nonTextReply-hint').should('contain', `目前 ${OWN_NON_TEXT_REPLY.length} 字`);
      cy.contains('button', '儲存').click();
    });
    inLineSetup(() => {
      cy.get('.feedback').should('contain', '已儲存');
      cy.get('.error-summary').should('not.exist');
      cy.get('.checklist li[data-state="passed"]').should('have.length', 3);
      cy.contains('.serving__reason', '服務中').scrollIntoView().should('be.visible');
    });
    cy.reload();
    inLineSetup(() => {
      cy.get('#line-nonTextReply').should('have.value', OWN_NON_TEXT_REPLY);
      cy.contains('.serving__reason', '服務中').scrollIntoView().should('be.visible');
    });

    // 暫停、恢復與取消啟用都是 LINE 自己的操作，不影響其他管道。
    inLineSetup(() => {
      cy.contains('button', '暫停服務').click();
    });
    inLineSetup(() => {
      cy.contains('已暫停 LINE').scrollIntoView().should('be.visible');
      cy.contains('.serving__reason', '擁有者暫停中').scrollIntoView().should('be.visible');
      cy.contains('button', '恢復服務').click();
    });
    inLineSetup(() => {
      cy.contains('.serving__reason', '服務中').scrollIntoView().should('be.visible');
      cy.contains('button', '取消啟用').click();
    });
    inLineSetup(() => {
      cy.contains('已取消啟用').scrollIntoView().should('be.visible');
      cy.contains('.serving__reason', '尚未啟用').scrollIntoView().should('be.visible');
    });
    cy.contains('app-channel-card', '官網嵌入').should('contain', '已發布');
  });

  it('refuses enabling an untested, unaccepted LINE channel and lists both reasons with a way to fix each', () => {
    loginAs('SMB 管理者');
    cy.visit('/app/assistants/assistant-internal-onboarding/publishing?channel=line');
    inLineSetup(() => {
      cy.contains('button', '測試連線').should('be.disabled');
      cy.get('#line-officialAccountId').type('@anxin-demo').should('have.value', '@anxin-demo');
      cy.get('#line-channelId').type('1650000000').should('have.value', '1650000000');
      cy.get('#line-channelSecret').type('0123456789abcdef'.repeat(2));
      cy.get('#line-accessToken').type(VALID_TOKEN).should('have.value', VALID_TOKEN);
      cy.contains('button', '儲存').click();
    });
    inLineSetup(() => {
      cy.get('.feedback').should('contain', '已儲存');
      cy.contains('button', '測試連線').should('be.enabled');
      cy.contains('.block--status button', '啟用').click();
    });
    inLineSetup(() => {
      cy.get('.error-summary[role="alert"]')
        .should('contain', '請先測試連線')
        .and('contain', '驗收狀態必須是「通過」')
        .within(() => {
          cy.contains('a', '前往驗收題組頁').click();
        });
    });
    cy.location('pathname').should('eq', '/app/assistants/assistant-internal-onboarding/acceptance');
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
    cy.visit(`/chat/${ASSISTANT}`);
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
