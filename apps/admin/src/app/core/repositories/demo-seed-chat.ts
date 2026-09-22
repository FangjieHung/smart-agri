import type { AssistantId } from '../domain/assistant.model';
import type { ChatResponseId } from '../domain/conversation.model';
import type { DatabaseId } from '../domain/database.model';
import type { KnowledgeBaseId } from '../domain/knowledge-base.model';

/*
 * 終端對話的固定回覆。新問題以關鍵字對應到其中一則，對應不到就回覆查無資料；
 * 不連接真實 AI，也不會自行產生回答。
 */

export interface ChatCitationFixture {
  readonly knowledgeBaseId: KnowledgeBaseId;
  readonly documentName: string;
  readonly excerpt: string;
  readonly updatedLabel: string;
}

export type ChatAnswerFixture =
  | {
      readonly kind: 'company-data';
      readonly text: string;
      readonly citations: readonly ChatCitationFixture[];
    }
  | { readonly kind: 'general-knowledge'; readonly text: string }
  | { readonly kind: 'form-request'; readonly text: string; readonly databaseId: DatabaseId };

export interface ChatResponseFixture {
  readonly id: ChatResponseId;
  /** 建議問題的顯示文字。 */
  readonly prompt: string;
  /** 任一組關鍵字全部出現在問題中即視為對應。 */
  readonly matchers: readonly (readonly string[])[];
  readonly answer: ChatAnswerFixture;
}

export interface ChatProfileFixture {
  readonly welcome: string;
  /** 是否允許以獨立區塊補充一般知識。 */
  readonly allowGeneralKnowledge: boolean;
}

export const DEFAULT_CHAT_PROFILE: ChatProfileFixture = {
  welcome: '你好，我會依據已連接的資料回答；找不到資料時會直接告訴你。',
  allowGeneralKnowledge: false,
};

export const CHAT_PROFILES: Readonly<Partial<Record<AssistantId, ChatProfileFixture>>> = {
  'assistant-customer-service': {
    welcome: '你好，我是安心商行的客服助理。可以問我商品、退換貨與配送問題，也可以在這裡回報訂單問題。',
    allowGeneralKnowledge: true,
  },
  'assistant-internal-onboarding': {
    welcome: '歡迎加入！可以問我商品規格與保養方式。',
    allowGeneralKnowledge: false,
  },
};

export const CHAT_RESPONSES: readonly ChatResponseFixture[] = [
  {
    id: 'chat-refund-window',
    prompt: '收到商品後幾天內可以退貨？',
    matchers: [['退貨'], ['退換貨'], ['退款']],
    answer: {
      kind: 'company-data',
      text: '收到商品後 7 天內可以申請退貨，商品需保持完整包裝；退款會在收到退貨後 5 個工作天內完成。',
      citations: [
        {
          knowledgeBaseId: 'knowledge-refund-policy',
          documentName: '退換貨辦法 2026 版.pdf',
          excerpt: '消費者於收受商品後七日內，得申請退貨，商品應保持原包裝完整。',
          updatedLabel: '2026-09-18',
        },
        {
          knowledgeBaseId: 'knowledge-refund-policy',
          documentName: '退款作業流程.docx',
          excerpt: '倉儲確認退貨商品後，於五個工作天內完成退款作業。',
          updatedLabel: '2026-09-18',
        },
      ],
    },
  },
  {
    id: 'chat-leather-wash',
    prompt: '皮革商品可以用水清洗嗎？',
    matchers: [['皮革', '清洗'], ['皮革', '水洗'], ['皮革', '用水']],
    answer: {
      kind: 'company-data',
      text: '不建議用水清洗皮革商品。請用微濕的軟布擦拭表面，再放在通風處自然陰乾。',
      citations: [
        {
          knowledgeBaseId: 'knowledge-product-guide',
          documentName: 'FAQ：皮革商品可以用水清洗嗎？',
          excerpt: '皮革商品請勿直接水洗，可使用微濕軟布擦拭後置於通風處陰乾。',
          updatedLabel: '2026-09-18',
        },
      ],
    },
  },
  {
    id: 'chat-leather-care',
    prompt: '皮革商品平常要怎麼保養？',
    matchers: [['保養']],
    answer: {
      kind: 'general-knowledge',
      text: '一般建議避免長時間日曬與潮濕，並每 1 到 3 個月使用皮革保養油。',
    },
  },
  {
    id: 'chat-order-issue',
    prompt: '我要回報訂單問題',
    matchers: [['訂單'], ['出貨'], ['配送延遲']],
    answer: {
      kind: 'form-request',
      text: '可以的，請在下方表單填寫訂單資料。送出前會先讓你確認資料會交給誰、做什麼用途。',
      databaseId: 'database-orders',
    },
  },
];

export const CHAT_PRIVACY_NOTICE =
  '這段對話只屬於你的帳號：其他使用者與助理建立者都看不到內容，建立者只會看到匿名的使用次數。';

export const CHAT_GENERAL_KNOWLEDGE_NOTICE = '這不是公司資料，是一般知識補充，僅供參考。';

export const CHAT_NO_RESULT_TEXT =
  '查無資料：目前連接的資料中找不到這個問題的答案。這是 Demo，不會自行產生回答。';

export const CHAT_SENSITIVE_NOTICE =
  '請勿填寫身分證字號、病歷、信用卡號或密碼等敏感資料；只填寫處理這次問題需要的內容。';

export const CHAT_WITHDRAWAL_NOTICE =
  '送出後可以隨時聯絡接收單位撤回同意或申請刪除；撤回後這筆資料不會再出現在收集紀錄中。';
