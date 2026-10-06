import { readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

const FILE = join(__dirname, 'embed.js');
const SOURCE = readFileSync(FILE, 'utf8');
const BASE = 'https://api.example.com';

function makeScript(attrs: Record<string, string>, src = `${BASE}/embed.js`): HTMLScriptElement {
  const el = document.createElement('script');
  el.src = src;
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  return el;
}

/** Runs embed.js as a browser would: `currentScript` is the pasted tag (or null for the fallback path). */
function run(current: HTMLScriptElement | null): void {
  Object.defineProperty(document, 'currentScript', { value: current, configurable: true });
  new Function(SOURCE)();
}

const launcher = () => document.getElementById('smartagri-launcher') as HTMLButtonElement;
const frame = () => document.getElementById('smartagri-frame') as HTMLIFrameElement | null;
const frameWindow = () => frame()?.contentWindow ?? null;
const root = () => document.getElementById('smartagri-embed') as HTMLElement;

function post(origin: string, data: unknown, source: MessageEventSource | null): void {
  window.dispatchEvent(new MessageEvent('message', { origin, data, source }));
}

beforeEach(() => {
  document.head.innerHTML = '';
  document.body.innerHTML = '';
  delete (window as unknown as Record<string, unknown>)['__smartagriEmbed'];
});

afterEach(() => {
  Object.defineProperty(document, 'currentScript', { value: null, configurable: true });
});

describe('embed.js', () => {
  it('is at most 5 kB uncompressed', () => {
    expect(statSync(FILE).size).toBeLessThanOrEqual(5120);
  });

  it('does nothing without data-assistant', () => {
    run(makeScript({}));
    expect(launcher()).toBeNull();
  });

  it('inserts a launcher with accessible name and aria state, and no iframe yet', () => {
    run(makeScript({ 'data-assistant': 'a1' }));
    expect(launcher().getAttribute('aria-label')).toBe('開啟客服對話');
    expect(launcher().getAttribute('aria-expanded')).toBe('false');
    expect(launcher().getAttribute('aria-controls')).toBe('smartagri-frame');
    expect(frame()).toBeNull();
  });

  it('creates the iframe lazily on first open with encoded id and host', () => {
    run(makeScript({ 'data-assistant': 'a b/1' }));
    launcher().click();
    expect(frame()?.getAttribute('src')).toBe(
      `${BASE}/use/a%20b%2F1?host=${encodeURIComponent(location.origin)}`,
    );
    launcher().click();
    launcher().click();
    expect(document.querySelectorAll('iframe')).toHaveLength(1);
  });

  it('derives the API base from currentScript.src, ignoring query and keeping a path prefix', () => {
    run(makeScript({ 'data-assistant': 'a1' }, 'https://api.example.com:8443/sa/embed.js?v=3'));
    launcher().click();
    expect(frame()?.getAttribute('src')).toMatch(/^https:\/\/api\.example\.com:8443\/sa\/use\/a1\?host=/);
  });

  it('falls back to the script tag with data-assistant whose src ends in /embed.js', () => {
    document.head.appendChild(makeScript({ 'data-assistant': 'other' }, 'https://cdn.example.com/other.js'));
    document.head.appendChild(makeScript({ 'data-assistant': 'a2' }, 'https://fallback.example.com/embed.js'));
    run(null);
    launcher().click();
    expect(frame()?.getAttribute('src')).toMatch(/^https:\/\/fallback\.example\.com\/use\/a2\?host=/);
  });

  it('toggles open state, aria-expanded and focus', () => {
    run(makeScript({ 'data-assistant': 'a1' }));
    launcher().click();
    expect(launcher().getAttribute('aria-expanded')).toBe('true');
    expect(root().classList.contains('smartagri-open')).toBe(true);
    launcher().click();
    expect(launcher().getAttribute('aria-expanded')).toBe('false');
    expect(root().classList.contains('smartagri-open')).toBe(false);
    expect(document.activeElement).toBe(launcher());
  });

  it('Esc closes and returns focus to the launcher; Esc while closed does nothing', () => {
    run(makeScript({ 'data-assistant': 'a1' }));
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(frame()).toBeNull();
    launcher().click();
    frame()?.focus();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(launcher().getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(launcher());
  });

  it('closes on smartagri:close from the base origin and iframe window', () => {
    run(makeScript({ 'data-assistant': 'a1' }));
    launcher().click();
    post(BASE, { type: 'smartagri:close' }, frameWindow());
    expect(launcher().getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(launcher());
  });

  it('ignores messages from other origins, other windows, or other types', () => {
    run(makeScript({ 'data-assistant': 'a1' }));
    launcher().click();
    post('https://evil.example.com', { type: 'smartagri:close' }, frameWindow());
    post(BASE, { type: 'smartagri:close' }, window);
    post(BASE, { type: 'smartagri:other' }, frameWindow());
    post(BASE, 'smartagri:close', frameWindow());
    post(BASE, null, frameWindow());
    expect(launcher().getAttribute('aria-expanded')).toBe('true');
  });

  it('initialises once when the script is pasted twice', () => {
    run(makeScript({ 'data-assistant': 'a1' }));
    run(makeScript({ 'data-assistant': 'a1' }));
    expect(document.querySelectorAll('#smartagri-launcher')).toHaveLength(1);
    expect(document.querySelectorAll('style#smartagri-style')).toHaveLength(1);
    launcher().click();
    expect(document.querySelectorAll('iframe')).toHaveLength(1);
  });

  it('injects a single prefixed <style> with isolation, a 480px full-screen rule and position', () => {
    run(makeScript({ 'data-assistant': 'a1' }));
    const css = (document.getElementById('smartagri-style')?.textContent ?? '');
    expect(document.querySelectorAll('style')).toHaveLength(1);
    expect(css).toContain('all:initial');
    expect(css).toMatch(/@media \(max-width:480px\)\{[^]*\.smartagri-open \.smartagri-frame\{position:fixed;inset:0;width:100%;height:100%/);
    expect(css).toContain('.smartagri-root{position:fixed;bottom:20px;right:20px');
  });

  it('data-position="left" anchors to the left', () => {
    run(makeScript({ 'data-assistant': 'a1', 'data-position': 'left' }));
    const css = (document.getElementById('smartagri-style')?.textContent ?? '');
    expect(css).toContain('.smartagri-root{position:fixed;bottom:20px;left:20px');
    expect(css).toContain('.smartagri-frame{display:none;position:absolute;bottom:68px;left:0');
  });

  it('waits for DOMContentLoaded when body does not exist yet', () => {
    const body = document.body;
    body.remove();
    run(makeScript({ 'data-assistant': 'a1' }));
    expect(launcher()).toBeNull();
    document.documentElement.appendChild(body);
    document.dispatchEvent(new Event('DOMContentLoaded'));
    expect(launcher()).not.toBeNull();
  });

  it('uses no cookies or web storage', () => {
    expect(SOURCE).not.toMatch(/cookie|localStorage|sessionStorage|indexedDB/);
  });
});
