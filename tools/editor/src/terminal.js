// Terminal page of VIGO (xterm.js in WebView2, ADR-071). The app owns the shell (ConPTY): this page only draws the
// VT stream it receives and sends back keystrokes, the size and the few messages below. Messages go both ways as JSON.
//
// app -> page  data {text} · theme {colors, fontSize} · appKeys {keys} · focus · paste {text}
// page -> app  ready {cols, rows} · input {data, binary} · resize {cols, rows} · ack {length} · key {vk, ctrl, shift, alt}
//              copy {text} · pasteRequest · link {url} · error {message}
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { WebglAddon } from '@xterm/addon-webgl';
import { Unicode11Addon } from '@xterm/addon-unicode11';
import { WebLinksAddon } from '@xterm/addon-web-links';
import '@xterm/xterm/css/xterm.css';
import './terminal.css';

const host = window.chrome.webview;
const post = (message) => host.postMessage(message);
const root = document.getElementById('root');
const params = new URLSearchParams(location.search);

const VK_C = 67;
const VK_V = 86;
const VK_INSERT = 45;

window.addEventListener('error', (e) => post({ type: 'error', message: String(e.message) }));

// Nerd Fonts after Cascadia: prompts (oh-my-posh, starship) draw their icons with them, per glyph, when installed.
const DEFAULT_FONT = '"Cascadia Mono", "Cascadia Code", "CaskaydiaCove NFM", "CaskaydiaCove NF", "MesloLGM Nerd Font Mono", "MesloLGS Nerd Font Mono", ' +
  '"JetBrainsMono Nerd Font Mono", "FiraCode Nerd Font Mono", "Hack Nerd Font Mono", Consolas, "Courier New", monospace';

const term = new Terminal({
  allowProposedApi: true,
  cursorBlink: true,
  cursorStyle: 'bar',
  scrollback: 10000,
  fontFamily: DEFAULT_FONT,
  fontSize: 12,
  lineHeight: 1.15,
  // ConPTY re-wraps lines itself: xterm.js must not reflow what it already drew (needs the Windows build number).
  windowsPty: { backend: 'conpty', buildNumber: Number(params.get('build')) || 0 },
  theme: { background: '#1c1c1c', foreground: '#cccccc' },
});

// Debug builds of the app (?debug=1): the terminal is reachable from DevTools to check what was drawn.
if (params.has("debug")) window.vigoTerminal = term;

const fit = new FitAddon();
term.loadAddon(fit);
term.loadAddon(new Unicode11Addon());
term.unicode.activeVersion = '11';
// Opening a link needs Ctrl: a plain click selects text.
term.loadAddon(new WebLinksAddon((event, url) => {
  if (event.ctrlKey || event.metaKey) post({ type: 'link', url });
}));
term.open(root);
try {
  const webgl = new WebglAddon();
  // Lost GPU context: back to the DOM renderer.
  webgl.onContextLoss(() => webgl.dispose());
  term.loadAddon(webgl);
} catch {
  // No WebGL: the DOM renderer stays.
}

let started = false;
const refit = () => {
  // A hidden panel has no size: wait until it is shown.
  if (root.clientWidth === 0 || root.clientHeight === 0) return;
  fit.fit();
  if (!started) {
    started = true;
    post({ type: 'ready', cols: term.cols, rows: term.rows });
  }
};
new ResizeObserver(() => requestAnimationFrame(refit)).observe(root);

term.onResize(({ cols, rows }) => {
  if (started) post({ type: 'resize', cols, rows });
});
term.onData((data) => post({ type: 'input', data, binary: false }));
// Mouse reports of old encodings: bytes sent as a Latin-1 string.
term.onBinary((data) => post({ type: 'input', data, binary: true }));

// Shortcuts of the app that must work with the terminal focused (the toggle, Ctrl+Shift combinations): sent by the app.
let appKeys = [];

const copySelection = () => {
  const text = term.getSelection();
  if (text) post({ type: 'copy', text });
  return text.length > 0;
};

term.attachCustomKeyEventHandler((e) => {
  if (e.type !== 'keydown') return true;
  // AltGr (AZERTY, QWERTZ) is reported as Ctrl+Alt: it types a character, it is never a shortcut.
  if (e.getModifierState('AltGraph')) return true;

  const app = appKeys.find((k) => k.vk === e.keyCode && k.ctrl === e.ctrlKey && k.shift === e.shiftKey && k.alt === e.altKey);
  if (app) {
    e.preventDefault();
    post({ type: 'key', vk: e.keyCode, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey });
    return false;
  }
  // Copy: Ctrl+Shift+C, or Ctrl+C while text is selected (otherwise Ctrl+C is the interrupt of the shell).
  if (e.ctrlKey && !e.altKey && e.keyCode === VK_C && (e.shiftKey || term.hasSelection())) {
    copySelection();
    term.clearSelection();
    return false;
  }
  // Paste: left to the browser, which raises the paste event xterm.js handles (bracketed paste included).
  if ((e.ctrlKey && !e.altKey && e.keyCode === VK_V) || (e.shiftKey && !e.ctrlKey && e.keyCode === VK_INSERT)) return false;
  return true;
});

// Right click, as in Windows Terminal: copies the selection, or pastes when there is none.
root.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  if (term.hasSelection()) {
    copySelection();
    term.clearSelection();
  } else {
    post({ type: 'pasteRequest' });
  }
});

// Dimmed while the page does not have the focus (the preference decides, see applyTheme).
const html = document.documentElement;
window.addEventListener('blur', () => html.classList.add('vg-blurred'));
window.addEventListener('focus', () => html.classList.remove('vg-blurred'));
if (!document.hasFocus()) html.classList.add('vg-blurred');

const applyTheme = (message) => {
  const colors = message.colors ?? {};
  term.options.theme = colors;
  if (message.fontSize) term.options.fontSize = message.fontSize;
  // The chosen family first; the default list stays behind it (Nerd Fonts of the prompts).
  term.options.fontFamily = message.fontFamily ? `"${message.fontFamily.replace(/["\\]/g, '')}", ${DEFAULT_FONT}` : DEFAULT_FONT;
  if (message.lineHeight) term.options.lineHeight = message.lineHeight;
  if (message.cursorStyle) term.options.cursorStyle = message.cursorStyle;
  html.classList.toggle('vg-dim', message.dimWhenUnfocused !== false);
  document.documentElement.style.setProperty('--vg-background', colors.background ?? '#1c1c1c');
  if (message.scrollbar) document.documentElement.style.setProperty('--vg-scrollbar', message.scrollbar);
  if (message.scrollbarHover) document.documentElement.style.setProperty('--vg-scrollbar-hover', message.scrollbarHover);
  requestAnimationFrame(refit);
};

host.addEventListener('message', (e) => {
  const message = e.data;
  switch (message.type) {
    case 'data':
      // The acknowledgement tells the app how much was drawn: it stops sending while the page is behind (cat of a huge file).
      term.write(message.text, () => post({ type: 'ack', length: message.text.length }));
      break;
    case 'theme':
      applyTheme(message);
      break;
    case 'appKeys':
      appKeys = message.keys ?? [];
      break;
    case 'focus':
      term.focus();
      break;
    case 'paste':
      if (message.text) term.paste(message.text);
      break;
  }
});

// Starts from the first non-empty layout.
requestAnimationFrame(refit);
