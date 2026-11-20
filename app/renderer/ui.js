'use strict';
// Kleine UI-Helfer: Elemente bauen, Toasts, Modals, Busy-Anzeige, IPC-Aufrufe

const UI = (() => {
  /** h('div.class#id', {attrs}, ...kinder) */
  function h(tag, attrs, ...children) {
    const m = tag.match(/^([a-z0-9]+)?((?:[.#][\w-]+)*)$/i);
    const el = document.createElement(m[1] || 'div');
    (m[2].match(/[.#][\w-]+/g) || []).forEach((p) => {
      if (p[0] === '.') el.classList.add(p.slice(1)); else el.id = p.slice(1);
    });
    if (attrs && (typeof attrs !== 'object' || attrs instanceof Node || Array.isArray(attrs))) {
      children.unshift(attrs);
      attrs = null;
    }
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v === null || v === undefined || v === false) continue;
      if (k.startsWith('on')) el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (k === 'html') el.innerHTML = v;
      else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
      else el.setAttribute(k, v === true ? '' : v);
    }
    for (const c of children.flat(Infinity)) {
      if (c === null || c === undefined || c === false) continue;
      el.append(c instanceof Node ? c : document.createTextNode(String(c)));
    }
    return el;
  }

  function toast(text, type = 'ok', ms = 4500) {
    const t = h('div.toast', text);
    if (type !== 'ok') t.classList.add(type);
    document.getElementById('toasts').append(t);
    setTimeout(() => t.remove(), type === 'error' ? Math.max(ms, 8000) : ms);
  }

  let busyCount = 0;
  function busy(text) {
    busyCount++;
    document.getElementById('busy-text').textContent = text || 'Bitte warten …';
    document.getElementById('busy-bar').style.width = '0%';
    document.getElementById('busy').classList.remove('hidden');
  }
  function done() {
    busyCount = Math.max(0, busyCount - 1);
    if (!busyCount) document.getElementById('busy').classList.add('hidden');
  }
  function progress(data) {
    if (!busyCount) return;
    if (data.text) document.getElementById('busy-text').textContent = data.text;
    if (data.percent >= 0) document.getElementById('busy-bar').style.width = `${data.percent}%`;
  }

  /** Ruft den Hauptprozess auf. Fehler werden als Toast gezeigt und als null zurückgegeben. */
  async function call(channel, args, { busyText, quiet } = {}) {
    if (busyText) busy(busyText);
    try {
      const r = await window.api.invoke(channel, args);
      if (!r.ok) {
        if (!quiet) toast(r.error, 'error');
        return null;
      }
      return r.data;
    } finally {
      if (busyText) done();
    }
  }

  function modal({ title, body, buttons = [], wide = false, onClose }) {
    const root = document.getElementById('modal-root');
    const close = () => { back.remove(); document.removeEventListener('keydown', onKey); onClose && onClose(); };
    const onKey = (e) => { if (e.key === 'Escape') close(); };
    const foot = buttons.length
      ? h('div.modal-foot', buttons.map((b) => h(`button.btn${b.primary ? '.primary' : ''}${b.danger ? '.danger' : ''}`, {
        onclick: async () => { const keep = b.onClick ? await b.onClick(close) : false; if (keep !== true) close(); },
      }, b.label)))
      : null;
    const box = h(`div.modal${wide ? '.wide' : ''}`,
      h('div.modal-head', h('h2', title), h('button.btn.ghost.small', { onclick: close, title: 'Schließen' }, '✕')),
      h('div.modal-body', body),
      foot);
    const back = h('div.modal-back', { onmousedown: (e) => { if (e.target === back) close(); } }, box);
    root.append(back);
    document.addEventListener('keydown', onKey);
    return { close, box };
  }

  function confirm(title, text, okLabel = 'OK', { danger = false } = {}) {
    return new Promise((resolve) => {
      let answered = false;
      modal({
        title,
        body: h('p', text),
        buttons: [
          { label: 'Abbrechen', onClick: () => { answered = true; resolve(false); } },
          { label: okLabel, primary: !danger, danger, onClick: () => { answered = true; resolve(true); } },
        ],
        onClose: () => { if (!answered) resolve(false); },
      });
    });
  }

  const kb = (bytes) => {
    if (!bytes) return '–';
    if (bytes > 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
    return `${Math.round(bytes / 1024)} KB`;
  };
  const base = (p) => String(p || '').split(/[\\/]/).pop();

  return { h, toast, busy, done, progress, call, modal, confirm, kb, base };
})();
