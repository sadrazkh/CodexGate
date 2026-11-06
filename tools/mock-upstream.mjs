// A stand-in for the OpenAI Responses API, for trying CodexGate without a real key or spending money.
// Usage: node tools/mock-upstream.mjs [port] [dump-dir]   then set Gate:Upstreams:0:BaseUrl=http://127.0.0.1:<port>/v1
// With dump-dir, the last request (headers and body) is written to dump-dir/last-request.json.
// It answers every request with a short streamed message and realistic usage numbers.
import fs from 'node:fs';
import http from 'node:http';

const port = Number(process.argv[2] || 5191);
const dumpDir = process.argv[3];
let counter = 0;

function sse(res, events) {
  res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', 'x-request-id': 'req_mock' });
  let seq = 0;
  for (const e of events) {
    res.write(`event: ${e.type}\ndata: ${JSON.stringify({ ...e, sequence_number: seq++ })}\n\n`);
  }
  res.end();
}

const server = http.createServer((req, res) => {
  let body = '';
  req.on('data', (c) => { body += c; });
  req.on('end', () => {
    const auth = req.headers.authorization || '';
    console.log(`${new Date().toISOString()} ${req.method} ${req.url} auth=${auth.slice(0, 12)}… bytes=${body.length}`);
    if (req.method === 'GET' && req.url === '/v1/models') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ object: 'list', data: [{ id: 'gpt-5.5', object: 'model' }, { id: 'gpt-5.4-mini', object: 'model' }] }));
      return;
    }
    if (req.method !== 'POST' || !req.url.startsWith('/v1/responses')) {
      res.writeHead(404, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'not found', type: 'invalid_request_error' } }));
      return;
    }
    if (dumpDir) fs.writeFileSync(dumpDir + '/last-request.json', JSON.stringify({ url: req.url, headers: req.headers, body }, null, 2));
    let request = {};
    try { request = JSON.parse(body); } catch { /* answer anyway */ }
    const id = `resp_mock_${++counter}`;
    const model = request.model || 'gpt-5.5';
    const text = 'Hello from the CodexGate mock upstream.';
    const item = { id: `msg_${counter}`, type: 'message', status: 'completed', role: 'assistant', content: [{ type: 'output_text', text, annotations: [] }] };
    const usage = {
      input_tokens: 1200 + body.length % 500,
      input_tokens_details: { cached_tokens: 1000 },
      output_tokens: 42,
      output_tokens_details: { reasoning_tokens: 10 },
      total_tokens: 1242 + body.length % 500,
    };
    const base = { id, object: 'response', created_at: Math.floor(Date.now() / 1000), model };
    const words = text.split(/(?<= )/);
    sse(res, [
      { type: 'response.created', response: { ...base, status: 'in_progress', output: [] } },
      { type: 'response.output_item.added', output_index: 0, item: { ...item, status: 'in_progress', content: [] } },
      { type: 'response.content_part.added', item_id: item.id, output_index: 0, content_index: 0, part: { type: 'output_text', text: '', annotations: [] } },
      ...words.map((w) => ({ type: 'response.output_text.delta', item_id: item.id, output_index: 0, content_index: 0, delta: w })),
      { type: 'response.output_text.done', item_id: item.id, output_index: 0, content_index: 0, text },
      { type: 'response.content_part.done', item_id: item.id, output_index: 0, content_index: 0, part: item.content[0] },
      { type: 'response.output_item.done', output_index: 0, item },
      { type: 'response.completed', response: { ...base, status: 'completed', output: [item], usage } },
    ]);
  });
});

server.listen(port, '127.0.0.1', () => console.log(`mock upstream on http://127.0.0.1:${port}/v1`));
