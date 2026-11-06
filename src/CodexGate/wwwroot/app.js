'use strict';

// CodexGate panel. No framework and no build step: the DOM is built with h(), which only ever
// creates text nodes for data, so nothing a user typed is parsed as HTML.
(() => {
  const root = document.getElementById('app');
  const toasts = document.getElementById('toasts');
  const DICT = window.CODEXGATE_I18N;
  const state = { me: null, renderId: 0 };

  // ---------- language ----------
  const LANGS = {
    fa: { locale: 'fa-IR', dates: 'fa-IR-u-ca-persian', dir: 'rtl' },
    en: { locale: 'en-US', dates: 'en-GB', dir: 'ltr' },
  };
  let lang = pickLanguage();
  let F = null;

  function pickLanguage() {
    try {
      const saved = localStorage.getItem('codexgate.lang');
      if (saved && LANGS[saved]) return saved;
    } catch (e) { /* storage blocked: fall back to the browser language */ }
    return (navigator.language || '').toLowerCase().startsWith('fa') ? 'fa' : 'en';
  }

  function applyLanguage() {
    const L = LANGS[lang];
    document.documentElement.lang = lang;
    document.documentElement.dir = L.dir;
    F = {
      int: new Intl.NumberFormat(L.locale),
      dec2: new Intl.NumberFormat(L.locale, { maximumFractionDigits: 2 }),
      dec4: new Intl.NumberFormat(L.locale, { maximumFractionDigits: 4 }),
      usd2: new Intl.NumberFormat(L.locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
      compact: new Intl.NumberFormat(L.locale, { notation: 'compact', maximumFractionDigits: 1 }),
      pct: new Intl.NumberFormat(L.locale, { style: 'percent', maximumFractionDigits: 1 }),
      dateTime: new Intl.DateTimeFormat(L.dates, { year: 'numeric', month: 'long', day: 'numeric', hour: '2-digit', minute: '2-digit' }),
      date: new Intl.DateTimeFormat(L.dates, { year: 'numeric', month: 'long', day: 'numeric' }),
      day: new Intl.DateTimeFormat(L.dates, { month: 'short', day: 'numeric', timeZone: 'UTC' }),
      rel: new Intl.RelativeTimeFormat(lang, { numeric: 'auto' }),
    };
  }

  function setLanguage(next) {
    lang = next;
    try { localStorage.setItem('codexgate.lang', next); } catch (e) { /* not remembered, still switched */ }
    applyLanguage();
    render();
  }

  // Picks "<key>One" for exactly one (English needs it; Persian repeats the same text).
  function tn(key, n, shown) {
    const one = n === 1 && DICT.en[key + 'One'] ? key + 'One' : key;
    return t(one, { n: shown === undefined ? n : shown });
  }

  function t(key, vars) {
    let s = (DICT[lang] && DICT[lang][key]) || DICT.en[key] || key;
    if (vars) {
      for (const [k, v] of Object.entries(vars)) s = s.split('{' + k + '}').join(String(v));
    }
    return s;
  }

  // ---------- DOM ----------
  function h(tag, attrs, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v === null || v === undefined || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'width') el.style.width = v;
      else if (k === 'height') el.style.height = v;
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
      else if (typeof v === 'boolean' || k === 'value') el[k] = v;
      else el.setAttribute(k, String(v));
    }
    for (const kid of kids.flat(Infinity)) {
      if (kid === null || kid === undefined || kid === false) continue;
      el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
    }
    return el;
  }

  function mount(target, ...kids) {
    target.replaceChildren(...kids.flat(Infinity).filter(Boolean));
  }

  // ---------- formatting ----------
  const fmtInt = (v) => F.int.format(Math.round(v));
  const fmtTokens = (v) => (Math.abs(v) >= 10000 ? F.compact.format(v) : fmtInt(v));
  const fmtUsd = (v) => t('usd', { n: (v > 0 && v < 1 ? F.dec4 : F.usd2).format(v) });
  const fmtAmount = (kind, v) => (kind === 'usd' ? fmtUsd(v) : tn('tokens', v, fmtTokens(v)));
  const fmtPct = (p) => F.pct.format((p || 0) / 100);
  const fmtDate = (ms) => (ms ? F.dateTime.format(new Date(ms)) : t('none'));
  const fmtDay = (ms) => (ms ? F.date.format(new Date(ms)) : t('none'));
  const fmtBucket = (day) => F.day.format(new Date(day * 86400000));

  function ago(ms) {
    if (!ms) return t('none');
    const sec = Math.round((ms - Date.now()) / 1000);
    const abs = Math.abs(sec);
    if (abs < 60) return F.rel.format(sec, 'second');
    if (abs < 3600) return F.rel.format(Math.round(sec / 60), 'minute');
    if (abs < 86400) return F.rel.format(Math.round(sec / 3600), 'hour');
    return F.rel.format(Math.round(sec / 86400), 'day');
  }

  const windowLabel = (hours) => (hours % 24 === 0 ? tn('days', hours / 24, fmtInt(hours / 24)) : tn('hours', hours, fmtInt(hours)));
  const daysLeft = (ms) => (ms ? Math.ceil((ms - Date.now()) / 86400000) : null);
  const tzMinutes = () => -new Date().getTimezoneOffset();

  // ---------- errors and toasts ----------
  class ApiError extends Error {
    constructor(code, status, detail) {
      super(code);
      this.code = code;
      this.status = status;
      this.detail = detail;
    }
  }

  function errorText(err) {
    if (!(err instanceof ApiError)) return t('err_network');
    const key = 'err_' + err.code;
    const base = DICT.en[key] ? t(key) : t('err_unknown', { code: err.code });
    return err.detail ? base + ' (' + err.detail + ')' : base;
  }

  function toast(text, bad) {
    const el = h('div', { class: 'toast' + (bad ? ' bad' : ''), role: bad ? 'alert' : 'status' }, text);
    toasts.append(el);
    setTimeout(() => el.remove(), bad ? 6000 : 3000);
  }

  // ---------- API ----------
  async function api(method, url, body) {
    let res;
    try {
      res = await fetch(url, {
        method,
        credentials: 'same-origin',
        headers: Object.assign({ 'X-CodexGate': '1' }, body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        body: body !== undefined ? JSON.stringify(body) : undefined,
      });
    } catch (e) {
      throw new ApiError('network', 0);
    }
    if (res.status === 401 && url !== '/api/auth/login') {
      state.me = null;
      closeModal();
      render();
      throw new ApiError('invalid_credentials', 401);
    }
    const text = await res.text();
    let data = null;
    if (text) {
      try { data = JSON.parse(text); } catch (e) { data = null; }
    }
    if (!res.ok) throw new ApiError((data && data.error) || 'http_' + res.status, res.status, data && data.detail);
    return data;
  }

  async function guarded(button, work) {
    if (button) button.disabled = true;
    try {
      return await work();
    } catch (err) {
      toast(errorText(err), true);
      return undefined;
    } finally {
      if (button) button.disabled = false;
    }
  }

  // ---------- clipboard ----------
  async function copy(text) {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
      } else {
        const area = h('textarea', { readonly: 'readonly' });
        area.value = text;
        area.style.position = 'fixed';
        area.style.opacity = '0';
        document.body.append(area);
        area.select();
        const ok = document.execCommand('copy');
        area.remove();
        if (!ok) throw new Error('copy refused');
      }
      toast(t('copied'));
    } catch (e) {
      toast(t('copyFailed'), true);
    }
  }

  // Commands and config are left-to-right code; a message to forward is plain text in the page's direction.
  function codeBox(text, plain) {
    return h('div', { class: 'code-box' },
      h('button', { class: 'small', type: 'button', onclick: () => copy(text) }, t('copy')),
      plain ? h('pre', { class: 'plain', dir: 'auto' }, text) : h('pre', { class: 'code', dir: 'ltr' }, text));
  }

  // ---------- modal (a plain overlay, not <dialog>, so closing is always just removing it) ----------
  let overlay = null;
  function openModal(title, body, wide) {
    closeModal();
    overlay = h('div', { class: 'overlay', onclick: (e) => { if (e.target === overlay) closeModal(); } },
      h('div', { class: 'card modal' + (wide ? ' wide' : ''), role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
        h('div', { class: 'modal-head' },
          h('h2', null, title),
          h('button', { class: 'ghost', type: 'button', 'aria-label': t('close'), onclick: closeModal }, '✕')),
        body));
    document.body.append(overlay);
    const first = overlay.querySelector('input:not([disabled]), select, textarea, button.primary');
    if (first) first.focus();
  }
  function closeModal() {
    if (overlay) {
      overlay.remove();
      overlay = null;
    }
  }
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeModal(); });

  // ---------- form helpers ----------
  function field(label, control, help) {
    return h('label', { class: 'field' }, h('span', null, label), control, help ? h('small', null, help) : null);
  }
  function input(attrs) {
    return h('input', Object.assign({ type: 'text' }, attrs));
  }
  function select(options, value, attrs) {
    return h('select', attrs || null,
      options.map(([v, text]) => h('option', { value: String(v), selected: String(v) === String(value) }, text)));
  }
  function meter(percent, mini) {
    const p = Math.max(0, Math.min(100, percent || 0));
    const cls = 'meter' + (mini ? ' mini' : '') + (p >= 100 ? ' bad' : p >= 80 ? ' warn' : '');
    return h('div', { class: cls, title: fmtPct(p), role: 'progressbar', 'aria-valuenow': String(Math.round(p)), 'aria-valuemin': '0', 'aria-valuemax': '100' },
      h('i', { width: p + '%' }));
  }
  const ACCESS = {
    ok: ['accessOk', 'ok'],
    account_disabled: ['accessDisabled', 'bad'],
    subscription_expired: ['accessExpired', 'warn'],
    no_plan: ['accessNoPlan', 'warn'],
  };
  function accessBadge(code) {
    const [key, cls] = ACCESS[code] || [code, ''];
    return h('span', { class: 'badge ' + cls }, t(key));
  }
  const ltr = (text, cls) => h('span', { class: 'ltr' + (cls ? ' ' + cls : ''), dir: 'ltr' }, text);
  const table = (headers, rows, emptyText) => h('div', { class: 'table-wrap' }, h('table', null,
    h('thead', null, h('tr', null, headers.map((x) => h('th', null, x)))),
    h('tbody', null, rows.length ? rows : h('tr', null, h('td', { class: 'empty', colspan: String(headers.length) }, emptyText)))));

  // ---------- Codex setup text ----------
  function codexConfig(g) {
    return [
      'model = "' + (g.defaultModel || 'MODEL_NAME') + '"',
      'model_provider = "' + g.providerId + '"',
      '',
      '[model_providers.' + g.providerId + ']',
      'name = "CodexGate"',
      'base_url = "' + g.baseUrl + '"',
      'env_key = "' + g.envKey + '"',
      'wire_api = "responses"',
      'supports_websockets = false',
      'stream_idle_timeout_ms = 600000',
      '',
    ].join('\n');
  }

  function windowsSetup(g, key) {
    return [
      '$dir = Join-Path $HOME ".codex"',
      'New-Item -ItemType Directory -Force $dir | Out-Null',
      '$file = Join-Path $dir "config.toml"',
      'if (Test-Path $file) { Copy-Item $file "$file.bak" -Force }',
      "$config = @'",
      codexConfig(g).trimEnd(),
      "'@",
      '[IO.File]::WriteAllText($file, $config + "`n")',
      // Codex reads CODEX_HOME/.env itself, so the CLI, the app and the IDE extension all get the key.
      '$envFile = Join-Path $dir ".env"',
      '$lines = @()',
      "if (Test-Path $envFile) { $lines = @(Get-Content $envFile | Where-Object { $_ -notmatch '^" + g.envKey + "=' }) }",
      "$lines += '" + envLine(g, key) + "'",
      '[IO.File]::WriteAllLines($envFile, [string[]]$lines)',
      'Write-Host "Done. Run: codex"',
    ].join('\n');
  }

  function unixSetup(g, key) {
    return [
      'umask 077',
      'mkdir -p ~/.codex',
      '[ -f ~/.codex/config.toml ] && cp ~/.codex/config.toml ~/.codex/config.toml.bak',
      "cat > ~/.codex/config.toml <<'EOF'",
      codexConfig(g).trimEnd(),
      'EOF',
      'touch ~/.codex/.env && chmod 600 ~/.codex/.env',
      "grep -v '^" + g.envKey + "=' ~/.codex/.env > ~/.codex/.env.new",
      "echo '" + envLine(g, key) + "' >> ~/.codex/.env.new",
      'mv ~/.codex/.env.new ~/.codex/.env && chmod 600 ~/.codex/.env',
      'echo "Done. Run: codex"',
    ].join('\n');
  }

  function envLine(g, key) {
    return g.envKey + '=' + key;
  }

  function setupTabs(g, key) {
    const panes = {
      win: [t('tabWindows'), windowsSetup(g, key)],
      unix: [t('tabUnix'), unixSetup(g, key)],
      manual: [t('tabManual'), codexConfig(g) + '\n# ~/.codex/.env\n' + envLine(g, key) + '\n'],
    };
    const body = h('div');
    const tabs = h('div', { class: 'tabs', role: 'tablist' });
    const show = (id) => {
      for (const b of tabs.children) {
        const on = b.dataset.id === id;
        b.classList.toggle('active', on);
        b.setAttribute('aria-selected', String(on));
      }
      const extra = id === 'manual'
        ? h('p', { class: 'small muted' }, t('manualHelp1'), h('code', { class: 'inline' }, '~/.codex/config.toml'),
          t('manualHelp2'), h('code', { class: 'inline' }, '~/.codex/.env'), t('manualHelp3'))
        : h('p', { class: 'small muted' }, t('backupHelp'), h('code', { class: 'inline' }, '~/.codex/.env'), t('backupHelp2'));
      mount(body, codeBox(panes[id][1]), extra);
    };
    for (const [id, [label]] of Object.entries(panes)) {
      const b = h('button', { type: 'button', role: 'tab', onclick: () => show(id) }, label);
      b.dataset.id = id;
      tabs.append(b);
    }
    show(navigator.userAgent.includes('Windows') ? 'win' : 'unix');
    return h('div', null, tabs, body);
  }

  // ---------- routing ----------
  const PAGES = {
    me: { title: 'navMe', render: pageMe },
    users: { title: 'navUsers', render: pageUsers, admin: true },
    plans: { title: 'navPlans', render: pagePlans, admin: true },
    report: { title: 'navReport', render: pageReport, admin: true },
    settings: { title: 'navSettings', render: pageSettings, admin: true },
  };

  function currentRoute() {
    const id = (location.hash || '').replace(/^#\/?/, '') || 'me';
    const page = PAGES[id];
    if (!page || (page.admin && state.me.role !== 'admin')) return 'me';
    return id;
  }

  window.addEventListener('hashchange', () => { if (state.me) render(); });

  async function boot() {
    applyLanguage();
    try {
      state.me = await api('GET', '/api/me/');
    } catch (e) {
      state.me = null;
    }
    render();
  }

  const langButton = () => h('button', {
    class: 'small ghost lang', type: 'button', lang: lang === 'fa' ? 'en' : 'fa',
    onclick: () => setLanguage(lang === 'fa' ? 'en' : 'fa'),
  }, t('otherLanguage'));

  function render() {
    closeModal();
    if (!state.me) {
      document.title = 'CodexGate';
      mount(root, loginView());
      return;
    }
    const route = currentRoute();
    const nav = h('nav', { class: 'nav' },
      Object.entries(PAGES)
        .filter(([, p]) => !p.admin || state.me.role === 'admin')
        .map(([id, p]) => h('a', { href: '#/' + id, class: id === route ? 'active' : null, 'aria-current': id === route ? 'page' : null }, t(p.title))));
    const main = h('main');
    mount(root,
      h('header', { class: 'topbar' },
        h('div', { class: 'brand' }, h('img', { src: 'favicon.svg', alt: '' }), 'CodexGate'),
        nav,
        h('div', { class: 'who' },
          h('span', { class: 'muted small' }, state.me.displayName || state.me.username),
          langButton(),
          h('button', { class: 'small', type: 'button', onclick: logout }, t('signOut')))),
      main);
    document.title = t(PAGES[route].title) + ' · CodexGate';
    const id = ++state.renderId;
    mount(main, h('p', { class: 'muted' }, t('loading')));
    PAGES[route].render(main, () => id === state.renderId).catch((err) => {
      if (id === state.renderId) mount(main, h('div', { class: 'notice bad' }, errorText(err)));
    });
  }

  async function refreshMe() {
    state.me = await api('GET', '/api/me/');
  }

  async function logout() {
    try { await api('POST', '/api/auth/logout'); } catch (e) { /* signed out anyway */ }
    state.me = null;
    location.hash = '';
    render();
  }

  // ---------- login ----------
  function loginView() {
    const user = input({ name: 'username', autocomplete: 'username', dir: 'ltr', required: true });
    const pass = input({ name: 'password', type: 'password', autocomplete: 'current-password', dir: 'ltr', required: true });
    const error = h('div', { class: 'notice bad hidden', role: 'alert' });
    const submit = h('button', { class: 'primary', type: 'submit' }, t('signIn'));
    const form = h('form', {
      onsubmit: async (e) => {
        e.preventDefault();
        error.classList.add('hidden');
        submit.disabled = true;
        try {
          state.me = await api('POST', '/api/auth/login', { username: user.value, password: pass.value });
          render();
        } catch (err) {
          error.textContent = errorText(err);
          error.classList.remove('hidden');
        } finally {
          submit.disabled = false;
        }
      },
    },
    error,
    field(t('username'), user),
    field(t('password'), pass),
    h('div', { class: 'actions end' }, submit));
    setTimeout(() => user.focus(), 0);
    return h('div', { class: 'login-wrap' },
      h('div', { class: 'card login' },
        h('div', { class: 'login-top' }, h('div', { class: 'brand' }, h('img', { src: 'favicon.svg', alt: '' }), 'CodexGate'), langButton()),
        form));
  }

  // ---------- my page ----------
  async function pageMe(main, alive) {
    await refreshMe();
    const [keys, usage] = await Promise.all([
      api('GET', '/api/me/keys'),
      api('GET', '/api/me/usage?days=30&tz=' + tzMinutes()),
    ]);
    if (!alive()) return;
    const me = state.me;

    const notices = [];
    if (me.access === 'subscription_expired') notices.push(h('div', { class: 'notice warn' }, t('noticeExpired')));
    if (me.access === 'no_plan') notices.push(h('div', { class: 'notice warn' }, t('noticeNoPlan')));
    if (me.quota.some((q) => q.exhausted)) notices.push(h('div', { class: 'notice bad' }, t('noticeExhausted')));

    const left = daysLeft(me.expiresAt);
    const status = h('div', { class: 'card' },
      h('div', { class: 'card-head' }, h('h2', null, t('subscription')), accessBadge(me.access)),
      h('p', null, t('planLabel'), h('strong', null, me.plan ? me.plan.name : t('none'))),
      h('p', null, t('validUntil'), h('strong', null, me.expiresAt ? fmtDay(me.expiresAt) : t('noEndDate')),
        left !== null && left > 0 ? h('span', { class: 'muted' }, ' (' + tn('daysLeft', left, fmtInt(left)) + ')') : null),
      me.plan ? h('p', { class: 'small muted' },
        t('modelsLabel'), me.plan.allowedModels.length ? ltr(me.plan.allowedModels.join(', ')) : t('all'),
        t('atATime'), fmtInt(me.plan.maxConcurrency)) : null);

    const quotaCards = me.quota.length
      ? me.quota.map((q) => h('div', { class: 'card quota' },
        h('div', { class: 'card-head' }, h('h3', null, t('quotaFor', { w: windowLabel(q.windowHours) })),
          q.exhausted ? h('span', { class: 'badge bad' }, t('usedUp')) : null),
        h('div', { class: 'big' }, fmtPct(q.percent)),
        meter(q.percent),
        h('p', { class: 'small muted' }, t('usedOf', { used: fmtAmount(q.kind, q.used), amount: fmtAmount(q.kind, q.amount) })),
        q.freesAt ? h('p', { class: 'small muted' }, t('freesAt', { t: fmtDate(q.freesAt) })) : null))
      : me.plan ? [h('div', { class: 'card' }, h('p', { class: 'muted' }, t('noLimitsOnPlan')))] : [];

    mount(main, 
      h('div', { class: 'page-head' }, h('h1', null, t('hello', { name: me.displayName || me.username }))),
      ...notices,
      h('div', { class: 'grid two' }, status, connectCard(me.gateway, keys)),
      h('h2', null, t('usage')),
      h('div', { class: 'grid three' }, quotaCards),
      usageCard(t('last30'), usage.daily),
      recentCard(usage.recent, false),
      passwordCard());
  }

  function connectCard(g, keys) {
    const active = keys.filter((k) => !k.revokedAt);
    const list = active.length
      ? table([t('colKey'), t('colName'), t('colLastUsed'), ''], active.map((k) => h('tr', null,
        h('td', null, ltr(k.prefix + '…')),
        h('td', null, k.name || t('none')),
        h('td', { class: 'small' }, ago(k.lastUsedAt)),
        h('td', null, h('button', { class: 'small danger', type: 'button', onclick: (e) => revokeKey(e.currentTarget, k) }, t('revoke'))))), '')
      : h('p', { class: 'muted' }, t('noKeys'));

    return h('div', { class: 'card' },
      h('div', { class: 'card-head' }, h('h2', null, t('connectCodex')),
        h('button', { class: 'primary small', type: 'button', onclick: () => newKeyDialog(g) }, t('newKey'))),
      h('ol', { class: 'steps' },
        h('li', null, t('step1')),
        h('li', null, t('step2')),
        h('li', null, t('step3a'), h('code', { class: 'inline' }, 'codex'), t('step3b'))),
      h('p', { class: 'small muted' }, t('gatewayAddress'), ltr(g.baseUrl)),
      list);
  }

  function newKeyDialog(g) {
    const name = input({ placeholder: t('keyNamePlaceholder'), maxlength: '40' });
    const create = h('button', { class: 'primary', type: 'button' }, t('createKey'));
    create.addEventListener('click', () => guarded(create, async () => {
      const res = await api('POST', '/api/me/keys', { name: name.value });
      openModal(t('keyCreated'), h('div', null,
        h('div', { class: 'notice warn' }, t('keyShownOnce')),
        h('div', { class: 'secret', dir: 'ltr' }, res.key),
        h('div', { class: 'actions' }, h('button', { type: 'button', onclick: () => copy(res.key) }, t('copyKey'))),
        h('h3', null, t('setupTitle')),
        setupTabs(g, res.key),
        h('div', { class: 'actions end' }, h('button', { class: 'primary', type: 'button', onclick: () => { closeModal(); render(); } }, t('done')))), true);
    }));
    openModal(t('newKey'), h('div', null,
      field(t('keyName'), name, t('keyNameHelp')),
      h('div', { class: 'actions end' }, create)));
  }

  async function revokeKey(button, key) {
    if (!confirm(t('revokeConfirm', { name: key.name || key.prefix }))) return;
    await guarded(button, async () => {
      await api('DELETE', '/api/me/keys/' + key.id);
      toast(t('keyRevoked'));
      render();
    });
  }

  function usageCard(title, daily) {
    const byDay = new Map(daily.map((d) => [d.day, d]));
    const today = Math.floor((Date.now() + tzMinutes() * 60000) / 86400000);
    const days = [];
    for (let d = today - 29; d <= today; d++) days.push(byDay.get(d) || { day: d, requests: 0, tokens: 0, costUsd: 0 });
    const max = Math.max(1, ...days.map((d) => d.tokens));
    const sum = (k) => days.reduce((s, d) => s + d[k], 0);
    const parts = [tn('tokens', sum('tokens'), fmtTokens(sum('tokens'))), tn('requests', sum('requests'), fmtInt(sum('requests')))];
    if (sum('costUsd') > 0) parts.push(fmtUsd(sum('costUsd')));
    return h('div', { class: 'card' },
      h('div', { class: 'card-head' }, h('h2', null, title), h('span', { class: 'muted small' }, parts.join(' · '))),
      h('div', { class: 'chart', role: 'img', 'aria-label': title },
        days.map((d) => h('div', {
          class: 'bar' + (d.tokens > 0 ? '' : ' zero'),
          height: (d.tokens > 0 ? Math.max(3, (d.tokens / max) * 100) : 2) + '%',
          title: t('chartTip', { day: fmtBucket(d.day), tokens: tn('tokens', d.tokens, fmtTokens(d.tokens)), requests: tn('requests', d.requests, fmtInt(d.requests)) }),
        }))),
      h('div', { class: 'chart-axis' }, h('span', null, fmtBucket(days[0].day)), h('span', null, fmtBucket(days[days.length - 1].day))));
  }

  function recentCard(rows, withUser) {
    const headers = [t('colTime')].concat(withUser ? [t('colUser')] : [],
      [t('colModel'), t('colInput'), t('colCached'), t('colOutput'), t('colCounted'), t('colCost'), t('colStatus')]);
    return h('div', { class: 'card' },
      h('div', { class: 'card-head' }, h('h2', null, t('recentRequests'))),
      table(headers, rows.map((r) => h('tr', null,
        h('td', { class: 'small nowrap', title: fmtDate(r.at) }, ago(r.at)),
        withUser ? h('td', null, r.username || t('none')) : null,
        h('td', null, ltr(r.model, 'small')),
        h('td', { class: 'num' }, fmtTokens(r.inputTokens)),
        h('td', { class: 'num' }, fmtTokens(r.cachedTokens)),
        h('td', { class: 'num' }, fmtTokens(r.outputTokens)),
        h('td', { class: 'num' }, fmtTokens(r.billableTokens), ' ', r.estimated ? h('span', { class: 'badge warn', title: t('estimatedTip') }, t('estimated')) : null,
          r.webSearches > 0 ? h('span', { class: 'badge brand', title: t('webSearch') }, tn('searches', r.webSearches, fmtInt(r.webSearches))) : null),
        h('td', { class: 'num' }, r.costUsd === null || r.costUsd === undefined ? t('none') : fmtUsd(r.costUsd)),
        h('td', null, r.status >= 200 && r.status < 300 ? h('span', { class: 'badge ok' }, t('statusOk')) : h('span', { class: 'badge bad' }, String(r.status))))),
      t('noRequests')));
  }

  function passwordCard() {
    const current = input({ type: 'password', autocomplete: 'current-password', dir: 'ltr' });
    const next = input({ type: 'password', autocomplete: 'new-password', dir: 'ltr' });
    const save = h('button', { class: 'primary', type: 'submit' }, t('changePassword'));
    const form = h('form', {
      onsubmit: (e) => {
        e.preventDefault();
        guarded(save, async () => {
          await api('POST', '/api/me/password', { current: current.value, next: next.value });
          current.value = '';
          next.value = '';
          toast(t('passwordChanged'));
        });
      },
    }, h('div', { class: 'row' }, field(t('currentPassword'), current), field(t('newPassword'), next, t('min8')),
      h('div', { class: 'fixed field' }, save)));
    return h('div', { class: 'card' }, h('div', { class: 'card-head' }, h('h2', null, t('panelPassword'))), form);
  }

  // ---------- admin: users ----------
  async function pageUsers(main, alive) {
    const [users, plans] = await Promise.all([api('GET', '/api/admin/users'), api('GET', '/api/admin/plans')]);
    if (!alive()) return;
    const rows = users.map((u) => {
      const top = u.quota.reduce((best, q) => (best && best.percent >= q.percent ? best : q), null);
      const left = daysLeft(u.expiresAt);
      return h('tr', null,
        h('td', null, h('strong', null, u.displayName || u.username), ' ',
          u.role === 'admin' ? h('span', { class: 'badge brand' }, t('admin')) : null,
          h('div', { class: 'small muted' }, ltr(u.username))),
        h('td', null, u.planName || t('none')),
        h('td', null, accessBadge(u.access)),
        h('td', { class: 'small' }, u.expiresAt ? fmtDay(u.expiresAt) : t('noEnd'),
          left !== null && left > 0 ? h('div', { class: 'muted' }, tn('daysLeft', left, fmtInt(left))) : null),
        h('td', null, top ? h('div', null, meter(top.percent, true), h('div', { class: 'small muted' }, windowLabel(top.windowHours) + ': ' + fmtPct(top.percent))) : t('none')),
        h('td', { class: 'small' }, ago(u.lastUsedAt), h('div', { class: 'muted' }, tn('keysCount', u.activeKeys, fmtInt(u.activeKeys)))),
        h('td', null, h('div', { class: 'actions' },
          h('button', { class: 'small', type: 'button', onclick: () => userDialog(u, plans) }, t('edit')),
          h('button', { class: 'small', type: 'button', onclick: () => renewDialog(u) }, t('renew')),
          h('button', { class: 'small', type: 'button', onclick: () => keysDialog(u) }, t('keys')))));
    });
    mount(main, 
      h('div', { class: 'page-head' }, h('h1', null, t('users')),
        h('button', { class: 'primary', type: 'button', onclick: () => userDialog(null, plans) }, t('newUser'))),
      plans.length ? null : h('div', { class: 'notice warn' }, t('noPlansHint')),
      h('div', { class: 'card' }, table(
        [t('colUser'), t('colPlan'), t('colStatus'), t('colValidity'), t('colTopUsage'), t('colLastUsed'), ''],
        rows, t('noUsers'))));
  }

  function userDialog(user, plans) {
    const isNew = !user;
    const username = input({ dir: 'ltr', value: user ? user.username : '', disabled: !isNew, autocomplete: 'off' });
    const displayName = input({ value: user ? user.displayName : '', maxlength: '64' });
    const password = input({ dir: 'ltr', autocomplete: 'new-password', value: isNew ? randomPassword() : '' });
    const plan = select([[0, t('noPlanOption')]].concat(plans.map((p) => [p.id, p.name])),
      user ? user.planId || 0 : (plans[0] ? plans[0].id : 0));
    const role = select([['user', t('roleUser')], ['admin', t('roleAdmin')]], user ? user.role : 'user');
    const expiryMode = select(
      (isNew ? [] : [['keep', t('keepExpiry')]]).concat([['days', t('daysFromToday')], ['never', t('noEndDate')]]),
      isNew ? 'days' : 'keep');
    const days = input({ type: 'number', min: '1', max: '3660', value: '30', dir: 'ltr' });
    const enabled = h('input', { type: 'checkbox', checked: user ? user.enabled : true });
    const note = h('textarea', { maxlength: '500' });
    note.value = user ? user.note : '';
    const daysField = field(t('numberOfDays'), days);
    const syncExpiry = () => daysField.classList.toggle('hidden', expiryMode.value !== 'days');
    expiryMode.addEventListener('change', syncExpiry);
    syncExpiry();

    const save = h('button', { class: 'primary', type: 'submit' }, isNew ? t('createUser') : t('save'));
    const extra = [];
    if (!isNew) {
      const reset = h('button', { type: 'button' }, t('resetUsage'));
      reset.addEventListener('click', () => {
        if (!confirm(t('resetUsageConfirm', { name: user.username }))) return;
        guarded(reset, async () => {
          await api('POST', '/api/admin/users/' + user.id + '/reset-usage');
          toast(t('usageReset'));
          closeModal();
          render();
        });
      });
      const remove = h('button', { class: 'danger', type: 'button' }, t('deleteUser'));
      remove.addEventListener('click', () => {
        if (!confirm(t('deleteUserConfirm', { name: user.username }))) return;
        guarded(remove, async () => {
          await api('DELETE', '/api/admin/users/' + user.id);
          toast(t('userDeleted'));
          closeModal();
          render();
        });
      });
      extra.push(remove, reset);
    }

    const form = h('form', {
      onsubmit: (e) => {
        e.preventDefault();
        guarded(save, async () => {
          const body = { displayName: displayName.value, planId: Number(plan.value), role: role.value, enabled: enabled.checked, note: note.value };
          if (expiryMode.value === 'days') body.expiresInDays = Number(days.value);
          if (expiryMode.value === 'never') body.noExpiry = true;
          if (password.value) body.password = password.value;
          if (isNew) {
            body.username = username.value;
            if (body.planId === 0) delete body.planId;
            await api('POST', '/api/admin/users', body);
            credentialsDialog(username.value.trim(), password.value);
          } else {
            await api('PATCH', '/api/admin/users/' + user.id, body);
            toast(t('saved'));
            closeModal();
            render();
          }
        });
      },
    },
    h('div', { class: 'row' }, field(t('username'), username, isNew ? t('usernameHelp') : null), field(t('displayName'), displayName)),
    h('div', { class: 'row' },
      field(isNew ? t('password') : t('newPasswordOptional'), password),
      h('div', { class: 'fixed field' }, h('button', { type: 'button', onclick: () => { password.value = randomPassword(); } }, t('randomPassword')))),
    h('div', { class: 'row' }, field(t('colPlan'), plan), field(t('role'), role)),
    h('div', { class: 'row' }, field(t('validity'), expiryMode), daysField),
    h('label', { class: 'check' }, enabled, t('accountEnabled')),
    field(t('note'), note),
    h('div', { class: 'actions split' }, h('div', { class: 'actions' }, extra), save));
    openModal(isNew ? t('newUserTitle') : t('editUserTitle', { name: user.username }), form);
  }

  function credentialsDialog(username, password) {
    const text = [
      t('credPanel', { url: location.origin + '/' }),
      t('credUser', { name: username }),
      t('credPassword', { password }),
      t('credNext'),
    ].join('\n');
    openModal(t('userCreated'), h('div', null,
      h('p', null, t('sendThis')),
      codeBox(text, true),
      h('div', { class: 'actions end' }, h('button', { class: 'primary', type: 'button', onclick: () => { closeModal(); render(); } }, t('done')))));
  }

  function renewDialog(user) {
    const days = input({ type: 'number', min: '1', max: '3660', value: '30', dir: 'ltr' });
    const go = (button, n) => guarded(button, async () => {
      const res = await api('POST', '/api/admin/users/' + user.id + '/renew', { days: n });
      toast(t('renewedUntil', { date: fmtDay(res.expiresAt) }));
      closeModal();
      render();
    });
    const quick = [30, 90, 365].map((n) => h('button', { type: 'button', onclick: (e) => go(e.currentTarget, n) }, t('plusDays', { n: fmtInt(n) })));
    const custom = h('button', { class: 'primary', type: 'button' }, t('renew'));
    custom.addEventListener('click', () => go(custom, Number(days.value)));
    openModal(t('renewTitle', { name: user.username }), h('div', null,
      h('p', { class: 'muted' }, t('currentValidity', { date: user.expiresAt ? fmtDay(user.expiresAt) : t('noEnd') })),
      h('div', { class: 'actions' }, quick),
      h('div', { class: 'row' }, field(t('customDays'), days), h('div', { class: 'fixed field' }, custom))));
  }

  async function keysDialog(user) {
    const keys = await guarded(null, () => api('GET', '/api/admin/users/' + user.id + '/keys'));
    if (!keys) return;
    openModal(t('keysOf', { name: user.username }), table(
      [t('colKey'), t('colName'), t('colCreated'), t('colLastUsed'), ''],
      keys.map((k) => h('tr', null,
        h('td', null, ltr(k.prefix + '…')),
        h('td', null, k.name || t('none')),
        h('td', { class: 'small' }, fmtDay(k.createdAt)),
        h('td', { class: 'small' }, ago(k.lastUsedAt)),
        h('td', null, k.revokedAt ? h('span', { class: 'badge' }, t('revoked')) : h('button', {
          class: 'small danger', type: 'button',
          onclick: (e) => guarded(e.currentTarget, async () => {
            await api('DELETE', '/api/admin/keys/' + k.id);
            toast(t('keyRevoked'));
            keysDialog(user);
          }),
        }, t('revoke'))))),
      t('userHasNoKeys')));
  }

  function randomPassword() {
    const alphabet = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    const out = [];
    const bytes = new Uint8Array(1);
    while (out.length < 14) {
      crypto.getRandomValues(bytes);
      if (bytes[0] < 216) out.push(alphabet[bytes[0] % alphabet.length]); // 216 = 4 × 54: no modulo bias
    }
    return out.join('');
  }

  // ---------- admin: plans ----------
  async function pagePlans(main, alive) {
    const plans = await api('GET', '/api/admin/plans');
    if (!alive()) return;
    const cards = plans.map((p) => h('div', { class: 'card' },
      h('div', { class: 'card-head' }, h('h2', null, p.name), h('span', { class: 'badge' }, tn('usersCount', p.users, fmtInt(p.users)))),
      p.limits.length
        ? h('ul', { class: 'limits' }, p.limits.map((l) => h('li', null, t('perWindow', { w: windowLabel(l.windowHours), amount: fmtAmount(l.kind, l.amount) }))))
        : h('p', { class: 'muted' }, t('noLimit')),
      h('p', { class: 'small muted' }, t('modelsLabel'), p.allowedModels.length ? ltr(p.allowedModels.join(', ')) : t('all'),
        t('atATime'), fmtInt(p.maxConcurrency)),
      h('div', { class: 'actions' },
        h('button', { class: 'small', type: 'button', onclick: () => planDialog(p) }, t('edit')),
        h('button', {
          class: 'small danger', type: 'button', disabled: p.users > 0, title: p.users > 0 ? t('planInUse') : null,
          onclick: (e) => {
            if (!confirm(t('deletePlanConfirm', { name: p.name }))) return;
            guarded(e.currentTarget, async () => {
              await api('DELETE', '/api/admin/plans/' + p.id);
              toast(t('planDeleted'));
              render();
            });
          },
        }, t('delete')))));
    mount(main, 
      h('div', { class: 'page-head' }, h('h1', null, t('plans')),
        h('button', { class: 'primary', type: 'button', onclick: () => planDialog(null) }, t('newPlan'))),
      h('p', { class: 'muted' }, t('plansHelp')),
      plans.length ? h('div', { class: 'grid two' }, cards) : h('div', { class: 'card' }, h('p', { class: 'empty' }, t('noPlans'))));
  }

  function planDialog(plan) {
    const WINDOWS = [[5, t('w5h')], [24, t('w1d')], [168, t('w7d')], [720, t('w30d')], ['custom', t('wCustom')]];
    const name = input({ value: plan ? plan.name : '', maxlength: '64' });
    const concurrency = input({ type: 'number', min: '1', max: '64', value: String(plan ? plan.maxConcurrency : 2), dir: 'ltr' });
    const models = input({ dir: 'ltr', value: plan ? plan.allowedModels.join(', ') : '', placeholder: 'gpt-5.5, gpt-5*' });
    const limitsBox = h('div');
    const limits = plan ? plan.limits.map((l) => Object.assign({}, l)) : [];

    function drawLimits() {
      mount(limitsBox, limits.length ? limits.map((l, i) => {
        const known = WINDOWS.some(([w]) => w === l.windowHours);
        const win = select(WINDOWS, known ? l.windowHours : 'custom', { 'aria-label': t('window') });
        const hours = input({ type: 'number', min: '1', max: '8784', value: String(l.windowHours), dir: 'ltr', 'aria-label': t('wCustom') });
        hours.classList.toggle('hidden', known);
        win.addEventListener('change', () => {
          if (win.value === 'custom') {
            hours.classList.remove('hidden');
          } else {
            l.windowHours = Number(win.value);
            hours.value = win.value;
            hours.classList.add('hidden');
          }
        });
        hours.addEventListener('input', () => { l.windowHours = Number(hours.value); });
        const kind = select([['tokens', t('unitTokens')], ['usd', t('unitUsd')]], l.kind);
        kind.addEventListener('change', () => { l.kind = kind.value; });
        const amount = input({ type: 'number', min: '0', step: 'any', value: String(l.amount), dir: 'ltr' });
        amount.addEventListener('input', () => { l.amount = Number(amount.value); });
        return h('div', { class: 'row' },
          field(t('window'), h('div', { class: 'stack-tight' }, win, hours)),
          field(t('unit'), kind),
          field(t('cap'), amount),
          h('div', { class: 'fixed field' }, h('button', { class: 'ghost', type: 'button', 'aria-label': t('removeLimit'), onclick: () => { limits.splice(i, 1); drawLimits(); } }, '✕')));
      }) : h('p', { class: 'muted small' }, t('noLimitsYet')));
    }
    drawLimits();

    const addLimit = h('button', { type: 'button', onclick: () => { limits.push({ windowHours: 5, kind: 'tokens', amount: 1000000 }); drawLimits(); } }, t('addLimit'));
    const template = h('button', {
      type: 'button',
      onclick: () => {
        limits.splice(0, limits.length, { windowHours: 5, kind: 'tokens', amount: 2000000 }, { windowHours: 168, kind: 'tokens', amount: 20000000 });
        drawLimits();
      },
    }, t('codexTemplate'));

    const save = h('button', { class: 'primary', type: 'submit' }, t('save'));
    const form = h('form', {
      onsubmit: (e) => {
        e.preventDefault();
        guarded(save, async () => {
          const body = {
            name: name.value,
            maxConcurrency: Number(concurrency.value),
            allowedModels: models.value,
            limits: limits.map((l) => ({ windowHours: Number(l.windowHours), kind: l.kind, amount: Number(l.amount) })),
          };
          if (plan) await api('PUT', '/api/admin/plans/' + plan.id, body);
          else await api('POST', '/api/admin/plans', body);
          toast(t('planSaved'));
          closeModal();
          render();
        });
      },
    },
    h('div', { class: 'row' }, field(t('planName'), name), field(t('concurrency'), concurrency, t('concurrencyHelp'))),
    field(t('allowedModels'), models, t('allowedModelsHelp')),
    h('h3', null, t('limits')),
    h('p', { class: 'small muted' }, t('limitsHelp')),
    limitsBox,
    h('div', { class: 'actions' }, addLimit, template),
    h('div', { class: 'actions end' }, save));
    openModal(plan ? t('editPlanTitle') : t('newPlanTitle'), form, true);
  }

  // ---------- admin: report ----------
  async function pageReport(main, alive) {
    const [o, recent] = await Promise.all([
      api('GET', '/api/admin/overview?tz=' + tzMinutes()),
      api('GET', '/api/admin/usage?limit=100'),
    ]);
    if (!alive()) return;
    const stat = (label, x) => h('div', { class: 'card stat' },
      h('div', { class: 'label' }, label),
      h('div', { class: 'value' }, tn('tokens', x.tokens, fmtTokens(x.tokens))),
      h('div', { class: 'small muted' }, [tn('requests', x.requests, fmtInt(x.requests))].concat(x.costUsd > 0 ? [fmtUsd(x.costUsd)] : []).join(' · ')));
    const group = (title, rows) => h('div', { class: 'card' },
      h('div', { class: 'card-head' }, h('h2', null, title), h('span', { class: 'small muted' }, t('thirtyDays'))),
      table([t('colName'), t('colRequests'), t('colTokens'), t('colCost'), t('colLast')], rows.map((r) => h('tr', null,
        h('td', null, ltr(r.name)),
        h('td', { class: 'num' }, fmtInt(r.requests)),
        h('td', { class: 'num' }, fmtTokens(r.tokens)),
        h('td', { class: 'num' }, r.costUsd > 0 ? fmtUsd(r.costUsd) : t('none')),
        h('td', { class: 'small' }, ago(r.lastAt)))), t('noUsage')));
    const upstreams = h('div', { class: 'card' },
      h('div', { class: 'card-head' }, h('h2', null, t('upstreams'))),
      o.upstreams.length
        ? table([t('colName'), t('colHost'), t('colModels'), t('colInFlight'), t('colTotal'), t('colLastError')], o.upstreams.map((u) => h('tr', null,
          h('td', null, u.name), h('td', null, ltr(u.host)),
          h('td', null, u.models.length ? ltr(u.models.join(', '), 'small') : t('all')),
          h('td', { class: 'num' }, fmtInt(u.inFlight)), h('td', { class: 'num' }, fmtInt(u.requests)),
          h('td', { class: 'small' }, u.lastError ? h('span', null, ltr(u.lastError), ' · ', ago(u.lastErrorAt)) : t('none')))), '')
        : h('div', { class: 'notice bad' }, t('noUpstream')));
    mount(main, 
      h('div', { class: 'page-head' }, h('h1', null, t('report')),
        h('div', { class: 'actions' },
          h('a', { class: 'btn', href: '/api/admin/usage.csv?days=30', download: '' }, t('exportCsv')),
          h('a', { class: 'btn', href: '/api/admin/backup', download: '' }, t('downloadBackup')))),
      h('div', { class: 'grid three' }, stat(t('last24h'), o.day), stat(t('last7d'), o.week), stat(t('last30d'), o.month)),
      usageCard(t('allUsers30'), o.daily),
      h('div', { class: 'grid two' }, group(t('byUser'), o.byUser), group(t('byModel'), o.byModel)),
      upstreams,
      recentCard(recent, true));
  }

  // ---------- admin: settings ----------
  async function pageSettings(main, alive) {
    const [s, prices] = await Promise.all([api('GET', '/api/admin/settings'), api('GET', '/api/admin/prices')]);
    if (!alive()) return;
    const publicUrl = input({ dir: 'ltr', value: s.publicUrl, placeholder: 'https://codex.example.com' });
    const defaultModel = input({ dir: 'ltr', value: s.defaultModel, placeholder: 'gpt-5.5' });
    const allowSearch = h('input', { type: 'checkbox', checked: s.allowWebSearch });
    const searchPrice = input({ type: 'number', min: '0', max: '10', step: 'any', dir: 'ltr', value: String(s.webSearchPrice) });
    const saveSettings = h('button', { class: 'primary', type: 'submit' }, t('save'));
    const settingsForm = h('form', {
      onsubmit: (e) => {
        e.preventDefault();
        guarded(saveSettings, async () => {
          await api('PUT', '/api/admin/settings', {
            publicUrl: publicUrl.value, defaultModel: defaultModel.value,
            allowWebSearch: allowSearch.checked, webSearchPrice: Number(searchPrice.value),
          });
          toast(t('saved'));
          render();
        });
      },
    },
    field(t('publicUrl'), publicUrl, t('publicUrlHelp', { url: s.effectiveBaseUrl })),
    field(t('defaultModel'), defaultModel, t('defaultModelHelp')),
    h('h3', null, t('webSearch')),
    h('label', { class: 'check' }, allowSearch, t('allowWebSearch')),
    h('p', { class: 'small muted' }, t('allowWebSearchHelp')),
    field(t('webSearchPrice'), searchPrice, t('webSearchPriceHelp')),
    h('div', { class: 'actions end' }, saveSettings));

    const rows = prices.map((p) => Object.assign({}, p));
    const box = h('div');
    function drawPrices() {
      mount(box, rows.length ? rows.map((p, i) => {
        const cell = (key, attrs) => {
          const el = input(Object.assign({ dir: 'ltr', value: String(p[key]) }, attrs));
          el.addEventListener('input', () => { p[key] = attrs && attrs.type === 'number' ? Number(el.value) : el.value; });
          return el;
        };
        return h('div', { class: 'row' },
          field(t('colModel'), cell('model', { placeholder: t('priceModelPlaceholder') })),
          field(t('priceInput'), cell('inputPerM', { type: 'number', min: '0', step: 'any' })),
          field(t('priceCached'), cell('cachedPerM', { type: 'number', min: '0', step: 'any' })),
          field(t('priceOutput'), cell('outputPerM', { type: 'number', min: '0', step: 'any' })),
          h('div', { class: 'fixed field' }, h('button', { class: 'ghost', type: 'button', 'aria-label': t('delete'), onclick: () => { rows.splice(i, 1); drawPrices(); } }, '✕')));
      }) : h('p', { class: 'muted small' }, t('noPrices')));
    }
    drawPrices();
    const addPrice = h('button', { type: 'button', onclick: () => { rows.push({ model: '', inputPerM: 0, cachedPerM: 0, outputPerM: 0 }); drawPrices(); } }, t('addModel'));
    const savePrices = h('button', { class: 'primary', type: 'button' }, t('savePrices'));
    savePrices.addEventListener('click', () => guarded(savePrices, async () => {
      await api('PUT', '/api/admin/prices', rows.map((p) => ({ model: p.model, inputPerM: Number(p.inputPerM), cachedPerM: Number(p.cachedPerM), outputPerM: Number(p.outputPerM) })));
      toast(t('pricesSaved'));
      render();
    }));

    mount(main, 
      h('div', { class: 'page-head' }, h('h1', null, t('settings'))),
      h('div', { class: 'card' }, h('div', { class: 'card-head' }, h('h2', null, t('gateway'))), settingsForm),
      h('div', { class: 'card' },
        h('div', { class: 'card-head' }, h('h2', null, t('pricesTitle'))),
        h('p', { class: 'small muted' }, t('pricesHelp')),
        box,
        h('div', { class: 'actions' }, addPrice),
        h('div', { class: 'actions end' }, savePrices)));
  }

  boot();
})();
