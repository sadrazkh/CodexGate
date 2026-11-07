// End-to-end check of install.sh's result (used by CI): drives the setup page's calls with the
// installer's token, waits for the restart into HTTPS, and checks the panel, the redirect on port 80
// and that the setup page is gone.
//   node tools/install-smoke.mjs <setup-port> <https-port> <domain> <token>
import https from 'node:https';
import http from 'node:http';

const [setupPort, httpsPort, domain, token] = process.argv.slice(2);
const setupBase = `http://127.0.0.1:${setupPort}`;
const headers = { 'X-Setup-Token': token, 'X-CodexGate': '1', 'Content-Type': 'application/json' };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function fail(message) {
  console.error('FAIL: ' + message);
  process.exit(1);
}

async function setup(method, path, body) {
  const res = await fetch(setupBase + path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  return { status: res.status, data: text ? JSON.parse(text) : null };
}

// HTTPS to 127.0.0.1 while presenting the domain (SNI and Host), trusting the test CA's chain blindly.
function secure(method, path, body, cookie) {
  return new Promise((resolve, reject) => {
    const req = https.request({
      host: '127.0.0.1', port: Number(httpsPort), servername: domain, path, method, rejectUnauthorized: false,
      headers: { Host: `${domain}:${httpsPort}`, 'X-CodexGate': '1', 'Content-Type': 'application/json', ...(cookie ? { Cookie: cookie } : {}) },
    }, (res) => {
      let data = '';
      res.on('data', (c) => { data += c; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: data, cert: res.socket.getPeerCertificate?.() }));
    });
    req.on('error', reject);
    if (body) req.write(JSON.stringify(body));
    req.end();
  });
}

const state = await setup('GET', '/api/setup/state');
if (state.status !== 200) fail('setup page did not open with the token: ' + state.status);
if (!state.data.httpAvailable) fail('port 80 should have been free and mapped');
if (state.data.httpsPort !== Number(httpsPort)) fail('wrong https port in state');
const wrong = await fetch(setupBase + '/api/setup/state', { headers: { 'X-Setup-Token': 'x'.repeat(32) } });
if (wrong.status !== 404) fail('setup page opened without the right token');
console.log('setup page opens only with the token');

const started = await setup('POST', '/api/setup/complete', {
  username: 'boss', password: 'install-pass-1', domain, method: 'http', cloudflareToken: '', email: '', openAiKey: 'sk-ci-key-1234',
});
if (started.status !== 202) fail('setup did not start: ' + JSON.stringify(started.data));

let progress = null;
for (let i = 0; i < 120; i++) {
  await sleep(1000);
  const p = await setup('GET', '/api/setup/progress');
  if (p.status !== 200) continue;
  progress = p.data;
  if (!progress.running) break;
}
if (!progress || progress.error) fail('setup failed: ' + JSON.stringify(progress));
console.log('setup steps:', progress.steps.join(' > '), '| url:', progress.url);

let health = null;
for (let i = 0; i < 60; i++) {
  await sleep(2000);
  try {
    health = await secure('GET', '/healthz');
    if (health.status === 200) break;
  } catch { /* restarting */ }
}
if (!health || health.status !== 200) fail('HTTPS did not come up after the restart');
const issuer = health.cert && health.cert.issuer ? JSON.stringify(health.cert.issuer) : '';
if (!/Pebble/i.test(issuer)) fail('the certificate is not from the test CA: ' + issuer);
if (!health.cert.subjectaltname || !health.cert.subjectaltname.includes(domain)) fail('the certificate is not for ' + domain);
console.log('HTTPS is up with a certificate for', domain, 'from', issuer);

const login = await secure('POST', '/api/auth/login', { username: 'boss', password: 'install-pass-1' });
if (login.status !== 200) fail('the admin from setup cannot sign in: ' + login.status + ' ' + login.body);
const cookie = String(login.headers['set-cookie']).split(';')[0];
const settings = JSON.parse((await secure('GET', '/api/admin/settings', null, cookie)).body);
if (settings.openAiKey !== '…1234') fail('the OpenAI key from setup was not saved');
console.log('admin signs in; the OpenAI key from setup is in Settings');

try {
  const closed = await fetch(setupBase + '/api/setup/state', { headers });
  if (closed.status === 200) fail('the setup page still answers after setup');
} catch { /* nothing listens any more: closed */ }
console.log('the setup page is closed');

const redirect = await new Promise((resolve, reject) => {
  http.get({ host: '127.0.0.1', port: 80, path: '/panel', headers: { Host: domain } }, (res) => resolve(res)).on('error', reject);
});
if (redirect.statusCode !== 301 || redirect.headers.location !== `https://${domain}:${httpsPort}/panel`) {
  fail('port 80 does not redirect to HTTPS: ' + redirect.statusCode + ' ' + redirect.headers.location);
}
console.log('port 80 redirects to', redirect.headers.location);
console.log('PASS');
