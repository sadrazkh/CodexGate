'use strict';

// The one-time setup page of an installed server. It opens only with the token the installer
// printed after "#", which browsers never send to the server or write into any log.
(() => {
  const S = {
    fa: {
      title: 'نصب CodexGate',
      intro: 'این صفحه فقط یک بار، برای راه‌اندازی، باز است. بعد از «نصب» گواهی HTTPS گرفته می‌شود، پنل روی دامنه‌ی شما بالا می‌آید و این صفحه بسته می‌شود.',
      other: 'English',
      badLink: 'این لینک نصب نامعتبر است یا نصب قبلاً انجام شده است. اگر نصب‌کننده را دوباره اجرا کرده‌اید، لینک تازه‌ای که چاپ کرد را باز کنید.',
      admin: 'حساب مدیر',
      username: 'نام کاربری',
      password: 'رمز عبور',
      password2: 'تکرار رمز',
      passwordsDiffer: 'دو رمز یکی نیستند.',
      domainTitle: 'دامنه و گواهی HTTPS',
      domain: 'دامنه',
      domainHelp: 'یک رکورد A این دامنه را به IP همین سرور ({ip}) اشاره دهد.',
      domainHelpNoIp: 'یک رکورد A این دامنه را به IP همین سرور اشاره دهد.',
      checking: 'در حال بررسی DNS…',
      pointsHere: '✓ به همین سرور اشاره می‌کند.',
      pointsElsewhere: 'این دامنه به {addr} اشاره می‌کند، نه به این سرور ({ip}). اگر پشت پروکسی Cloudflare است، روش Cloudflare را انتخاب کنید.',
      notFound: 'این دامنه هنوز IP ندارد. رکورد DNS را بسازید؛ ممکن است چند دقیقه طول بکشد.',
      port: 'پنل روی پورت {port} باز می‌شود: {url}',
      method: 'روش گرفتن گواهی',
      methodHttp: 'از راه پورت 80',
      methodHttpHelp: 'پورت 80 این سرور آزاد بود و نصب‌کننده آن را برای همین کار گرفت.',
      methodHttpOff: 'پورت 80 این سرور دست برنامه‌ی دیگری است؛ از روش Cloudflare استفاده کنید.',
      methodCf: 'از راه DNS در Cloudflare',
      methodCfHelp: 'هیچ پورتی لازم ندارد. در Cloudflare یک API Token با دسترسی Zone → DNS → Edit برای همین دامنه بسازید.',
      cfToken: 'API Token کلادفلر',
      email: 'ایمیل (اختیاری)',
      emailHelp: 'Let\'s Encrypt اگر تمدید گواهی به مشکل بخورد، به این ایمیل خبر می‌دهد.',
      keyTitle: 'سرویس مدل',
      key: 'کلید API اوپن‌ای‌آی (اختیاری)',
      keyHelp: 'بعداً هم در «تنظیمات» پنل می‌شود گذاشت یا عوضش کرد. فقط روی سرور شما ذخیره می‌شود.',
      install: 'نصب',
      working: 'در حال نصب',
      s_account: 'ساخت حساب Let\'s Encrypt',
      s_order: 'درخواست گواهی برای دامنه',
      s_dns: 'ساخت رکورد TXT در Cloudflare و صبر برای پخش شدنش',
      s_validate: 'تأیید مالکیت دامنه',
      s_certificate: 'دریافت گواهی',
      s_admin: 'ساخت حساب مدیر و ذخیره‌ی تنظیمات',
      s_done: 'تمام شد',
      finished: 'نصب تمام شد',
      finishedText: 'برنامه با HTTPS دوباره بالا می‌آید (چند ثانیه). پنل:',
      closed: 'این صفحه دیگر باز نمی‌شود. برای تغییرات بعدی از «تنظیمات» پنل استفاده کنید.',
      failed: 'نصب کامل نشد',
      tryAgain: 'مشکل را برطرف کنید و دوباره «نصب» را بزنید. چیزی ذخیره نشده است.',
      e_invalid_username: 'نام کاربری باید ۳ تا ۳۲ نویسه از حروف انگلیسی، عدد، نقطه، خط تیره یا زیرخط باشد.',
      e_weak_password: 'رمز باید دست‌کم ۸ نویسه باشد.',
      e_invalid_domain: 'دامنه نامعتبر است. مثال: codex.example.com',
      e_http_unavailable: 'پورت 80 در دسترس نیست؛ روش Cloudflare را انتخاب کنید.',
      e_invalid_cloudflare_token: 'API Token کلادفلر را وارد کنید.',
      e_invalid_method: 'روش گرفتن گواهی را انتخاب کنید.',
      e_invalid_email: 'ایمیل نامعتبر است.',
      e_invalid_key: 'کلید نامعتبر است.',
      e_busy: 'نصب در حال انجام است.',
      e_validation_failed: 'Let\'s Encrypt نتوانست مالکیت دامنه را تأیید کند. DNS دامنه و پورت 80 (یا توکن Cloudflare) را بررسی کنید.',
      e_cloudflare_zone_not_found: 'این توکن Cloudflare به دامنه‌ی شما دسترسی ندارد.',
      e_cloudflare_failed: 'Cloudflare درخواست را رد کرد.',
      e_certificate_failed: 'گرفتن گواهی شکست خورد.',
      e_too_many_attempts: 'تلاش‌ها زیاد بود؛ یک دقیقه صبر کنید.',
      e_network: 'ارتباط با سرور برقرار نشد.',
    },
    en: {
      title: 'Set up CodexGate',
      intro: 'This page is open once, for setting up. After “Install” an HTTPS certificate is obtained, the panel comes up on your domain, and this page closes.',
      other: 'فارسی',
      badLink: 'This setup link is invalid or setup is already done. If you ran the installer again, open the new link it printed.',
      admin: 'Admin account',
      username: 'Username',
      password: 'Password',
      password2: 'Repeat password',
      passwordsDiffer: 'The passwords differ.',
      domainTitle: 'Domain and HTTPS certificate',
      domain: 'Domain',
      domainHelp: 'Point an A record of this domain at this server’s IP ({ip}).',
      domainHelpNoIp: 'Point an A record of this domain at this server’s IP.',
      checking: 'Checking DNS…',
      pointsHere: '✓ Points at this server.',
      pointsElsewhere: 'This domain points at {addr}, not at this server ({ip}). If it is behind Cloudflare’s proxy, pick the Cloudflare method.',
      notFound: 'This domain has no IP yet. Create the DNS record; it can take a few minutes.',
      port: 'The panel opens on port {port}: {url}',
      method: 'How to get the certificate',
      methodHttp: 'Through port 80',
      methodHttpHelp: 'Port 80 of this server was free, and the installer took it for this.',
      methodHttpOff: 'Another program uses port 80 on this server; use the Cloudflare method.',
      methodCf: 'Through DNS at Cloudflare',
      methodCfHelp: 'Needs no port. In Cloudflare, create an API token with Zone → DNS → Edit for this domain.',
      cfToken: 'Cloudflare API token',
      email: 'Email (optional)',
      emailHelp: 'Let’s Encrypt writes here if renewing the certificate runs into trouble.',
      keyTitle: 'Model API',
      key: 'OpenAI API key (optional)',
      keyHelp: 'You can also add or change it later in the panel’s Settings. It is stored only on your server.',
      install: 'Install',
      working: 'Installing',
      s_account: 'Creating a Let’s Encrypt account',
      s_order: 'Ordering a certificate for the domain',
      s_dns: 'Adding a TXT record at Cloudflare and letting it spread',
      s_validate: 'Proving the domain is yours',
      s_certificate: 'Getting the certificate',
      s_admin: 'Creating the admin and saving settings',
      s_done: 'Done',
      finished: 'Setup is done',
      finishedText: 'The app comes back with HTTPS in a few seconds. Your panel:',
      closed: 'This page will not open again. Use the panel’s Settings for later changes.',
      failed: 'Setup did not finish',
      tryAgain: 'Fix the problem and press “Install” again. Nothing was saved.',
      e_invalid_username: 'Usernames are 3–32 letters, digits, dots, dashes or underscores.',
      e_weak_password: 'The password needs at least 8 characters.',
      e_invalid_domain: 'Invalid domain. Example: codex.example.com',
      e_http_unavailable: 'Port 80 is not available; pick the Cloudflare method.',
      e_invalid_cloudflare_token: 'Enter the Cloudflare API token.',
      e_invalid_method: 'Pick how to get the certificate.',
      e_invalid_email: 'Invalid email.',
      e_invalid_key: 'Invalid key.',
      e_busy: 'Setup is already running.',
      e_validation_failed: 'Let’s Encrypt could not confirm the domain is yours. Check its DNS and port 80 (or the Cloudflare token).',
      e_cloudflare_zone_not_found: 'This Cloudflare token cannot see your domain.',
      e_cloudflare_failed: 'Cloudflare refused the request.',
      e_certificate_failed: 'Getting the certificate failed.',
      e_too_many_attempts: 'Too many attempts; wait a minute.',
      e_network: 'Could not reach the server.',
    },
  };

  const root = document.getElementById('app');
  const token = decodeURIComponent((location.hash || '').replace(/^#/, ''));
  history.replaceState(null, '', location.pathname);
  let lang = (navigator.language || '').toLowerCase().startsWith('fa') ? 'fa' : 'en';
  let state = null;
  let form = null;

  const t = (k, v) => {
    let s = S[lang][k] || S.en[k] || k;
    for (const [a, b] of Object.entries(v || {})) s = s.split('{' + a + '}').join(String(b));
    return s;
  };

  function h(tag, attrs, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v === null || v === undefined || v === false) continue;
      if (k === 'class') el.className = v;
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

  async function call(method, url, body) {
    let res;
    try {
      res = await fetch(url, {
        method,
        headers: Object.assign({ 'X-Setup-Token': token, 'X-CodexGate': '1' }, body ? { 'Content-Type': 'application/json' } : {}),
        body: body ? JSON.stringify(body) : undefined,
      });
    } catch (e) {
      return { ok: false, status: 0, data: { error: 'network' } };
    }
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
    return { ok: res.ok, status: res.status, data };
  }

  const errorText = (data) => {
    const code = (data && data.error) || 'network';
    const base = S.en['e_' + code] ? t('e_' + code) : code;
    return data && data.detail ? base + ' (' + data.detail + ')' : base;
  };

  function apply() {
    document.documentElement.lang = lang;
    document.documentElement.dir = lang === 'fa' ? 'rtl' : 'ltr';
    document.title = t('title');
  }

  function shell(...body) {
    apply();
    root.replaceChildren(h('div', { class: 'card login setup' },
      h('div', { class: 'login-top' },
        h('div', { class: 'brand' }, h('img', { src: 'favicon.svg', alt: '' }), t('title')),
        h('button', { class: 'small ghost lang', type: 'button', onclick: () => { lang = lang === 'fa' ? 'en' : 'fa'; render(); } }, t('other'))),
      body));
  }

  const field = (label, control, help) => h('label', { class: 'field' }, h('span', null, label), control, help ? h('small', null, help) : null);

  function render() {
    if (!state) {
      shell(h('div', { class: 'notice warn' }, t('badLink')));
      return;
    }
    const keep = form || {};
    const v = (k, d) => (keep[k] !== undefined ? keep[k] : d);
    const username = h('input', { dir: 'ltr', value: v('username', 'admin'), autocomplete: 'username' });
    const password = h('input', { type: 'password', dir: 'ltr', autocomplete: 'new-password', value: v('password', '') });
    const password2 = h('input', { type: 'password', dir: 'ltr', autocomplete: 'new-password', value: v('password2', '') });
    const domain = h('input', { dir: 'ltr', placeholder: 'codex.example.com', value: v('domain', '') });
    const dnsNote = h('small', { class: 'dns-note' });
    const viaHttp = h('input', { type: 'radio', name: 'method', value: 'http', disabled: !state.httpAvailable });
    const viaCf = h('input', { type: 'radio', name: 'method', value: 'cloudflare' });
    (v('method', state.httpAvailable ? 'http' : 'cloudflare') === 'http' && state.httpAvailable ? viaHttp : viaCf).checked = true;
    const cfToken = h('input', { type: 'password', dir: 'ltr', autocomplete: 'off', value: v('cfToken', '') });
    const cfBox = field(t('cfToken'), cfToken, t('methodCfHelp'));
    const email = h('input', { type: 'email', dir: 'ltr', value: v('email', '') });
    const key = h('input', { type: 'password', dir: 'ltr', autocomplete: 'off', placeholder: 'sk-...', value: v('key', '') });
    const error = h('div', { class: 'notice bad hidden', role: 'alert' });
    const submit = h('button', { class: 'primary', type: 'submit' }, t('install'));
    const syncMethod = () => cfBox.classList.toggle('hidden', !viaCf.checked);
    viaHttp.addEventListener('change', syncMethod);
    viaCf.addEventListener('change', syncMethod);
    syncMethod();

    const portLine = h('small', { class: 'muted' });
    const syncPort = () => {
      const d = domain.value.trim() || 'codex.example.com';
      portLine.textContent = t('port', { port: state.httpsPort, url: 'https://' + d + (state.httpsPort === 443 ? '' : ':' + state.httpsPort) });
    };
    domain.addEventListener('input', syncPort);
    syncPort();
    domain.addEventListener('change', async () => {
      const d = domain.value.trim();
      if (!d) return;
      dnsNote.textContent = t('checking');
      const r = await call('POST', '/api/setup/domain', { domain: d });
      if (!r.ok) { dnsNote.textContent = ''; return; }
      if (!r.data.addresses.length) dnsNote.textContent = t('notFound');
      else if (r.data.pointsHere || !state.publicIp) dnsNote.textContent = r.data.pointsHere ? t('pointsHere') : '';
      else dnsNote.textContent = t('pointsElsewhere', { addr: r.data.addresses.join(', '), ip: state.publicIp });
    });

    const remember = () => {
      form = {
        username: username.value, password: password.value, password2: password2.value, domain: domain.value,
        method: viaCf.checked ? 'cloudflare' : 'http', cfToken: cfToken.value, email: email.value, key: key.value,
      };
    };

    const el = h('form', {
      onsubmit: async (e) => {
        e.preventDefault();
        remember();
        error.classList.add('hidden');
        if (password.value !== password2.value) {
          error.textContent = t('passwordsDiffer');
          error.classList.remove('hidden');
          return;
        }
        submit.disabled = true;
        const r = await call('POST', '/api/setup/complete', {
          username: username.value, password: password.value, domain: domain.value,
          method: viaCf.checked ? 'cloudflare' : 'http', cloudflareToken: cfToken.value, email: email.value, openAiKey: key.value,
        });
        if (!r.ok) {
          submit.disabled = false;
          error.textContent = errorText(r.data);
          error.classList.remove('hidden');
          return;
        }
        progress();
      },
    },
    h('p', { class: 'muted small' }, t('intro')),
    error,
    h('h3', null, t('admin')),
    field(t('username'), username),
    h('div', { class: 'row' }, field(t('password'), password), field(t('password2'), password2)),
    h('h3', null, t('domainTitle')),
    field(t('domain'), domain, state.publicIp ? t('domainHelp', { ip: state.publicIp }) : t('domainHelpNoIp')),
    dnsNote,
    h('p', null, portLine),
    h('div', { class: 'field' }, h('span', null, t('method')),
      h('label', { class: 'check' }, viaHttp, t('methodHttp')),
      h('small', { class: 'muted method-note' }, state.httpAvailable ? t('methodHttpHelp') : t('methodHttpOff')),
      h('label', { class: 'check' }, viaCf, t('methodCf'))),
    cfBox,
    field(t('email'), email, t('emailHelp')),
    h('h3', null, t('keyTitle')),
    field(t('key'), key, t('keyHelp')),
    h('div', { class: 'actions end' }, submit));
    shell(el);
  }

  async function progress() {
    const r = await call('GET', '/api/setup/progress');
    if (!r.ok) {
      setTimeout(progress, 1500);
      return;
    }
    const p = r.data;
    const steps = h('ul', { class: 'setup-steps' }, p.steps.map((s) => h('li', null, '✓ ' + t('s_' + s))));
    if (p.url) {
      shell(h('h3', null, t('finished')), steps, h('p', null, t('finishedText')),
        h('p', null, h('a', { href: p.url, dir: 'ltr' }, p.url)), h('p', { class: 'small muted' }, t('closed')));
      return;
    }
    if (p.error) {
      const back = h('button', { class: 'primary', type: 'button', onclick: render }, t('install'));
      shell(h('h3', null, t('failed')), steps,
        h('div', { class: 'notice bad' }, errorText({ error: p.error, detail: p.detail })),
        h('p', { class: 'small muted' }, t('tryAgain')), h('div', { class: 'actions end' }, back));
      return;
    }
    shell(h('h3', null, t('working') + '…'), steps);
    setTimeout(progress, 1500);
  }

  (async () => {
    apply();
    if (token.length >= 16) {
      const r = await call('GET', '/api/setup/state');
      if (r.ok) state = r.data;
    }
    render();
  })();
})();
