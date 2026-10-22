// Inline review of a pull request (J6.11): discussion threads and the comment box are drawn as view zones under the code
// lines they are about, in the unified diff and in the side-by-side diff (Azure DevOps style). Everything the user reads
// comes from the app (labels, localized); comments are Markdown, rendered here (markdown-it, sanitized by DOMPurify).
//
// App -> page: { type: 'review', enabled, canDraft, labels, threads, drafts }, { type: 'reviewResult', key, ok }.
//   `reveal: { thread, token }` in the review message scrolls to a thread (once per token) and flashes its card.
// Page -> app: reviewSubmit { key, mode: 'now' | 'draft', side, startLine, line, startColumn, endColumn, body },
//              reviewReply { key, threadId, body }, reviewResolve { key, threadId, resolved }, reviewRemoveDraft { index }.
import MarkdownIt from 'markdown-it';
import DOMPurify from 'dompurify';

const ZONE_GAP = 8;

const ICONS = {
  heading: '<path d="M4 3v10M12 3v10M4 8h8" />',
  link: '<path d="M6.5 9.5l3-3M7 4.5l1-1a2.5 2.5 0 013.5 3.5l-1 1M9 11.5l-1 1A2.5 2.5 0 014.5 9l1-1" />',
  bullets: '<path d="M6 4h7M6 8h7M6 12h7" /><circle cx="3" cy="4" r="0.9" fill="currentColor" /><circle cx="3" cy="8" r="0.9" fill="currentColor" /><circle cx="3" cy="12" r="0.9" fill="currentColor" />',
  numbers: '<path d="M7 4h6M7 8h6M7 12h6M3 3l1-.5V6M2.8 9.6c.5-.7 1.5-.6 1.5.1 0 .6-1.3 1-1.5 1.8h1.7" />',
  quote: '<path d="M3 6.5c0-1.5 1-2.5 2.5-2.7M3 6.5h2.5V10H3zM9 6.5c0-1.5 1-2.5 2.5-2.7M9 6.5h2.5V10H9z" />',
  code: '<path d="M6 4L2.5 8 6 12M10 4l3.5 4L10 12" />',
  block: '<path d="M5.5 4C4 4 4 5 4 6s0 2-1.5 2C4 8 4 9 4 10s0 2 1.5 2M10.5 4C12 4 12 5 12 6s0 2 1.5 2c-1.5 0-1.5 1-1.5 2s0 2-1.5 2" />',
  suggest: '<path d="M8 3v6M5 6h6M5 12h6" />',
  comment: '<path d="M2.5 3.5h11v7h-6l-3 2.5v-2.5h-2z" />',
  plus: '<path d="M8 3v10M3 8h10" />',
};

function icon(name) {
  return `<svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name]}</svg>`;
}

function escapeHtml(text) {
  return text.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

export function createReview({ monaco, post, surfaces }) {
  const md = new MarkdownIt({ html: true, linkify: true, breaks: true });
  const defaultFence = md.renderer.rules.fence;
  md.renderer.rules.fence = (tokens, idx, options, env, self) => {
    const token = tokens[idx];
    if (token.info.trim() === 'suggestion') {
      // Like Azure DevOps: the lines the suggestion replaces (red), then the suggested ones (green).
      const lines = text => (text === '' ? [] : text.replace(/\n$/, '').split('\n'));
      const row = (kind, sign, line) => `<div class="vg-rv-${kind}"><span class="vg-rv-sign">${sign}</span><span class="vg-rv-code">${escapeHtml(line) || '&nbsp;'}</span></div>`;
      const removed = typeof env?.original === 'string' ? lines(env.original).map(l => row('del', '\u2212', l)) : [];
      const added = lines(token.content).map(l => row('add', '+', l));
      return `<div class="vg-rv-suggestion"><div class="vg-rv-suggestion-title">${escapeHtml(state.labels.suggestion ?? '')}</div>`
        + `<div class="vg-rv-diff">${removed.join('')}${added.join('')}</div></div>`;
    }
    return defaultFence(tokens, idx, options, env, self);
  };

  const state = {
    enabled: false,
    canComment: false,
    canDraft: false,
    labels: {},
    links: {},
    mentions: {},        // identity id -> name, for the @<id> mentions of Azure DevOps
    canComplete: false,
    file: null,
    threads: [],
    drafts: [],
    composer: null,          // { side, startLine, line, startColumn, endColumn, text, suggestion, preview, busyKey }
    replies: new Map(),      // thread id -> { text, open, preview, busyKey }
    expanded: new Set(),     // resolved threads shown open
  };
  let pendingReveal = null;  // thread id to scroll to once its card is drawn
  let lastRevealToken = 0;   // the app asks again by changing the token
  let entries = [];          // rendered zones: { editor, id, zone, observer, decorations, threadId }
  let adornments = [];       // per editor: selection button, hover "+"
  let nextKey = 0;
  const pendingKeys = new Map();
  const pendingReferences = new Map();

  // The text box that had the focus when the last press started (see addZone).
  let lastArea = null;
  window.addEventListener('pointerdown', () => {
    lastArea = document.activeElement instanceof HTMLTextAreaElement && document.activeElement.classList.contains('vg-rv-input') ? document.activeElement : null;
  }, true);

  // #123 (issue / work item), !123 (pull request), @name (person): links to the service when the app gave their address.
  md.core.ruler.push('vg_references', coreState => {
    const links = state.links ?? {};
    const pattern = /(^|[\s(])([#!@])([A-Za-z0-9][A-Za-z0-9_.-]*)/g;
    for (const block of coreState.tokens) {
      if (block.type !== 'inline' || !block.children) continue;
      const out = [];
      let inLink = false;
      for (const token of block.children) {
        if (token.type === 'link_open') inLink = true;
        if (token.type === 'link_close') inLink = false;
        if (token.type !== 'text' || inLink) { out.push(token); continue; }
        let last = 0;
        const text = token.content;
        for (const m of text.matchAll(pattern)) {
          const [whole, lead, sign, name] = m;
          const numeric = /^\d+$/.test(name);
          if (sign !== '@' && !numeric) continue;
          const template = sign === '#' ? links.issue : sign === '!' ? links.pull : links.user;
          const start = m.index + lead.length;
          if (start > last) { const t = new coreState.Token('text', '', 0); t.content = text.slice(last, start); out.push(t); }
          const label = sign + name;
          if (template) {
            const open = new coreState.Token('link_open', 'a', 1);
            open.attrs = [['href', template.replace('{0}', encodeURIComponent(name))], ['class', 'vg-rv-ref']];
            const t = new coreState.Token('text', '', 0); t.content = label;
            out.push(open, t, new coreState.Token('link_close', 'a', -1));
          } else {
            const span = new coreState.Token('html_inline', '', 0);
            span.content = `<span class="vg-rv-mention">${escapeHtml(label)}</span>`;
            out.push(span);
          }
          last = m.index + whole.length;
        }
        if (last === 0) out.push(token);
        else if (last < text.length) { const t = new coreState.Token('text', '', 0); t.content = text.slice(last); out.push(t); }
      }
      block.children = out;
    }
  });

  /** Markdown to sanitized HTML; <paramref name="original"/> is the code a suggestion replaces. */
  document.addEventListener('click', e => {
    const link = e.target instanceof Element ? e.target.closest('.vg-rv-md a[href]') : null;
    if (!link) return;
    e.preventDefault();
    post({ type: 'openLink', url: link.href });
  });

  const markdown = (text, original) => {
    // An Azure DevOps mention is stored as @<identity id> and shown as the name.
    const named = (text ?? '').replace(/@<([0-9a-fA-F-]{36})>/g,
      (_, id) => `<span class="vg-rv-mention vg-rv-person">${escapeHtml(state.mentions[id] ?? state.labels.unknownPerson ?? '?')}</span>`);
    return DOMPurify.sanitize(md.render(named, { original }), { FORBID_TAGS: ['img', 'style', 'form', 'input', 'iframe'] });
  };

  // ---------------------------------------------------------------- Markdown box

  function surround(area, before, after, placeholder) {
    const { selectionStart: start, selectionEnd: end, value } = area;
    const chosen = value.slice(start, end) || placeholder;
    area.setRangeText(before + chosen + after, start, end, 'preserve');
    area.focus();
    area.setSelectionRange(start + before.length, start + before.length + chosen.length);
    area.dispatchEvent(new Event('input'));
  }

  /** Inserts a block on its own lines at the caret (replacing the selection of the text area). */
  function insertBlock(area, block) {
    const { selectionStart: start, selectionEnd: end, value } = area;
    const before = start > 0 && value[start - 1] !== '\n' ? '\n' : '';
    const after = end < value.length && value[end] !== '\n' ? '\n' : '';
    area.setRangeText(before + block + after, start, end, 'end');
    area.focus();
    area.dispatchEvent(new Event('input'));
  }

  function prefixLines(area, prefix) {
    const { value } = area;
    const start = value.lastIndexOf('\n', area.selectionStart - 1) + 1;
    let end = value.indexOf('\n', area.selectionEnd);
    if (end < 0) end = value.length;
    const block = value.slice(start, end).split('\n').map((line, i) => (typeof prefix === 'function' ? prefix(i) : prefix) + line).join('\n');
    area.setRangeText(block, start, end, 'select');
    area.focus();
    area.dispatchEvent(new Event('input'));
  }

  /**
   * A Markdown input: toolbar (heading, bold, italic, underline, strikethrough, code, code block, link, lists, quote,
   * suggestion), write / preview tabs, and the action buttons.
   */
  function markdownBox(options) {
    const labels = state.labels;
    const box = el('div', 'vg-rv-box');
    const bar = el('div', 'vg-rv-bar');
    const area = el('textarea', 'vg-rv-input');
    const preview = el('div', 'vg-rv-preview vg-rv-md');
    area.placeholder = options.placeholder ?? '';
    area.value = options.text ?? '';
    area.rows = 3;
    area.spellcheck = true;

    const buttons = [
      ['heading', labels.heading, () => prefixLines(area, '### ')],
      ['B', labels.bold, () => surround(area, '**', '**', labels.boldSample), 'vg-rv-bold'],
      ['I', labels.italic, () => surround(area, '_', '_', labels.italicSample), 'vg-rv-italic'],
      ['U', labels.underline, () => surround(area, '<u>', '</u>', labels.underlineSample), 'vg-rv-underline'],
      ['S', labels.strike, () => surround(area, '~~', '~~', labels.strikeSample), 'vg-rv-strike'],
      ['code', labels.code, () => surround(area, '`', '`', labels.codeSample)],
      ['block', labels.codeBlock, () => surround(area, '\n```\n', '\n```\n', labels.codeSample)],
      ['link', labels.link, () => surround(area, '[', '](https://)', labels.linkSample)],
      ['bullets', labels.bulletList, () => prefixLines(area, '- ')],
      ['numbers', labels.numberList, () => prefixLines(area, i => `${i + 1}. `)],
      ['quote', labels.quote, () => prefixLines(area, '> ')],
    ];
    if (options.suggestion !== undefined) {
      buttons.push(['suggest', labels.suggest, () => insertBlock(area, '```suggestion\n' + options.suggestion + '\n```')]);
    }
    for (const [glyph, title, run, className] of buttons) {
      const button = el('button', `vg-rv-tool${className ? ` ${className}` : ''}`);
      button.type = 'button';
      button.title = title ?? '';
      button.setAttribute('aria-label', title ?? '');
      if (ICONS[glyph]) button.innerHTML = icon(glyph); else button.textContent = glyph;
      // The text area keeps its selection: a click on the toolbar must not move the focus.
      button.addEventListener('mousedown', e => e.preventDefault());
      button.addEventListener('click', run);
      bar.appendChild(button);
    }

    // The preview sits under the action buttons and follows the text while it is typed.
    let previewOn = options.preview !== false;
    const toggle = el('button', 'vg-rv-tool vg-rv-toggle', labels.preview);
    toggle.type = 'button';
    toggle.addEventListener('mousedown', e => e.preventDefault());
    const showPreview = () => {
      preview.style.display = previewOn ? '' : 'none';
      toggle.classList.toggle('vg-rv-toggle-on', previewOn);
      toggle.setAttribute('aria-pressed', String(previewOn));
      if (previewOn) preview.innerHTML = area.value.trim() ? markdown(area.value, options.original) : `<span class="vg-rv-empty">${escapeHtml(labels.nothingToPreview)}</span>`;
    };
    toggle.addEventListener('click', () => {
      previewOn = !previewOn;
      options.onPreview?.(previewOn);
      showPreview();
    });
    bar.appendChild(toggle);

    const actions = el('div', 'vg-rv-actions');
    const submit = (mode) => {
      const text = area.value.trim();
      if (text) options.onSubmit(text, mode);
    };
    const refresh = () => {
      for (const b of actions.querySelectorAll('button[data-needs-text]')) b.disabled = !area.value.trim() || !!options.busy;
    };
    for (const action of options.actions) {
      const button = el('button', `vg-button${action.primary ? ' vg-button-primary' : ''}`, action.label);
      button.type = 'button';
      button.dataset.needsText = '1';
      button.addEventListener('click', () => submit(action.mode));
      actions.appendChild(button);
    }
    const cancel = el('button', 'vg-button', labels.cancel);
    cancel.type = 'button';
    cancel.disabled = !!options.busy;
    cancel.addEventListener('click', options.onCancel);
    actions.appendChild(cancel);

    const grow = () => {
      area.style.height = 'auto';
      area.style.height = `${Math.min(Math.max(area.scrollHeight, 66), 320)}px`;
    };
    // Completion after #, ! and @: the app searches the service, the proposals show in a list under the text area.
    const menu = el('div', 'vg-rv-menu vg-rv-popup');
    menu.style.display = 'none';
    let proposals = [];
    let current = 0;
    let target = null;
    let waiting = 0;
    let timer = null;
    const closeMenu = () => {
      clearTimeout(timer);
      waiting = 0;
      proposals = [];
      target = null;
      menu.style.display = 'none';
      menu.replaceChildren();
      menu.remove();
    };
    const tokenAtCaret = () => {
      if (area.selectionStart !== area.selectionEnd) return null;
      const before = area.value.slice(0, area.selectionStart);
      const m = /(^|[\s(])([#!@])([\w.-]*)$/.exec(before);
      if (!m) return null;
      return { sign: m[2], query: m[3], start: before.length - m[2].length - m[3].length, end: before.length };
    };
    const apply = item => {
      if (!target) return;
      const mention = /^@<([0-9a-fA-F-]{36})>$/.exec(item.insert);
      if (mention) state.mentions[mention[1]] = item.label;
      area.setRangeText(item.insert + ' ', target.start, target.end, 'end');
      closeMenu();
      area.focus();
      area.dispatchEvent(new Event('input'));
    };
    const drawMenu = () => {
      menu.replaceChildren();
      proposals.forEach((item, index) => {
        const row = el('div', `vg-rv-menu-item${index === current ? ' vg-rv-menu-on' : ''}`);
        row.append(el('span', 'vg-rv-menu-label', item.label), el('span', 'vg-rv-menu-detail', item.detail));
        // The text area keeps the focus (and its caret) while the list is clicked.
        row.addEventListener('mousedown', e => { e.preventDefault(); apply(item); });
        menu.appendChild(row);
      });
      if (proposals.length === 0) { menu.style.display = 'none'; menu.remove(); return; }
      // A popup on the page (the zone of the box would clip it), under the character being completed, above it when the
      // window has no room below.
      const point = caretPoint(area, target.start);
      menu.style.display = '';
      menu.style.left = '0px';
      menu.style.top = '0px';
      document.body.appendChild(menu);
      const size = menu.getBoundingClientRect();
      const lineHeight = parseFloat(getComputedStyle(area).lineHeight) || 18;
      menu.style.left = `${Math.max(4, Math.min(point.x, window.innerWidth - size.width - 4))}px`;
      menu.style.top = `${point.y + size.height > window.innerHeight - 4 ? Math.max(4, point.y - lineHeight - size.height) : point.y}px`;
    };
    const updateMenu = () => {
      const found = state.canComplete ? tokenAtCaret() : null;
      if (!found) { closeMenu(); return; }
      target = found;
      clearTimeout(timer);
      timer = setTimeout(() => {
        const key = ++nextKey;
        waiting = key;
        pendingReferences.set(key, items => {
          if (waiting !== key || !target) return;
          proposals = items;
          current = 0;
          drawMenu();
        });
        post({ type: 'refSearch', key, kind: found.sign === '#' ? 'issue' : found.sign === '!' ? 'pull' : 'person', query: found.query });
      }, 180);
    };
    area.addEventListener('blur', () => setTimeout(() => { if (document.activeElement !== area) closeMenu(); }, 100));

    area.addEventListener('input', () => {
      options.onInput?.(area.value);
      refresh();
      grow();
      if (previewOn) showPreview();
      updateMenu();
    });
    area.addEventListener('keydown', e => {
      // Keys typed here stay here (the page forwards Ctrl keys to the app otherwise).
      e.stopPropagation();
      if (proposals.length > 0 && menu.style.display !== 'none' && !e.ctrlKey) {
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
          e.preventDefault();
          current = (current + (e.key === 'ArrowDown' ? 1 : proposals.length - 1)) % proposals.length;
          drawMenu();
          return;
        }
        if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); apply(proposals[current]); return; }
        if (e.key === 'Escape') { e.preventDefault(); closeMenu(); return; }
      }
      if (e.key === 'Escape') { e.preventDefault(); options.onCancel(); return; }
      if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); submit(options.actions[0].mode); return; }
      if (e.ctrlKey && !e.shiftKey && !e.altKey) {
        const map = { b: '**', i: '_', u: '<u>' };
        const key = e.key.toLowerCase();
        if (map[key]) {
          e.preventDefault();
          if (key === 'u') surround(area, '<u>', '</u>', labels.underlineSample); else surround(area, map[key], map[key], key === 'b' ? labels.boldSample : labels.italicSample);
        }
      }
    });
    area.disabled = !!options.busy;

    box.append(bar, area, actions, preview);
    showPreview();
    refresh();
    queueMicrotask(grow);
    return { node: box, area };
  }

  // ---------------------------------------------------------------- cards

  function avatar(comment) {
    const node = el('span', 'vg-rv-avatar', comment.initials);
    node.style.background = comment.color;
    return node;
  }

  function commentNode(comment, original) {
    const node = el('div', 'vg-rv-comment');
    const head = el('div', 'vg-rv-comment-head');
    head.append(avatar(comment), el('span', 'vg-rv-author', comment.author), el('span', 'vg-rv-date', comment.date));
    const body = el('div', 'vg-rv-md');
    body.innerHTML = markdown(comment.body, original);
    node.append(head, body);
    return node;
  }

  function rangeText(item) {
    const labels = state.labels;
    if (item.startLine && item.startLine !== item.line) return `${labels.lines} ${item.startLine}–${item.line}`;
    return `${labels.line} ${item.line}`;
  }

  function threadCard(thread, outsideDiff, original) {
    const labels = state.labels;
    const card = el('div', `vg-rv-card${thread.resolved ? ' vg-rv-resolved' : ''}`);
    const reply = state.replies.get(thread.id) ?? { text: '', open: false, preview: true, busyKey: null };
    state.replies.set(thread.id, reply);

    const collapsed = thread.resolved && !state.expanded.has(thread.id);
    const head = el('div', 'vg-rv-head');
    head.append(el('span', 'vg-rv-where', rangeText(thread)));
    if (outsideDiff) head.appendChild(el('span', 'vg-rv-status', labels.outsideDiff));
    if (thread.status) head.appendChild(el('span', 'vg-rv-status', thread.status));
    head.appendChild(el('span', 'vg-rv-spacer'));
    if (thread.resolved) {
      const toggle = el('button', 'vg-rv-link', collapsed ? labels.show : labels.hide);
      toggle.type = 'button';
      toggle.addEventListener('click', () => {
        if (state.expanded.has(thread.id)) state.expanded.delete(thread.id); else state.expanded.add(thread.id);
        render();
      });
      head.appendChild(toggle);
    }
    if (thread.canReply) {
      const resolve = el('button', 'vg-button', thread.resolved ? labels.reopen : labels.resolve);
      resolve.type = 'button';
      resolve.addEventListener('click', () => {
        const key = ++nextKey;
        pendingKeys.set(key, () => {});
        resolve.disabled = true;
        post({ type: 'reviewResolve', key, threadId: thread.id, resolved: !thread.resolved });
      });
      head.appendChild(resolve);
    }
    card.appendChild(head);
    if (collapsed) {
      card.appendChild(el('div', 'vg-rv-summary', `${thread.comments.length} ${labels.comments}`));
      return card;
    }

    for (const comment of thread.comments) card.appendChild(commentNode(comment, original));

    if (thread.canReply) {
      if (!reply.open) {
        const fake = el('button', 'vg-rv-reply', labels.replyPlaceholder);
        fake.type = 'button';
        fake.addEventListener('click', () => { reply.open = true; render(thread.id); });
        card.appendChild(fake);
      } else {
        const { node, area } = markdownBox({
          text: reply.text,
          placeholder: labels.replyPlaceholder,
          preview: reply.preview,
          original,
          busy: reply.busyKey !== null,
          actions: [{ label: labels.reply, mode: 'reply', primary: true }],
          onPreview: on => { reply.preview = on; },
          onInput: text => { reply.text = text; },
          onCancel: () => { reply.open = false; render(); },
          onSubmit: text => {
            const key = ++nextKey;
            reply.busyKey = key;
            pendingKeys.set(key, ok => {
              reply.busyKey = null;
              if (ok) { reply.text = ''; reply.open = false; }
              render();
            });
            post({ type: 'reviewReply', key, threadId: thread.id, body: text });
            render();
          },
        });
        card.appendChild(node);
        area.dataset.focusKey = `reply:${thread.id}`;
      }
    }
    return card;
  }

  function draftCard(draft, original) {
    const labels = state.labels;
    const card = el('div', 'vg-rv-card vg-rv-draft');
    const head = el('div', 'vg-rv-head');
    head.append(el('span', 'vg-rv-where', rangeText(draft)), el('span', 'vg-rv-status', labels.pending), el('span', 'vg-rv-spacer'));
    const remove = el('button', 'vg-button vg-button-danger', labels.removeDraft);
    remove.type = 'button';
    remove.addEventListener('click', () => post({ type: 'reviewRemoveDraft', index: draft.index }));
    head.appendChild(remove);
    const body = el('div', 'vg-rv-md');
    body.innerHTML = markdown(draft.body, original);
    card.append(head, body);
    return card;
  }

  function composerCard() {
    const labels = state.labels;
    const c = state.composer;
    const card = el('div', 'vg-rv-card vg-rv-new');
    const head = el('div', 'vg-rv-head');
    head.append(el('span', 'vg-rv-where', `${labels.commentOn} ${rangeText(c)}`));
    card.appendChild(head);
    const actions = [{ label: labels.comment, mode: 'now', primary: true }];
    if (state.canDraft) actions.push({ label: labels.addToReview, mode: 'draft' });
    const { node, area } = markdownBox({
      text: c.text,
      placeholder: labels.placeholder,
      preview: c.preview,
      original: c.suggestion,
      busy: c.busyKey !== null,
      suggestion: c.suggestion,
      actions,
      onPreview: on => { c.preview = on; },
      onInput: text => { c.text = text; },
      onCancel: () => { state.composer = null; render(); },
      onSubmit: (text, mode) => {
        const key = ++nextKey;
        c.busyKey = key;
        pendingKeys.set(key, ok => {
          c.busyKey = null;
          if (ok) state.composer = null;
          render();
        });
        post({
          type: 'reviewSubmit', key, mode, side: c.side, startLine: c.startLine, line: c.line,
          startColumn: c.startColumn, endColumn: c.endColumn, body: text,
        });
        render();
      },
    });
    area.dataset.focusKey = 'composer';
    card.appendChild(node);
    return card;
  }

  // ---------------------------------------------------------------- zones

  /** Viewport position (left, bottom of the line) of the character at <paramref name="index"/> in a text area, measured on a hidden copy. */
  function caretPoint(area, index) {
    const style = getComputedStyle(area);
    const mirror = document.createElement('div');
    for (const name of ['fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft',
      'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth', 'boxSizing', 'tabSize']) mirror.style[name] = style[name];
    Object.assign(mirror.style, { position: 'fixed', visibility: 'hidden', top: '0', left: '0', whiteSpace: 'pre-wrap', overflowWrap: 'break-word', width: style.width });
    mirror.textContent = area.value.slice(0, index);
    const marker = document.createElement('span');
    marker.textContent = '\u200b';
    mirror.appendChild(marker);
    document.body.appendChild(mirror);
    const rect = area.getBoundingClientRect();
    const lineHeight = parseFloat(style.lineHeight) || 18;
    const point = { x: rect.left + marker.offsetLeft - area.scrollLeft, y: rect.top + marker.offsetTop - area.scrollTop + lineHeight };
    mirror.remove();
    return point;
  }

  function clearZones() {
    for (const popup of document.querySelectorAll('.vg-rv-popup')) popup.remove();
    for (const entry of entries) {
      entry.observer.disconnect();
      entry.editor.changeViewZones(accessor => accessor.removeZone(entry.id));
      entry.decorations?.clear();
    }
    entries = [];
  }

  /** Width and offset of the cards: the visible text area, pinned against the horizontal scroll (as the hunk headers). */
  function placeCard(editor, node) {
    const layout = editor.getLayoutInfo();
    node.style.setProperty('--vg-rv-width', `${Math.max(240, layout.contentWidth - layout.verticalScrollbarWidth - 24)}px`);
    node.style.setProperty('--vg-rv-left', `${editor.getScrollLeft()}px`);
  }

  function addZone(editor, afterLine, card, range, threadId) {
    const holder = el('div', 'vg-rv-zone');
    holder.appendChild(card);
    for (const type of ['mousedown', 'mouseup', 'click', 'dblclick', 'contextmenu', 'keydown', 'keyup', 'keypress', 'pointerdown']) {
      holder.addEventListener(type, e => e.stopPropagation());
    }
    // Monaco takes the focus on any press inside its view (before this handler runs): once the click is over, the box
    // that was being typed in gets it back, unless the click was in a text field or on selectable text.
    holder.addEventListener('pointerup', e => {
      const area = lastArea;
      if (!area || !(e.target instanceof Element) || e.target.closest('.vg-rv-md, .vg-rv-input')) return;
      setTimeout(() => { if (area.isConnected && document.activeElement !== area) area.focus(); }, 0);
    });
    placeCard(editor, holder);
    const zone = { afterLineNumber: Math.max(0, afterLine), heightInPx: 96, domNode: holder, suppressMouseDown: true };
    let id;
    editor.changeViewZones(accessor => { id = accessor.addZone(zone); });
    const observer = new ResizeObserver(() => {
      const height = Math.ceil(card.getBoundingClientRect().height) + ZONE_GAP;
      if (height > ZONE_GAP && height !== zone.heightInPx) {
        zone.heightInPx = height;
        editor.changeViewZones(accessor => accessor.layoutZone(id));
      }
    });
    observer.observe(card);
    const entry = { editor, id, zone, observer, holder, card, decorations: null, threadId };
    if (range) {
      entry.decorations = editor.createDecorationsCollection([{
        range,
        options: { className: 'vg-rv-range', isWholeLine: range.startColumn <= 1 && range.endColumn > 1000 },
      }]);
    }
    entries.push(entry);
  }

  /** Range decoration of an anchor: whole lines when the service gives no columns. */
  function rangeOf(surface, item) {
    const start = surface.modelLine(item.side, item.startLine ?? item.line);
    const end = surface.modelLine(item.side, item.line);
    if (!start || !end) return null;
    const columns = item.startColumn && item.endColumn;
    return columns
      ? new monaco.Range(start, item.startColumn, end, item.endColumn)
      : new monaco.Range(start, 1, end, 1_000_000);
  }

  function surfaceFor(all, side) {
    return all.find(s => s.side === side) ?? all.find(s => s.side === 'both') ?? null;
  }

  function render(focusThread) {
    // Typing continues across a re-render: remember the focused box and its caret.
    const active = document.activeElement;
    const focusKey = active instanceof HTMLTextAreaElement ? active.dataset.focusKey : null;
    const caret = focusKey ? [active.selectionStart, active.selectionEnd] : null;

    clearZones();
    const all = state.enabled ? surfaces() : [];
    updateAdornments(state.canComment ? all : []);
    if (all.length === 0) return;

    const place = (item, card, threadId) => {
      const surface = surfaceFor(all, item.side);
      if (!surface) return;
      const model = surface.editor.getModel();
      const line = surface.modelLine(item.side, item.line) ?? model?.getLineCount() ?? 0;
      addZone(surface.editor, line, card, rangeOf(surface, item), threadId);
    };

    // A thread about a line the diff does not show (unified layout) goes after the last line, with a note.
    const outside = item => surfaceFor(all, item.side)?.modelLine(item.side, item.line) === null;
    // The code a suggestion would replace: the text of the commented range (new version).
    const originalOf = item => {
      const surface = surfaceFor(all, item.side);
      const range = surface && item.side === 'new' ? rangeOf(surface, item) : null;
      return range ? surface.editor.getModel()?.getValueInRange(range) : undefined;
    };
    for (const thread of state.threads) place(thread, threadCard(thread, outside(thread), originalOf(thread)), thread.id);
    for (const draft of state.drafts) place(draft, draftCard(draft, originalOf(draft)));
    if (state.composer) {
      const surface = surfaceFor(all, state.composer.side);
      if (surface) place(state.composer, composerCard());
    }

    revealPending();

    const wanted = focusKey ?? (focusThread ? `reply:${focusThread}` : state.composer && !state.composer.busyKey && !state.composer.focused ? 'composer' : null);
    if (wanted) {
      const area = document.querySelector(`textarea[data-focus-key="${CSS.escape(wanted)}"]`);
      if (area) {
        const focus = () => {
          area.focus();
          if (caret) area.setSelectionRange(caret[0], caret[1]);
          else area.setSelectionRange(area.value.length, area.value.length);
        };
        focus();
        // The editor may take the focus back once the click that opened the box has finished.
        setTimeout(() => { if (area.isConnected && document.activeElement !== area) focus(); }, 50);
        if (wanted === 'composer' && state.composer) state.composer.focused = true;
      }
    }
  }

  /** Scrolls to the thread the app asked for, as soon as its card is drawn, and flashes it (once per request). */
  function revealPending() {
    if (!pendingReveal) return;
    const entry = entries.find(e => e.threadId === pendingReveal);
    if (!entry) return;
    pendingReveal = null;
    const show = () => {
      const line = Math.max(1, entry.zone.afterLineNumber);
      entry.editor.revealLineInCenter(line);
      entry.card.classList.remove('vg-rv-flash');
      void entry.card.offsetWidth;
      entry.card.classList.add('vg-rv-flash');
    };
    show();
    // The zones are measured after they are added: the position is corrected once their heights are known.
    setTimeout(() => { if (entry.card.isConnected) entry.editor.revealLineInCenter(Math.max(1, entry.zone.afterLineNumber)); }, 150);
  }

  // ---------------------------------------------------------------- starting a comment

  function openComposer(surface, range) {
    const model = surface.editor.getModel();
    let suggestion;
    if (range.side === 'new' && model) {
      const first = surface.modelLine('new', range.startLine) ?? 0;
      const last = surface.modelLine('new', range.line) ?? first;
      if (first && last) {
        suggestion = model.getValueInRange(new monaco.Range(first, range.startColumn || 1, last, range.endColumn || model.getLineMaxColumn(last)));
      }
    }
    state.composer = { ...range, text: '', suggestion, preview: true, busyKey: null, focused: false };
    render();
  }

  function updateAdornments(all) {
    for (const adornment of adornments) adornment.dispose();
    adornments = [];
    for (const surface of all) {
      const editor = surface.editor;
      editor.updateOptions({ glyphMargin: true });
      adornments.push(selectionButton(surface), hoverPlus(surface));
    }
  }

  function selectionButton(surface) {
    const editor = surface.editor;
    const node = el('button', 'vg-rv-selbutton');
    node.type = 'button';
    node.innerHTML = `${icon('comment')}<span></span>`;
    node.querySelector('span').textContent = state.labels.commentOnSelection ?? '';
    node.addEventListener('mousedown', e => e.preventDefault());
    let current = null;
    node.addEventListener('click', () => {
      if (current) openComposer(surface, current);
    });
    let visible = false;
    const holder = el('div', 'vg-rv-selholder');
    holder.appendChild(node);
    const widget = {
      getId: () => `vg.review.selection.${surface.side}`,
      getDomNode: () => holder,
      getPosition: () => {
        const sel = editor.getSelection();
        return sel && !sel.isEmpty() ? {
          position: { lineNumber: sel.endLineNumber, column: sel.endColumn },
          preference: [monaco.editor.ContentWidgetPositionPreference.BELOW, monaco.editor.ContentWidgetPositionPreference.ABOVE],
        } : null;
      },
    };
    const update = () => {
      const sel = editor.getSelection();
      current = sel && !sel.isEmpty() ? surface.rangeOfSelection(sel) : null;
      if (current && !visible) { editor.addContentWidget(widget); visible = true; }
      else if (!current && visible) { editor.removeContentWidget(widget); visible = false; }
      else if (visible) editor.layoutContentWidget(widget);
    };
    const subscription = editor.onDidChangeCursorSelection(update);
    return { dispose() { subscription.dispose(); if (visible) editor.removeContentWidget(widget); } };
  }

  function hoverPlus(surface) {
    const editor = surface.editor;
    const decorations = editor.createDecorationsCollection();
    let line = 0;
    const set = next => {
      if (next === line) return;
      line = next;
      decorations.set(next ? [{
        range: new monaco.Range(next, 1, next, 1),
        options: { glyphMarginClassName: 'vg-rv-plus', glyphMarginHoverMessage: { value: state.labels.commentOnLine ?? '' } },
      }] : []);
    };
    const move = editor.onMouseMove(e => {
      const type = e.target.type;
      const gutter = type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || type === monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS
        || type === monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS;
      set(gutter && e.target.position && surface.rangeOfLine(e.target.position.lineNumber) ? e.target.position.lineNumber : 0);
    });
    const leave = editor.onMouseLeave(() => set(0));
    const down = editor.onMouseDown(e => {
      if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !e.target.position) return;
      const range = surface.rangeOfLine(e.target.position.lineNumber);
      if (range) openComposer(surface, range);
    });
    return { dispose() { move.dispose(); leave.dispose(); down.dispose(); decorations.clear(); } };
  }

  // Cards follow the horizontal scroll and the width of the editor.
  function relayout() {
    for (const entry of entries) placeCard(entry.editor, entry.holder);
  }

  const listeners = new Map();
  function watch(all) {
    for (const surface of all) {
      if (listeners.has(surface.editor)) continue;
      listeners.set(surface.editor, [
        surface.editor.onDidScrollChange(relayout),
        surface.editor.onDidLayoutChange(relayout),
      ]);
    }
  }

  // ---------------------------------------------------------------- app messages

  return {
    /** New review state from the app (threads of the file, comments kept for the review, labels). */
    setState(message) {
      if (message.file !== state.file) {
        state.composer = null;
        state.replies.clear();
        state.expanded.clear();
        state.file = message.file;
      }
      state.enabled = message.enabled;
      state.canComment = message.canComment;
      state.canDraft = message.canDraft;
      state.labels = message.labels ?? state.labels;
      state.links = message.links ?? {};
      state.mentions = { ...state.mentions, ...(message.mentions ?? {}) };
      state.canComplete = !!message.canComplete;
      state.threads = message.threads ?? [];
      state.drafts = message.drafts ?? [];
      if (!message.enabled || !message.canComment) state.composer = null;
      if (message.reveal && message.reveal.token !== lastRevealToken) {
        lastRevealToken = message.reveal.token;
        pendingReveal = message.reveal.thread;
      }
      // A resolved thread is folded: it opens to be read. The request waits until the page shows the file that has the thread.
      if (pendingReveal && state.threads.some(t => t.id === pendingReveal)) state.expanded.add(pendingReveal);
      watch(surfaces());
      render();
    },
    /** The editors changed (another file, another layout, whole versions arrived). */
    refresh() {
      watch(surfaces());
      render();
    },
    /** The proposals the app found for a completion request. */
    references(message) {
      const done = pendingReferences.get(message.key);
      pendingReferences.delete(message.key);
      done?.(message.items ?? []);
    },
    /** The app finished the write a box was waiting for. */
    result(message) {
      const done = pendingKeys.get(message.key);
      pendingKeys.delete(message.key);
      done?.(message.ok);
    },
    /** A new file is shown: its box is gone, the threads follow with the next state. */
    reset() {
      state.composer = null;
      state.threads = [];
      state.drafts = [];
      clearZones();
      updateAdornments([]);
    },
  };
}
