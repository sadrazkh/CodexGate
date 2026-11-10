// End-to-end check of server sessions against a running "remote" image (used by CI).
//   node tools/remote-smoke.mjs http://127.0.0.1:8080 <admin-password>
// Creates a plan with server sessions and a teammate, starts a shell session, and through the web
// terminal checks: it runs as the teammate's own account, cannot read the gateway's data, can run
// Codex through the gateway (needs tools/mock-upstream.mjs as the upstream), survives a disconnect,
// and is gone after it is stopped.
const [base, adminPassword] = process.argv.slice(2);
const wsBase = base.replace(/^http/, 'ws');

function client() {
  let cookie = '';
  return async (method, url, body) => {
    const res = await fetch(base + url, {
      method,
      headers: { 'X-CodexGate': '1', 'Content-Type': 'application/json', ...(cookie ? { Cookie: cookie } : {}) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const set = res.headers.get('set-cookie');
    if (set) cookie = set.split(';')[0];
    const text = await res.text();
    if (!res.ok) throw new Error(`${method} ${url} -> ${res.status} ${text}`);
    return text ? JSON.parse(text) : null;
  };
}

function fail(message, output) {
  console.error('FAIL: ' + message);
  if (output) console.error('--- terminal output ---\n' + output.replace(/\x1b\[[0-9;?]*[A-Za-z]/g, ''));
  process.exit(1);
}

async function terminal(api, id, onOpen, until, timeoutMs) {
  const { ticket } = await api('POST', `/api/remote/sessions/${id}/ticket`);
  const ws = new WebSocket(`${wsBase}/api/remote/sessions/${id}/terminal?ticket=${encodeURIComponent(ticket)}&cols=160&rows=40`);
  ws.binaryType = 'arraybuffer';
  const decoder = new TextDecoder();
  let output = '';
  return await new Promise((resolve) => {
    const timer = setTimeout(() => { ws.close(); resolve(output); }, timeoutMs);
    ws.onopen = () => onOpen(ws);
    ws.onmessage = (e) => {
      output += typeof e.data === 'string' ? e.data : decoder.decode(e.data, { stream: true });
      if (until(output)) { clearTimeout(timer); ws.close(); resolve(output); }
    };
    ws.onclose = () => { clearTimeout(timer); resolve(output); };
    ws.onerror = () => {};
  });
}

const admin = client();
await admin('POST', '/api/auth/login', { username: 'admin', password: adminPassword });
await admin('PUT', '/api/admin/settings', { publicUrl: '', defaultModel: 'gpt-5.5' });
const planId = (await admin('POST', '/api/admin/plans', {
  name: 'Remote', maxConcurrency: 2, allowedModels: '', limits: [], remoteEnabled: true, remoteMaxSessions: 2, remoteMemoryMb: 0,
})).id;
const userId = (await admin('POST', '/api/admin/users', { username: 'teammate', password: 'teammate-pass-1', planId, expiresInDays: 30 })).id;

const mate = client();
await mate('POST', '/api/auth/login', { username: 'teammate', password: 'teammate-pass-1' });
const me = await mate('GET', '/api/me/');
if (!me.remoteAvailable) fail('remoteAvailable is false');
const session = await mate('POST', '/api/remote/sessions', { name: 'Smoke', dir: 'demo', program: 'shell', cols: 160, rows: 40 });
console.log('started session', session.id);

// Markers are split in the command text ("DATA-""READ") so only real output contains them, not the echoed command.
const script = 'id -un; pwd; (cat /data/codexgate.db > /dev/null 2>&1 && echo DATA-"READABLE") || echo NO-DATA-"ACCESS"; ' +
  "echo سلام-$((1+1)); codex exec --skip-git-repo-check 'Say hello' 2>&1 | tail -n 3; echo DONE-$((40+2))\r";
const out = await terminal(mate, session.id, (ws) => ws.send(new TextEncoder().encode(script)), (o) => o.includes('DONE-42'), 120000);
if (!out.includes('DONE-42')) fail('the command did not finish', out);
if (!out.includes(`cg-${userId}`)) fail(`not running as cg-${userId}`, out);
if (out.includes('DATA-READABLE') || !out.includes('NO-DATA-ACCESS')) fail('the session can read the gateway data', out);
if (!out.includes('/home/cg-' + userId + '/work/demo')) fail('wrong working folder', out);
if (!out.includes('Hello from the CodexGate mock upstream.')) fail('Codex did not answer through the gateway', out);
if (!out.includes('سلام-2')) fail('Persian typed into the terminal did not come back intact', out);
console.log('ran as its own account, could not read /data, Codex answered through the gateway, Persian typing intact');

const usage = await admin('GET', `/api/admin/usage?userId=${userId}`);
if (!usage.length) fail('no usage recorded for the server session');
console.log('usage recorded:', usage.length, 'request(s)');

const listed = await mate('GET', '/api/remote/');
if (!listed.sessions.find((s) => s.id === session.id && s.running)) fail('session not running after the browser left');
const again = await terminal(mate, session.id, () => {}, (o) => o.includes('DONE-42'), 15000);
if (!again.includes('DONE-42')) fail('reattaching did not show the earlier screen', again);
console.log('survived the disconnect and showed its screen again');

await mate('DELETE', `/api/remote/sessions/${session.id}`);
const after = await mate('GET', '/api/remote/');
if (after.sessions.length) fail('session still listed after stop');
console.log('stopped and gone');

// --- own ChatGPT login: the real Codex CLI, signed in with ChatGPT, through the relay ---------------
// The gateway's ChatGpt:BaseUrl points at the mock, which says which sign-in reached it.
const ownPlan = (await admin('POST', '/api/admin/plans', {
  name: 'Own login', maxConcurrency: 2, allowedModels: '', limits: [], remoteEnabled: true, remoteMaxSessions: 2, remoteMemoryMb: 0, ownLogin: true,
})).id;
const soloId = (await admin('POST', '/api/admin/users', { username: 'solo', password: 'solo-pass-123', planId: ownPlan, expiresInDays: 30 })).id;
const solo = client();
await solo('POST', '/api/auth/login', { username: 'solo', password: 'solo-pass-123' });
const phone = (await solo('POST', '/api/me/devices', { name: 'Phone', limits: [] })).id;
const own = await solo('POST', '/api/remote/sessions', { name: 'Own', dir: 'own', program: 'shell', cols: 160, rows: 40, deviceId: phone });

// A ChatGPT sign-in as Codex stores it (unsigned test tokens; nothing here talks to OpenAI).
const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
const jwt = (claims) => `${b64({ alg: 'none', typ: 'JWT' })}.${b64(claims)}.c2ln`;
const claims = {
  email: 'solo@example.test', exp: Math.floor(Date.now() / 1000) + 30 * 86400,
  'https://api.openai.com/auth': { chatgpt_account_id: 'acc-smoke', chatgpt_plan_type: 'pro', chatgpt_user_id: 'user-smoke' },
};
const auth = {
  OPENAI_API_KEY: null,
  tokens: { id_token: jwt(claims), access_token: jwt(claims), refresh_token: 'rt-smoke', account_id: 'acc-smoke' },
  last_refresh: new Date().toISOString(),
};
const ownScript = `mkdir -p ~/.codex && printf '%s' '${JSON.stringify(auth)}' > ~/.codex/auth.json && chmod 600 ~/.codex/auth.json; ` +
  "grep -c requires_openai_auth ~/.codex/config.toml; echo KEYS-IN-ARGS-$(cat /proc/[0-9]*/cmdline 2>/dev/null | tr '\\000' ' ' | grep -c '[c]gk_'); codex login status 2>&1 | tail -n 2; " +
  "codex exec --skip-git-repo-check 'Say hello' 2>&1 | tail -n 4; echo OWN-$((40+3))\r";
const ownOut = await terminal(solo, own.id, (ws) => ws.send(new TextEncoder().encode(ownScript)), (o) => o.includes('OWN-43'), 120000);
if (!ownOut.includes('OWN-43')) fail('the own-login commands did not finish', ownOut);
if (!ownOut.includes('KEYS-IN-ARGS-0')) fail('a gateway key is visible in a process command line', ownOut);
if (!ownOut.includes('Hello from the ChatGPT mock, account acc-smoke')) fail('Codex signed in with ChatGPT did not answer through the relay', ownOut);
console.log('Codex signed in with ChatGPT answered through the relay; no key on any command line');

const soloUsage = await solo('GET', '/api/me/usage');
const viaPhone = soloUsage.recent.find((r) => r.device === 'Phone');
if (!viaPhone) fail('the own-login request was not recorded for the device: ' + JSON.stringify(soloUsage.recent));
const listedOwn = (await solo('GET', '/api/remote/')).sessions.find((s) => s.id === own.id);
if (!listedOwn || !(listedOwn.tokens > 0)) fail('the session shows no tokens of its own');
console.log('recorded for the device "Phone", and', listedOwn.tokens, 'tokens on the session itself (user', soloId + ')');
await solo('DELETE', `/api/remote/sessions/${own.id}`);
console.log('PASS');
