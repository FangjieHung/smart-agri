/// <reference types="cypress" />

// ***********************************************
// This example commands.ts shows you how to
// create various custom commands and overwrite
// existing commands.
//
// For more comprehensive examples of custom
// commands please read more here:
// https://on.cypress.io/custom-commands
// ***********************************************

// 這個檔案本來就沒有 import/export，是「腳本」而非「模組」；`declare global` 只能出現在模組裡，
// 所以先用一個空 `export {}` 讓 TS 把它當模組看待。
export {};

// 擴充全域 Cypress 命名空間才是官方推薦的自訂指令型別寫法（見 https://on.cypress.io/typescript ）。
// @typescript-eslint/no-namespace 預設只放行「自己標了 declare」的 namespace；
// 巢狀在 `declare global {}` 底下的 namespace 語法上不會重複 declare，規則要往上一路找到
// 外層 `declare global` 才能判定它是 ambient 宣告——這正是規則內建 `allowDeclarations` 選項
// 存在的目的，見 apps/admin-e2e/eslint.config.mjs 對這個檔案的 override，範圍只鎖這一檔，
// 不是關掉規則。
declare global {
  namespace Cypress {
    interface Chainable {
      login(email: string, password: string): void;
    }
  }
}

// -- This is a parent command --
Cypress.Commands.add('login', (email, password) => {
  console.log('Custom command example: Login', email, password);
});
//
// -- This is a child command --
// Cypress.Commands.add("drag", { prevSubject: 'element'}, (subject, options) => { ... })
//
//
// -- This is a dual command --
// Cypress.Commands.add("dismiss", { prevSubject: 'optional'}, (subject, options) => { ... })
//
//
// -- This will overwrite an existing command --
// Cypress.Commands.overwrite("visit", (originalFn, url, options) => { ... })
