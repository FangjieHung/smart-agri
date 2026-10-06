import { FakeServer, HOST, answer, companyReply, mountReady, session, QUESTION, required } from './widget.testing';

describe('closing the window', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => document.body.replaceChildren());

  function setup(hostOrigin: string | null = HOST) {
    const fake = new FakeServer();
    fake.sessions.push(session());
    fake.runs.push(answer(companyReply));
    const postMessage = vi.fn();
    return { fake, postMessage, mount: () => mountReady(fake, { parent: { postMessage }, context: { hostOrigin } }) };
  }

  it('the close button posts smartagri:close to the parent with the host origin as targetOrigin', async () => {
    const { postMessage, mount } = setup();
    const view = await mount();

    view.host.querySelector<HTMLButtonElement>('button.close')?.click();

    expect(postMessage).toHaveBeenCalledTimes(1);
    expect(postMessage).toHaveBeenCalledWith({ type: 'smartagri:close' }, 'https://shop.example.com');
  });

  it('Esc does the same (keyboard events do not reach the loader)', async () => {
    const { postMessage, mount } = setup();
    await mount();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(postMessage).toHaveBeenCalledWith({ type: 'smartagri:close' }, 'https://shop.example.com');
  });

  it('Esc from the composer also closes', async () => {
    const { postMessage, mount } = setup();
    const view = await mount();

    view.host.querySelector('textarea')?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(postMessage).toHaveBeenCalledTimes(1);
  });

  it('ignores Esc while an input method is composing', async () => {
    const { postMessage, mount } = setup();
    await mount();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', isComposing: true, bubbles: true }));

    expect(postMessage).not.toHaveBeenCalled();
  });

  it('sends nothing when the host parameter is missing or invalid', async () => {
    const { postMessage, mount } = setup(null);
    const view = await mount();

    view.host.querySelector<HTMLButtonElement>('button.close')?.click();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(postMessage).not.toHaveBeenCalled();
  });

  it('never uses * as the target origin', async () => {
    const { postMessage, mount } = setup();
    const view = await mount();
    view.host.querySelector<HTMLButtonElement>('button.close')?.click();
    expect(postMessage.mock.calls.every(([, target]) => target !== '*')).toBe(true);
  });

  it('Esc in the citation drawer closes only the drawer and returns focus to the sources button', async () => {
    const { postMessage, mount } = setup();
    const view = await mount();
    view.type(QUESTION);
    view.send();
    await view.until(() => view.host.querySelector('.citation-toggle') !== null);
    const toggle = required<HTMLButtonElement>(view.host, '.citation-toggle');
    toggle.focus();
    toggle.click();
    await view.until(() => view.host.querySelector('app-citation-drawer') !== null);
    const closeButton = view.host.querySelector<HTMLButtonElement>('.drawer-close');
    expect(document.activeElement).toBe(closeButton);

    closeButton?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await view.until(() => view.host.querySelector('app-citation-drawer') === null);

    expect(postMessage).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(toggle);
  });

  it('does nothing outside an iframe (no parent window)', async () => {
    const fake = new FakeServer();
    fake.sessions.push(session());
    const view = await mountReady(fake);
    expect(() => view.host.querySelector<HTMLButtonElement>('button.close')?.click()).not.toThrow();
  });
});
