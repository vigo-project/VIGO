// Code editor page of VIGO (ADR-038): Monaco hosted in a WebView2, driven by the app through web messages.
// The app sends JSON objects { type, ... }; the page answers with { type, ... } (see EditorMessages.cs).
// Every user-facing text comes from the app (localization); nothing here is shown as is.
import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/features/register.all.js';
import 'monaco-editor/languages/definitions/register.all.js';
import './syntax.js';
import { createReview } from './review.js';
import './editor.css';

self.MonacoEnvironment = {
  // Only the generic editor worker (diff computation, links…): no language service is bundled.
  getWorker: () => new Worker('editor.worker.js'),
};

const webview = window.chrome?.webview;
const post = message => webview?.postMessage(message);
const root = document.getElementById('root');

const HUNK_HEIGHT = 30;
const HINT_HEIGHT = 40;

/** Options shared by every editor of the page; font and line height follow the app density. */
const DEFAULT_FONT = "'Cascadia Mono', 'Cascadia Code', Consolas, monospace";
const shared = {
  fontFamily: DEFAULT_FONT,
  fontSize: 12,
  lineHeight: 20,
  fontLigatures: false,
};

/** Preferences of the app (theme message): tab size, line ending of the text read back, syntax colors, numbers, wrap. */
const settings = { tabSize: 4, eol: 'auto', syntax: true, lineNumbers: true, wordWrap: false };

let editor = null;       // single code editor (unified diff, text)
let diffEditor = null;   // side-by-side diff, created on first use
let conflictUi = null;   // conflict editor (panes ours / theirs, result), created on first use
const hosts = {};        // their container elements, by name
let mode = 'empty';      // 'empty' | 'diff' | 'sideBySide' | 'text' | 'conflict' | 'blame'
let doc = null;          // unified diff shown: { kinds, old, new, hunks, staging, selected }
let decorations = null;
let zoneIds = [];
let sticky = null;       // overlay widget showing the header of the hunk at the top
let stickyIndex = -1;    // its hunk; -1 when hidden
let contextKeys = null;
let modelCount = 0;

// Inline review of a pull request (J6.11): zones and boxes drawn by review.js on whichever editors show the diff.
const review = createReview({ monaco, post, surfaces: () => reviewSurfaces() });

// ---------------------------------------------------------------- theme

function applyTheme(t) {
  const c = t.colors;
  const fg = color => color.slice(1, 7); // token rules take RRGGBB
  monaco.editor.defineTheme('vigo', {
    base: t.dark ? 'vs-dark' : 'vs',
    inherit: true,
    // Syntax colors of the design system (tokens syn-*); the other tokens keep the base theme.
    rules: [
      { token: 'comment', foreground: fg(c.synComment) },
      { token: 'keyword', foreground: fg(c.synKeyword) },
      { token: 'string', foreground: fg(c.synString) },
      { token: 'number', foreground: fg(c.synNumber) },
      { token: 'type', foreground: fg(c.synType) },
      { token: 'type.identifier', foreground: fg(c.synType) },
      { token: 'function', foreground: fg(c.synFunction) },
      // Categories of the grammars of syntax.js (C#, TypeScript, JavaScript).
      { token: 'keyword.control', foreground: fg(c.synControl) },
      { token: 'variable', foreground: fg(c.synVariable) },
      { token: 'property', foreground: fg(c.synProperty) },
    ],
    colors: {
      'editor.background': c.background,
      'editor.foreground': c.text,
      'editorGutter.background': c.background,
      'editorLineNumber.foreground': c.text3,
      'editorLineNumber.activeForeground': c.text2,
      'editor.lineHighlightBackground': '#00000000',
      'editor.lineHighlightBorder': '#00000000',
      // The accent at a fixed opacity: the soft "selected" color vanishes over the green / red of added / removed lines.
      'editor.selectionBackground': `${c.accent.slice(0, 7)}${t.dark ? '88' : '66'}`,
      'editor.inactiveSelectionBackground': `${c.accent.slice(0, 7)}${t.dark ? '66' : '4D'}`,
      'editorWidget.background': c.layer,
      'editorWidget.border': c.stroke,
      'editorStickyScroll.background': c.header,
      'focusBorder': c.accent,
      'diffEditor.insertedLineBackground': c.add,
      'diffEditor.removedLineBackground': c.del,
      'diffEditor.insertedTextBackground': c.addStrong,
      'diffEditor.removedTextBackground': c.delStrong,
      'diffEditorGutter.insertedLineBackground': c.add,
      'diffEditorGutter.removedLineBackground': c.del,
      'scrollbarSlider.background': c.scrollbar,
      'scrollbarSlider.hoverBackground': c.scrollbarHover,
      'scrollbarSlider.activeBackground': c.scrollbarHover,
    },
  });
  monaco.editor.setTheme('vigo');

  const style = document.documentElement.style;
  for (const [name, value] of Object.entries(c)) style.setProperty(`--vg-${name}`, value);
  document.documentElement.dataset.theme = t.dark ? 'dark' : 'light';

  shared.fontSize = t.fontSize;
  shared.lineHeight = t.lineHeight;
  shared.fontLigatures = t.ligatures ?? false;
  // The chosen family first; the default monospace fonts stay behind it for the glyphs it lacks.
  shared.fontFamily = t.fontFamily ? `'${t.fontFamily.replace(/['"\\]/g, '')}', ${DEFAULT_FONT}` : DEFAULT_FONT;
  Object.assign(settings, {
    tabSize: t.tabSize ?? 4, eol: t.eol ?? 'auto', syntax: t.syntax ?? true, lineNumbers: t.lineNumbers ?? true, wordWrap: t.wordWrap ?? false,
  });
  style.setProperty('--vg-line-height', `${t.lineHeight}px`);
  style.setProperty('--vg-font-family', shared.fontFamily);
  const fonts = { fontSize: t.fontSize, lineHeight: t.lineHeight, fontFamily: shared.fontFamily, fontLigatures: shared.fontLigatures, tabSize: settings.tabSize };
  const numbers = settings.lineNumbers ? 'on' : 'off';
  const wrap = settings.wordWrap ? 'on' : 'off';
  editor?.updateOptions({ ...fonts, wordWrap: wrap, lineNumbers: doc ? (settings.lineNumbers ? numberLabel : 'off') : numbers });
  diffEditor?.updateOptions({ ...fonts, wordWrap: wrap, lineNumbers: numbers });
  for (const e of conflictEditors()) e.updateOptions({ ...fonts, wordWrap: wrap, lineNumbers: numbers });
  // The blame column lines up with the lines of the file: no wrapping there.
  blameUi?.editor.updateOptions({ ...fonts, lineNumbers: numbers });
  for (const model of monaco.editor.getModels()) {
    model.updateOptions({ tabSize: settings.tabSize });
    const language = languageFor(decodeURIComponent(model.uri.path));
    if (model.getLanguageId() !== language) monaco.editor.setModelLanguage(model, language);
  }
  if (mode === 'diff') renderZones();
  if (mode === 'conflict') renderConflict();
  if (mode === 'blame') renderBlameColumn();
}

// ---------------------------------------------------------------- editors

function baseOptions() {
  return {
    ...shared,
    tabSize: settings.tabSize,
    // The tab size is the user's choice, not the one guessed from the file.
    detectIndentation: false,
    wordWrap: settings.wordWrap ? 'on' : 'off',
    lineNumbers: settings.lineNumbers ? 'on' : 'off',
    automaticLayout: true,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    renderLineHighlight: 'none',
    mouseWheelZoom: false,
    contextmenu: true,
    fixedOverflowWidgets: true,
    smoothScrolling: false,
    stickyScroll: { enabled: false },
    unicodeHighlight: { ambiguousCharacters: false, invisibleCharacters: false },
    occurrencesHighlight: 'off',
    renderWhitespace: 'none',
    unusualLineTerminators: 'off',
    guides: { indentation: false },
    links: false,
    padding: { top: 0, bottom: 0 },
    scrollbar: { verticalScrollbarSize: 12, horizontalScrollbarSize: 12, useShadows: false },
  };
}

function ensureEditor() {
  if (editor) return editor;
  const host = hosts.editor = document.createElement('div');
  host.className = 'vg-host';
  root.appendChild(host);
  editor = monaco.editor.create(host, { ...baseOptions(), readOnly: true, domReadOnly: false, glyphMargin: true });
  decorations = editor.createDecorationsCollection();

  contextKeys = {
    diff: editor.createContextKey('vgDiff', false),
    staging: editor.createContextKey('vgStaging', false),
    discard: editor.createContextKey('vgDiscard', false),
  };
  const key = (name) => () => post({ type: 'key', key: name, vk: 0, ctrl: false, shift: false, alt: false });
  // Diff keys of the app (mockup): S stages, Delete discards, Alt+↑ / Alt+↓ move between hunks, Escape goes back.
  editor.addCommand(monaco.KeyCode.KeyS, key('S'), 'vgStaging && editorTextFocus');
  editor.addCommand(monaco.KeyCode.Delete, key('Delete'), 'vgDiscard && editorTextFocus');
  editor.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.UpArrow, key('AltUp'), 'vgDiff && editorTextFocus');
  editor.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.DownArrow, key('AltDown'), 'vgDiff && editorTextFocus');
  editor.addCommand(monaco.KeyCode.Escape, key('Escape'),
    'vgDiff && editorTextFocus && !findWidgetVisible && !suggestWidgetVisible');

  editor.onMouseDown(onMouseDown);
  editor.onDidScrollChange(() => {
    root.style.setProperty('--vg-scroll-left', `${editor.getScrollLeft()}px`);
    updateSticky();
  });
  editor.onDidLayoutChange(layout => {
    setContentWidth(layout);
    updateSticky(true);
  });
  editor.onDidChangeModelContent(() => {
    if (mode === 'text') post({ type: 'changed' });
  });
  return editor;
}

function ensureDiffEditor() {
  if (diffEditor) return diffEditor;
  const host = hosts.diff = document.createElement('div');
  host.className = 'vg-host';
  root.appendChild(host);
  diffEditor = monaco.editor.createDiffEditor(host, {
    ...baseOptions(),
    readOnly: true,
    originalEditable: false,
    renderSideBySide: true,
    useInlineViewWhenSpaceIsLimited: false,
    renderOverviewRuler: true,
    ignoreTrimWhitespace: false,
    renderIndicators: true,
    diffAlgorithm: 'advanced',
    hideUnchangedRegions: { enabled: false },
  });
  const key = (name) => () => post({ type: 'key', key: name, vk: 0, ctrl: false, shift: false, alt: false });
  for (const side of [diffEditor.getOriginalEditor(), diffEditor.getModifiedEditor()]) {
    side.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.UpArrow, () => diffEditor.goToDiff('previous'), 'editorTextFocus');
    side.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.DownArrow, () => diffEditor.goToDiff('next'), 'editorTextFocus');
    side.addCommand(monaco.KeyCode.Escape, key('Escape'), 'editorTextFocus && !findWidgetVisible');
  }
  return diffEditor;
}

/** Shows one of the two editors and hides the other (both stay alive: switching is instant). */
function show(which) {
  for (const [name, host] of Object.entries(hosts)) host.style.display = name === which ? '' : 'none';
}

function languageFor(path) {
  if (!settings.syntax) return 'plaintext';
  const name = (path ?? '').split('/').pop().toLowerCase();
  const dot = name.lastIndexOf('.');
  const ext = dot >= 0 ? name.slice(dot) : '';
  for (const lang of monaco.languages.getLanguages()) {
    if (lang.filenames?.some(f => f.toLowerCase() === name)) return lang.id;
    if (ext && lang.extensions?.some(e => e.toLowerCase() === ext)) return lang.id;
  }
  return 'plaintext';
}

function newModel(text, path) {
  const model = monaco.editor.createModel(text, languageFor(path), monaco.Uri.parse(`inmemory://vigo/${++modelCount}/${encodeURIComponent(path ?? 'file')}`));
  model.updateOptions({ tabSize: settings.tabSize });
  return model;
}

/** Text of an editor in the line ending chosen in the preferences (auto: the one of the model). */
function textOf(e) {
  if (!e) return '';
  const eol = { lf: monaco.editor.EndOfLinePreference.LF, crlf: monaco.editor.EndOfLinePreference.CRLF }[settings.eol];
  return eol === undefined ? e.getValue() : e.getModel()?.getValue(eol) ?? '';
}

function replaceModel(target, model) {
  const old = target.getModel();
  target.setModel(model);
  old?.dispose();
}

// ---------------------------------------------------------------- unified diff

/** Line number gutter: "old new", each right-aligned on the width of the largest number. */
function numberLabel(line) {
  if (!doc) return String(line);
  const i = line - 1;
  const o = doc.old[i] ? String(doc.old[i]) : '';
  const n = doc.new[i] ? String(doc.new[i]) : '';
  return `${o.padStart(doc.width)} ${n.padStart(doc.width)}`;
}

function showDiff(m) {
  const target = ensureEditor();
  const scroll = m.keepScroll && mode === 'diff' ? { top: target.getScrollTop(), left: target.getScrollLeft() } : null;
  mode = 'diff';
  show('editor');

  let maxNumber = 0;
  for (const v of m.old) if (v > maxNumber) maxNumber = v;
  for (const v of m.new) if (v > maxNumber) maxNumber = v;
  const width = Math.max(3, String(maxNumber).length);
  doc = { kinds: m.kinds, old: m.old, new: m.new, hunks: m.hunks, staging: m.staging ?? null, selected: new Set(m.selected ?? []), width };

  target.updateOptions({
    readOnly: true,
    lineNumbers: settings.lineNumbers ? numberLabel : 'off',
    lineNumbersMinChars: 2 * width + 2,
    glyphMargin: !!doc.staging,
    folding: false,
    lineDecorationsWidth: 16,
  });
  replaceModel(target, newModel(m.text, m.path));
  contextKeys.diff.set(true);
  contextKeys.staging.set(!!doc.staging);
  contextKeys.discard.set(!!doc.staging?.discard);

  renderDecorations();
  renderZones();
  if (scroll) {
    target.setScrollTop(scroll.top);
    target.setScrollLeft(scroll.left);
  } else {
    // A freshly opened diff starts on its first change, not on the leading context.
    const first = m.kinds.search(/[ad]/);
    target.setScrollTop(0);
    if (first > 0) target.revealLineNearTop(first + 1);
  }
  updateSticky(true);
  review.refresh();
}

function renderDecorations() {
  if (!doc) return;
  const items = [];
  const kinds = doc.kinds;
  for (let i = 0; i < kinds.length; i++) {
    const k = kinds[i];
    if (k === 'c') continue;
    const range = new monaco.Range(i + 1, 1, i + 1, 1);
    if (k === 'n') {
      items.push({ range: new monaco.Range(i + 1, 1, i + 1, Number.MAX_SAFE_INTEGER), options: { inlineClassName: 'vg-note' } });
      continue;
    }
    const added = k === 'a';
    const selected = doc.staging && doc.selected.has(i + 1);
    items.push({
      range,
      options: {
        isWholeLine: true,
        className: selected ? 'vg-line-selected' : added ? 'vg-line-add' : 'vg-line-del',
        marginClassName: selected ? 'vg-margin-selected' : added ? 'vg-margin-add' : 'vg-margin-del',
        linesDecorationsClassName: added ? 'vg-sign vg-sign-add' : 'vg-sign vg-sign-del',
        glyphMarginClassName: doc.staging ? (selected ? 'vg-check vg-check-on' : 'vg-check') : undefined,
        glyphMarginHoverMessage: doc.staging ? { value: doc.staging.toggleTip } : undefined,
      },
    });
  }
  decorations.set(items);
}

function hunkNode(index, forSticky) {
  const hunk = doc.hunks[index];
  const node = document.createElement('div');
  node.className = forSticky ? 'vg-hunk vg-hunk-sticky' : 'vg-hunk';
  const header = document.createElement('span');
  header.className = 'vg-hunk-text';
  header.textContent = hunk.header;
  node.appendChild(header);
  if (doc.staging) {
    const button = (label, type, danger) => {
      const b = document.createElement('button');
      b.className = danger ? 'vg-button vg-button-danger' : 'vg-button';
      b.textContent = label;
      b.addEventListener('mousedown', e => e.stopPropagation());
      b.addEventListener('click', e => {
        e.stopPropagation();
        post({ type, hunk: index });
      });
      return b;
    };
    node.appendChild(button(doc.staging.apply, 'applyHunk', false));
    if (doc.staging.discard) node.appendChild(button(doc.staging.discard, 'discardHunk', true));
  }
  return node;
}

/** Width of the hunk headers and hint: the visible text area (they follow the horizontal scroll, see CSS). */
function setContentWidth(layout) {
  root.style.setProperty('--vg-content-width', `${layout.contentWidth - layout.verticalScrollbarWidth}px`);
}

/** Node of a view zone: Monaco sets its display (block / none), so the content goes in a child. */
function zoneNode(content) {
  const zone = document.createElement('div');
  zone.className = 'vg-zone';
  zone.appendChild(content);
  return zone;
}

function renderZones() {
  if (!editor || !doc) return;
  editor.changeViewZones(accessor => {
    for (const id of zoneIds) accessor.removeZone(id);
    zoneIds = [];
    doc.hunks.forEach((hunk, index) => {
      const margin = document.createElement('div');
      margin.className = 'vg-hunk-margin';
      zoneIds.push(accessor.addZone({
        afterLineNumber: hunk.line - 1,
        heightInPx: HUNK_HEIGHT,
        domNode: zoneNode(hunkNode(index, false)),
        marginDomNode: margin,
        suppressMouseDown: true,
      }));
    });
    if (doc.staging?.hint && doc.hunks.length > 0) {
      const hint = document.createElement('div');
      hint.className = 'vg-hint';
      hint.textContent = doc.staging.hint;
      zoneIds.push(accessor.addZone({
        afterLineNumber: doc.kinds.length,
        heightInPx: HINT_HEIGHT,
        domNode: zoneNode(hint),
        suppressMouseDown: true,
      }));
    }
  });
  setContentWidth(editor.getLayoutInfo());
}

/** Index of the hunk a document line belongs to (hunks are sorted by first line). */
function hunkAt(line) {
  let lo = 0, hi = doc.hunks.length - 1, found = 0;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (doc.hunks[mid].line <= line) { found = mid; lo = mid + 1; } else hi = mid - 1;
  }
  return found;
}

/** The header of the hunk at the top stays visible once its own header is scrolled away (as before). */
function updateSticky(force) {
  let index = -1;
  if (mode === 'diff' && doc && doc.hunks.length > 0) {
    const first = editor.getVisibleRanges()[0]?.startLineNumber;
    if (first) {
      const hunk = hunkAt(first);
      // Shown once the hunk's own header has scrolled above the top.
      if (editor.getTopForLineNumber(doc.hunks[hunk].line) - HUNK_HEIGHT < editor.getScrollTop()) index = hunk;
    }
  }
  // Scrolling within the same hunk: nothing to rebuild.
  if (!force && index === stickyIndex) return;
  stickyIndex = index;
  if (sticky) {
    editor.removeOverlayWidget(sticky);
    sticky = null;
  }
  if (index < 0) return;
  const node = hunkNode(index, true);
  const layout = editor.getLayoutInfo();
  node.style.left = `${layout.contentLeft}px`;
  node.style.width = `${layout.contentWidth - layout.verticalScrollbarWidth}px`;
  sticky = { getId: () => 'vg.sticky', getDomNode: () => node, getPosition: () => null };
  editor.addOverlayWidget(sticky);
}

function onMouseDown(e) {
  if (mode !== 'diff' || !doc?.staging || !e.target.position) return;
  const type = e.target.type;
  if (type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && type !== monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS) return;
  const line = e.target.position.lineNumber;
  const kind = doc.kinds[line - 1];
  if (kind !== 'a' && kind !== 'd') return;
  post({ type: 'toggleLine', line, extend: e.event.shiftKey });
  // The keys of the selection (S, Delete) go through the editor: a click in the gutter does not focus it by itself.
  editor.focus();
}

function setSelection(m) {
  if (!doc) return;
  doc.selected = new Set(m.lines);
  renderDecorations();
}

function revealHunk(m) {
  if (mode !== 'diff' || !doc || m.hunk < 0 || m.hunk >= doc.hunks.length) return;
  const top = editor.getTopForLineNumber(doc.hunks[m.hunk].line) - HUNK_HEIGHT;
  editor.setScrollTop(Math.max(0, top));
}

// ---------------------------------------------------------------- side by side, text

function showSideBySide(m) {
  const target = ensureDiffEditor();
  mode = 'sideBySide';
  doc = null;
  show('diff');
  const old = target.getModel();
  target.setModel({ original: newModel(m.original, m.oldPath ?? m.path), modified: newModel(m.modified, m.path) });
  old?.original.dispose();
  old?.modified.dispose();
  // The diff is computed asynchronously: open the file on its first change once it is known.
  revealFirstChange?.dispose();
  revealFirstChange = target.onDidUpdateDiff(() => {
    revealFirstChange.dispose();
    revealFirstChange = null;
    const change = target.getLineChanges()?.[0];
    if (!change) return;
    const line = change.modifiedEndLineNumber === 0 ? change.modifiedStartLineNumber + 1 : change.modifiedStartLineNumber;
    target.getModifiedEditor().revealLineNearTop(Math.max(1, line));
  });
  review.refresh();
}

let revealFirstChange = null;

function showText(m) {
  const target = ensureEditor();
  mode = 'text';
  doc = null;
  show('editor');
  target.changeViewZones(accessor => {
    for (const id of zoneIds) accessor.removeZone(id);
  });
  zoneIds = [];
  decorations.clear();
  updateSticky(true);
  contextKeys.diff.set(false);
  contextKeys.staging.set(false);
  contextKeys.discard.set(false);
  target.updateOptions({ readOnly: m.readOnly, lineNumbers: settings.lineNumbers ? 'on' : 'off', lineNumbersMinChars: 4, glyphMargin: false, folding: true, lineDecorationsWidth: 10 });
  replaceModel(target, newModel(m.text, m.path));
  review.refresh();
}

// ---------------------------------------------------------------- conflict editor (J5.4)

// Mockup "Conflits": panes Ours and Theirs (common lines + the lines of that side in each conflict, a check box per
// conflict line, "Prendre ce bloc" in each conflict header), editable Result below. The app keeps the choices: it
// sends the state of every conflict (labels, kept lines) and the lines of a conflict whose choice changed; the page
// replaces them in the result, where each conflict is tracked by a decoration (manual edits elsewhere are kept).

const CF_HEADER_HEIGHT = 26;
const CF_CARD_HEIGHT = 40;
const SIDES = ['ours', 'theirs'];
let cf = null;            // { labels, segments, blocks, current, panes: { ours, theirs }, ranges, lineIds, zones }
let cfApplying = false;   // the page itself is editing the result: not a user edit

function conflictEditors() {
  return conflictUi ? [conflictUi.ours.editor, conflictUi.theirs.editor, conflictUi.result.editor] : [];
}

function ensureConflictUi() {
  if (conflictUi) return conflictUi;
  const host = hosts.conflict = document.createElement('div');
  host.className = 'vg-host vg-conflict';
  root.appendChild(host);
  const top = document.createElement('div');
  top.className = 'vg-cf-top';
  host.appendChild(top);

  const key = (name) => () => post({ type: 'key', key: name, vk: 0, ctrl: false, shift: false, alt: false });
  const pane = (parent, side) => {
    const section = document.createElement('section');
    section.className = 'vg-cf-pane';
    parent.appendChild(section);
    const head = document.createElement('div');
    head.className = 'vg-cf-head';
    section.appendChild(head);
    const body = document.createElement('div');
    body.className = 'vg-cf-body';
    section.appendChild(body);
    const editable = side === 'result';
    const e = monaco.editor.create(body, {
      ...baseOptions(),
      readOnly: !editable,
      glyphMargin: !editable,
      folding: false,
      lineNumbersMinChars: 3,
      lineDecorationsWidth: editable ? 8 : 4,
    });
    e.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.UpArrow, key('AltUp'), 'editorTextFocus');
    e.addCommand(monaco.KeyMod.Alt | monaco.KeyCode.DownArrow, key('AltDown'), 'editorTextFocus');
    // Conflict headers and cards keep the width of the visible text and do not scroll sideways (see CSS).
    const width = layout => section.style.setProperty('--vg-content-width', `${layout.contentWidth - layout.verticalScrollbarWidth}px`);
    e.onDidLayoutChange(width);
    width(e.getLayoutInfo());
    e.onDidScrollChange(() => section.style.setProperty('--vg-scroll-left', `${e.getScrollLeft()}px`));
    if (!editable) {
      e.addCommand(monaco.KeyCode.Escape, key('Escape'), 'editorTextFocus && !findWidgetVisible');
      e.onMouseDown(m => onConflictMouseDown(side, m));
    } else {
      e.onDidChangeModelContent(() => {
        if (!cfApplying && mode === 'conflict') post({ type: 'changed' });
      });
    }
    return { section, head, editor: e, decorations: e.createDecorationsCollection(), zones: [] };
  };
  conflictUi = { ours: pane(top, 'ours'), theirs: pane(top, 'theirs'), result: pane(host, 'result') };
  return conflictUi;
}

/** Header of a pane: color swatch, title, detail, then the given buttons on the right. */
function fillHead(head, swatch, title, detail, buttons) {
  head.replaceChildren();
  if (swatch) {
    const s = document.createElement('span');
    s.className = `vg-cf-swatch vg-cf-swatch-${swatch}`;
    head.appendChild(s);
  }
  const b = document.createElement('b');
  b.textContent = title;
  head.appendChild(b);
  const d = document.createElement('span');
  d.className = 'vg-cf-detail';
  d.textContent = detail;
  head.appendChild(d);
  const spacer = document.createElement('span');
  spacer.style.flex = '1';
  head.appendChild(spacer);
  for (const button of buttons) head.appendChild(button);
}

function cfButton(label, className, onClick) {
  const b = document.createElement('button');
  b.className = className;
  b.textContent = label;
  b.addEventListener('mousedown', e => e.stopPropagation());
  b.addEventListener('click', e => {
    e.stopPropagation();
    onClick();
  });
  return b;
}

function legend(labels) {
  const span = document.createElement('span');
  span.className = 'vg-cf-legend';
  for (const side of SIDES) {
    const s = document.createElement('span');
    s.className = `vg-cf-swatch vg-cf-swatch-${side}`;
    span.appendChild(s);
    span.appendChild(document.createTextNode(side === 'ours' ? labels.legendOurs : labels.legendTheirs));
  }
  return span;
}

/** Text of a pane: common lines and the lines of that side in each conflict; where each conflict starts. */
function buildPane(side) {
  const lines = [];
  const map = [];       // per line: { block, index } for a conflict line, null for a common one
  const starts = [];    // per conflict: first line (1-based) of its lines in the pane
  let block = 0;
  for (const segment of cf.segments) {
    if (segment.c) {
      for (const line of segment.c) {
        lines.push(line);
        map.push(null);
      }
      continue;
    }
    starts.push(lines.length + 1);
    const own = side === 'ours' ? segment.o : segment.t;
    own.forEach((line, index) => {
      lines.push(line);
      map.push({ block, index });
    });
    block++;
  }
  return { text: lines.join('\n'), map, starts, count: lines.length };
}

function showConflict(m) {
  const ui = ensureConflictUi();
  mode = 'conflict';
  doc = null;
  show('conflict');
  cf = { labels: m.labels, segments: m.segments, blocks: m.state.blocks, current: m.state.current, panes: {}, ranges: [], lineIds: [], zones: [] };

  const labels = m.labels;
  fillHead(ui.ours.head, 'ours', labels.oursTitle, labels.oursDetail,
    [cfButton(labels.takeAll, 'vg-link', () => post({ type: 'conflictTakeAll', side: 'ours' }))]);
  fillHead(ui.theirs.head, 'theirs', labels.theirsTitle, labels.theirsDetail,
    [cfButton(labels.takeAll, 'vg-link', () => post({ type: 'conflictTakeAll', side: 'theirs' }))]);
  fillHead(ui.result.head, null, labels.resultTitle, labels.resultDetail, [legend(labels)]);

  for (const side of SIDES) {
    const pane = cf.panes[side] = buildPane(side);
    replaceModel(ui[side].editor, newModel(pane.text, m.path));
  }

  // Result: common lines and the lines of the conflicts already chosen; each conflict tracked by a decoration.
  const lines = [];
  const starts = [];
  let block = 0;
  for (const segment of cf.segments) {
    if (segment.c) {
      lines.push(...segment.c);
      continue;
    }
    starts.push(lines.length + 1);
    for (const line of m.results[block] ?? []) lines.push(line.t);
    block++;
  }
  cfApplying = true;
  try {
    replaceModel(ui.result.editor, newModel(lines.map(l => l + '\n').join(''), m.path));
  } finally {
    cfApplying = false;
  }
  const model = ui.result.editor.getModel();
  cf.ranges = starts.map((start, b) => model.deltaDecorations([], [rangeDecoration(start, (m.results[b] ?? []).length)])[0]);
  cf.lineIds = starts.map((start, b) => model.deltaDecorations([], lineDecorations(start, m.results[b] ?? [])));

  for (const e of conflictEditors()) e.setScrollTop(0);
  renderConflict();
  revealConflict();
}

/** Decoration spanning the lines of a conflict in the result: from its first line to the start of the next one. */
function rangeDecoration(start, count) {
  return {
    range: new monaco.Range(start, 1, start + count, 1),
    options: { stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges },
  };
}

/** Background and left bar of the result lines, by the side they come from. */
function lineDecorations(start, lines) {
  return lines.map((line, k) => ({
    range: new monaco.Range(start + k, 1, start + k, 1),
    options: {
      isWholeLine: true,
      className: line.s === 'o' ? 'vg-cf-res-ours' : 'vg-cf-res-theirs',
      linesDecorationsClassName: line.s === 'o' ? 'vg-cf-bar-ours' : 'vg-cf-bar-theirs',
      stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
    },
  }));
}

/** Replaces the lines of a conflict in the result (its choice changed); undoable with Ctrl+Z. */
function setConflictBlock(block, lines) {
  const e = conflictUi.result.editor;
  const model = e.getModel();
  const range = model.getDecorationRange(cf.ranges[block]);
  if (!range) return;
  const start = range.startLineNumber;
  cfApplying = true;
  try {
    e.executeEdits('vigo', [{ range, text: lines.map(l => l.t + '\n').join('') }]);
    e.pushUndoStop();
  } finally {
    cfApplying = false;
  }
  cf.ranges[block] = model.deltaDecorations([cf.ranges[block]], [rangeDecoration(start, lines.length)])[0];
  cf.lineIds[block] = model.deltaDecorations(cf.lineIds[block], lineDecorations(start, lines));
}

function conflictState(m) {
  if (mode !== 'conflict' || !cf) return;
  cf.blocks = m.blocks;
  cf.current = m.current;
  for (const change of m.set ?? []) setConflictBlock(change.block, change.lines);
  renderConflict();
  if (m.reveal) revealConflict();
}

/** Decorations and view zones of the panes and of the result, from the state of the conflicts. */
function renderConflict() {
  if (mode !== 'conflict' || !cf) return;
  const ui = conflictUi;
  for (const side of SIDES) {
    const pane = cf.panes[side];
    const target = ui[side];
    const items = [];
    pane.map.forEach((ref, i) => {
      if (!ref) return;
      const state = cf.blocks[ref.block];
      const kept = state[side][ref.index];
      const current = ref.block === cf.current;
      items.push({
        range: new monaco.Range(i + 1, 1, i + 1, 1),
        options: {
          isWholeLine: true,
          className: `vg-cf-line-${side}${current ? ' vg-cf-current' : ''}`,
          marginClassName: `vg-cf-line-${side}`,
          glyphMarginClassName: kept ? 'vg-check vg-check-on' : 'vg-check',
          glyphMarginHoverMessage: { value: cf.labels.toggleTip },
        },
      });
      if (state.done && !kept) {
        items.push({ range: new monaco.Range(i + 1, 1, i + 1, Number.MAX_SAFE_INTEGER), options: { inlineClassName: 'vg-cf-dropped' } });
      }
    });
    target.decorations.set(items);
    target.editor.changeViewZones(accessor => {
      for (const id of target.zones) accessor.removeZone(id);
      target.zones = pane.starts.map((start, block) => {
        const node = document.createElement('div');
        node.className = `vg-cf-block vg-cf-block-${side}${block === cf.current ? ' vg-cf-current' : ''}`;
        const label = document.createElement('span');
        label.className = 'vg-cf-block-label';
        label.textContent = side === 'ours' ? cf.blocks[block].oursLabel : cf.blocks[block].theirsLabel;
        node.appendChild(label);
        node.appendChild(cfButton(cf.labels.take, 'vg-button vg-button-small',
          () => post({ type: 'conflictTake', block, take: side })));
        return accessor.addZone({ afterLineNumber: start - 1, heightInPx: CF_HEADER_HEIGHT, domNode: zoneNode(node), suppressMouseDown: true });
      });
    });
  }

  // Result: a card for each conflict not resolved yet (mockup: "Conflit 1 non résolu" + Ours / Theirs / Les deux).
  const result = ui.result;
  const model = result.editor.getModel();
  result.editor.changeViewZones(accessor => {
    for (const id of result.zones) accessor.removeZone(id);
    result.zones = [];
    cf.blocks.forEach((state, block) => {
      if (state.done) return;
      const range = model.getDecorationRange(cf.ranges[block]);
      if (!range) return;
      const card = document.createElement('div');
      card.className = `vg-cf-card${block === cf.current ? ' vg-cf-current' : ''}`;
      const label = document.createElement('span');
      label.className = 'vg-cf-card-label';
      label.textContent = state.unresolved;
      card.appendChild(label);
      for (const [take, text] of [['ours', cf.labels.ours], ['theirs', cf.labels.theirs], ['both', cf.labels.both]])
        card.appendChild(cfButton(text, 'vg-button', () => post({ type: 'conflictTake', block, take })));
      result.zones.push(accessor.addZone({ afterLineNumber: range.startLineNumber - 1, heightInPx: CF_CARD_HEIGHT, domNode: zoneNode(card), suppressMouseDown: true }));
    });
  });
}

/** Scrolls the panes and the result to the current conflict. */
function revealConflict() {
  if (!cf || cf.current < 0) return;
  const ui = conflictUi;
  for (const side of SIDES) {
    const start = cf.panes[side].starts[cf.current];
    if (!start) continue;
    const e = ui[side].editor;
    const line = Math.min(start, Math.max(1, cf.panes[side].count));
    e.setScrollTop(Math.max(0, e.getTopForLineNumber(line) - CF_HEADER_HEIGHT - 2 * shared.lineHeight));
  }
  const range = ui.result.editor.getModel().getDecorationRange(cf.ranges[cf.current]);
  if (range) {
    const e = ui.result.editor;
    e.setScrollTop(Math.max(0, e.getTopForLineNumber(range.startLineNumber) - CF_CARD_HEIGHT - 2 * shared.lineHeight));
  }
}

/** Click on the check box (glyph margin) or the line number of a conflict line: includes or excludes it. */
function onConflictMouseDown(side, e) {
  if (mode !== 'conflict' || !cf || !e.target.position) return;
  const type = e.target.type;
  if (type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN && type !== monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS) return;
  const ref = cf.panes[side].map[e.target.position.lineNumber - 1];
  if (!ref) return;
  post({ type: 'conflictToggle', block: ref.block, side, line: ref.index });
}

function clearConflict() {
  cf = null;
  if (!conflictUi) return;
  for (const name of ['ours', 'theirs', 'result']) {
    const target = conflictUi[name];
    target.editor.changeViewZones(accessor => {
      for (const id of target.zones) accessor.removeZone(id);
    });
    target.zones = [];
    target.decorations.clear();
    cfApplying = true;
    try {
      replaceModel(target.editor, null);
    } finally {
      cfApplying = false;
    }
  }
}

// ---------------------------------------------------------------- blame (J5.6)

// Mockup "Blame": the file read-only in Monaco; on its left, a column drawn by the page (outside Monaco) with, for
// each block of lines from the same commit, an age bar, the author's avatar, the subject and the date on its first
// line. Only the visible rows exist; they follow the editor's scrolling (all lines have the same height: no wrapping,
// no view zone). The app sends the text, then the commits and the commit of each line as git finds them.
const BLAME_WIDTH = 303;
let blameUi = null;       // { host, column, editor, decorations }
let bl = null;            // { commits, lines, selected }
let blameCursorLine = 0;  // line of the caret already reported to the app

function ensureBlameUi() {
  if (blameUi) return blameUi;
  const host = hosts.blame = document.createElement('div');
  host.className = 'vg-host vg-blame';
  root.appendChild(host);
  const column = document.createElement('div');
  column.className = 'vg-bl-column';
  column.style.width = `${BLAME_WIDTH}px`;
  host.appendChild(column);
  const body = document.createElement('div');
  body.className = 'vg-bl-body';
  host.appendChild(body);

  const e = monaco.editor.create(body, {
    ...baseOptions(),
    readOnly: true,
    domReadOnly: false,
    glyphMargin: false,
    folding: false,
    lineNumbersMinChars: 4,
    lineDecorationsWidth: 8,
    wordWrap: 'off',
  });
  const key = (name) => () => post({ type: 'key', key: name, vk: 0, ctrl: false, shift: false, alt: false });
  e.addCommand(monaco.KeyCode.Escape, key('Escape'), 'editorTextFocus && !findWidgetVisible');
  e.onDidScrollChange(renderBlameColumn);
  e.onDidLayoutChange(renderBlameColumn);
  // The commit of the caret's line is the selected one: a click or the arrow keys select it.
  e.onDidChangeCursorPosition(event => {
    if (mode !== 'blame' || event.position.lineNumber === blameCursorLine) return;
    blameCursorLine = event.position.lineNumber;
    post({ type: 'blameSelect', line: blameCursorLine });
  });
  // The wheel over the column scrolls the editor; a click there selects the line's commit.
  column.addEventListener('wheel', event => {
    event.preventDefault();
    e.setScrollTop(e.getScrollTop() + (event.deltaMode === 1 ? event.deltaY * shared.lineHeight : event.deltaY));
  }, { passive: false });
  column.addEventListener('mousedown', event => {
    const row = event.target.closest('.vg-bl-row');
    if (!row) return;
    event.preventDefault();
    const line = Number(row.dataset.line);
    e.setPosition({ lineNumber: line, column: 1 });
    e.focus();
  });
  blameUi = { host, column, editor: e, decorations: e.createDecorationsCollection() };
  return blameUi;
}

function showBlame(m) {
  const ui = ensureBlameUi();
  mode = 'blame';
  doc = null;
  show('blame');
  bl = { commits: [], lines: [], selected: -1 };
  blameCursorLine = 0;
  replaceModel(ui.editor, newModel(m.text, m.path));
  ui.editor.setScrollTop(0);
  ui.decorations.clear();
  renderBlameColumn();
}

function blameState(m) {
  if (mode !== 'blame' || !bl) return;
  bl.commits = m.commits;
  bl.lines = m.lines;
  bl.selected = m.selected;
  renderBlame();
}

function blameSelection(m) {
  if (mode !== 'blame' || !bl) return;
  bl.selected = m.selected;
  renderBlame();
}

/** Editor decorations (lines of the selected commit, first line of each block), then the column. */
function renderBlame() {
  const ui = blameUi;
  const items = [];
  const lines = bl.lines;
  for (let i = 0; i < lines.length; i++) {
    const c = lines[i];
    const first = i > 0 && c !== lines[i - 1];
    const selected = c >= 0 && c === bl.selected;
    if (!first && !selected) continue;
    items.push({
      range: new monaco.Range(i + 1, 1, i + 1, 1),
      options: { isWholeLine: true, className: `${first ? 'vg-bl-first' : ''} ${selected ? 'vg-bl-selected' : ''}`.trim() },
    });
  }
  ui.decorations.set(items);
  renderBlameColumn();
}

function renderBlameColumn() {
  if (mode !== 'blame' || !blameUi || !bl) return;
  const { editor: e, column } = blameUi;
  const rows = [];
  const scrollTop = e.getScrollTop();
  const height = shared.lineHeight;
  const model = e.getModel();
  const count = model ? model.getLineCount() : 0;
  for (const range of e.getVisibleRanges()) {
    for (let line = range.startLineNumber; line <= range.endLineNumber && line <= count; line++) {
      const c = bl.lines[line - 1] ?? -1;
      const commit = c >= 0 ? bl.commits[c] : null;
      const first = line === 1 || c !== (bl.lines[line - 2] ?? -1);
      const row = document.createElement('div');
      row.className = `vg-bl-row${first && line > 1 ? ' vg-bl-row-first' : ''}${c >= 0 && c === bl.selected ? ' vg-bl-row-selected' : ''}`;
      row.dataset.line = String(line);
      row.style.top = `${e.getTopForLineNumber(line) - scrollTop}px`;
      row.style.height = `${height}px`;
      const age = document.createElement('span');
      age.className = 'vg-bl-age';
      if (commit) age.style.opacity = String(0.15 + 0.85 * commit.age);
      row.appendChild(age);
      if (commit && first) {
        const info = document.createElement('span');
        info.className = 'vg-bl-info';
        const avatar = document.createElement('span');
        avatar.className = 'vg-bl-avatar';
        avatar.style.background = commit.color;
        avatar.textContent = commit.initials;
        const summary = document.createElement('span');
        summary.className = 'vg-bl-summary';
        summary.textContent = commit.summary;
        const date = document.createElement('span');
        date.className = 'vg-bl-date';
        date.textContent = commit.date;
        info.append(avatar, summary, date);
        info.title = commit.tip;
        row.appendChild(info);
      }
      rows.push(row);
    }
  }
  column.replaceChildren(...rows);
}

function clearBlame() {
  bl = null;
  if (!blameUi) return;
  blameUi.decorations.clear();
  blameUi.column.replaceChildren();
  replaceModel(blameUi.editor, null);
}

function clear() {
  mode = 'empty';
  doc = null;
  clearConflict();
  clearBlame();
  if (editor) {
    editor.changeViewZones(accessor => {
      for (const id of zoneIds) accessor.removeZone(id);
    });
    zoneIds = [];
    decorations.clear();
    updateSticky(true);
    replaceModel(editor, null);
    contextKeys.diff.set(false);
    contextKeys.staging.set(false);
    contextKeys.discard.set(false);
  }
  if (diffEditor) {
    const old = diffEditor.getModel();
    diffEditor.setModel(null);
    old?.original.dispose();
    old?.modified.dispose();
  }
  review.refresh();
}

// ---------------------------------------------------------------- review surfaces (J6.11)

/** Editors of the diff on screen, with the mapping between their lines and the lines of the file (new / old version). */
function reviewSurfaces() {
  if (mode === 'sideBySide' && diffEditor) {
    const make = (target, side) => {
      const count = () => target.getModel()?.getLineCount() ?? 0;
      return {
        editor: target,
        side,
        modelLine: (s, line) => (s === side ? Math.min(Math.max(line, 1), Math.max(count(), 1)) : null),
        rangeOfLine: line => {
          const model = target.getModel();
          return model && line >= 1 && line <= count() ? { side, startLine: line, line, startColumn: 1, endColumn: model.getLineMaxColumn(line) } : null;
        },
        rangeOfSelection: sel => {
          const last = sel.endColumn === 1 && sel.endLineNumber > sel.startLineNumber ? sel.endLineNumber - 1 : sel.endLineNumber;
          const model = target.getModel();
          if (!model) return null;
          return {
            side, startLine: sel.startLineNumber, line: last, startColumn: sel.startColumn,
            endColumn: last === sel.endLineNumber ? sel.endColumn : model.getLineMaxColumn(last),
          };
        },
      };
    };
    return [make(diffEditor.getModifiedEditor(), 'new'), make(diffEditor.getOriginalEditor(), 'old')];
  }
  if (mode === 'diff' && editor && doc) {
    const numberAt = (side, i) => (side === 'old' ? doc.old[i] : doc.new[i]) || 0;
    const sideOf = line => (doc.kinds[line - 1] === 'd' ? 'old' : doc.kinds[line - 1] === 'n' || !doc.kinds[line - 1] ? null : 'new');
    const maxColumn = line => editor.getModel()?.getLineMaxColumn(line) ?? 1;
    return [{
      editor,
      side: 'both',
      modelLine: (side, fileLine) => {
        for (let i = 0; i < doc.kinds.length; i++) if (numberAt(side, i) === fileLine && doc.kinds[i] !== 'n') return i + 1;
        return null;
      },
      rangeOfLine: line => {
        const side = sideOf(line);
        const n = side ? numberAt(side, line - 1) : 0;
        return n ? { side, startLine: n, line: n, startColumn: 1, endColumn: maxColumn(line) } : null;
      },
      rangeOfSelection: sel => {
        let end = sel.endColumn === 1 && sel.endLineNumber > sel.startLineNumber ? sel.endLineNumber - 1 : sel.endLineNumber;
        while (end >= sel.startLineNumber && !sideOf(end)) end--;
        if (end < sel.startLineNumber) return null;
        const side = sideOf(end);
        let start = sel.startLineNumber;
        while (start < end && !numberAt(side, start - 1)) start++;
        return {
          side,
          startLine: numberAt(side, start - 1),
          line: numberAt(side, end - 1),
          startColumn: start === sel.startLineNumber ? sel.startColumn : 1,
          endColumn: end === sel.endLineNumber ? sel.endColumn : maxColumn(end),
        };
      },
    }];
  }
  return [];
}

function focusEditor() {
  if (mode === 'sideBySide') diffEditor?.getModifiedEditor().focus();
  else if (mode === 'conflict') conflictUi?.result.editor.focus();
  else if (mode === 'blame') blameUi?.editor.focus();
  else editor?.focus();
}

// ---------------------------------------------------------------- keys for the app

// Shortcuts of the app (Ctrl+T, Ctrl+P, F5…) are not seen by XAML while the page has the focus: keys with Ctrl or
// Alt, and function keys, that the editor did not use go back to the app. keyCode is the Windows virtual-key code of
// the active keyboard layout (e.code is the physical QWERTY position: Ctrl+A on AZERTY would be "KeyQ").
// Never forwarded: AltGr characters (Ctrl+Alt on French keyboards), keys typed in the find box, clipboard and caret
// keys (the browser handles them natively, without the editor marking them as handled), Alt+F4.
const NATIVE = new Set([
  65, 67, 86, 88,         // A C V X
  45, 46, 8,              // Insert Delete Backspace
  33, 34, 35, 36,         // PageUp PageDown End Home
  37, 38, 39, 40,         // arrows
]);

window.addEventListener('keydown', e => {
  if (e.defaultPrevented || e.isComposing || e.getModifierState('AltGraph')) return;
  const fn = e.keyCode >= 112 && e.keyCode <= 135; // F1-F24
  if (!fn && !e.ctrlKey && !e.altKey) return;
  if ([16, 17, 18, 91, 92].includes(e.keyCode)) return; // modifiers alone
  if (e.altKey && e.keyCode === 115) return; // Alt+F4 closes the window
  // A text field of a widget (find box…): its keys stay there. The editor's own input area is not one of them.
  const field = (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) && !e.target.classList.contains('inputarea');
  if (!fn && (NATIVE.has(e.keyCode) || field)) return;
  // Ctrl+Z / Ctrl+Y undo the text being edited; in a read-only view they are the app's Undo / Redo.
  if ((e.keyCode === 90 || e.keyCode === 89) && mode === 'text' && !editor?.getOption(monaco.editor.EditorOption.readOnly)) return;
  if ((e.keyCode === 90 || e.keyCode === 89) && mode === 'conflict' && conflictUi?.result.editor.hasTextFocus()) return;
  e.preventDefault();
  post({ type: 'key', key: e.code, vk: e.keyCode, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey });
});

// ---------------------------------------------------------------- messages

const handlers = {
  theme: applyTheme,
  diff: showDiff,
  selection: setSelection,
  revealHunk,
  sideBySide: showSideBySide,
  text: showText,
  conflict: showConflict,
  conflictState,
  blame: showBlame,
  blameState,
  blameSelection,
  review: m => review.setState(m),
  reviewResult: m => review.result(m),
  refResults: m => review.references(m),
  clear,
  focus: focusEditor,
  getText: m => post({
    type: 'text',
    id: m.id,
    text: textOf(m.target === 'result' ? conflictUi?.result.editor : editor),
  }),
};

webview?.addEventListener('message', e => {
  const message = e.data;
  const handler = handlers[message?.type];
  if (!handler) return;
  try {
    handler(message);
  } catch (error) {
    post({ type: 'error', message: String(error?.stack ?? error) });
  }
});

window.addEventListener('error', e => post({ type: 'error', message: String(e.error?.stack ?? e.message) }));
post({ type: 'ready' });
