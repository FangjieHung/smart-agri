/**
 * 矮視窗裡的對話框（issues #284、#288）：對話框限制在視窗高度內，只有中間那段捲動，
 * 標題、錯誤訊息與按鈕列一直在畫面上。測試全程不用 scrollIntoView，按鈕用 `scrollBehavior: false` 直接點。
 */
export const SHORT_WINDOWS = [[1000, 660], [390, 700]] as const;

/** 元素整個落在視窗內，而且視窗在它中心點命中的就是它自己（沒有被裁掉或蓋住）。 */
export function expectFullyOnScreen(selector: string): void {
  cy.get(selector).then(($element) => {
    const element = $element[0];
    const rect = element.getBoundingClientRect();
    const win = element.ownerDocument.defaultView as Window;
    expect(rect.top, `${selector} top`).to.be.at.least(0);
    expect(rect.left, `${selector} left`).to.be.at.least(0);
    expect(rect.bottom, `${selector} bottom`).to.be.at.most(win.innerHeight);
    expect(rect.right, `${selector} right`).to.be.at.most(win.innerWidth);
    const hit = element.ownerDocument.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
    expect(hit === element || element.contains(hit), `${selector} is the element at its own center`).to.eq(true);
  });
}

/** 內容比可用的高度多，所以這一段真的在捲動，而不是整個對話框被撐高。 */
export function expectScrollingBody(selector: string): void {
  cy.get(selector).should(($body) => {
    expect($body[0].scrollHeight, `${selector} scrollHeight`).to.be.greaterThan($body[0].clientHeight);
  });
}
