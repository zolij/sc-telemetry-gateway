import { createRemoteJWKSet, jwtVerify } from 'jose';

const DAY = 24 * 60 * 60 * 1000;
const MAX_BODY_BYTES = 1024;
const keySets = new Map();

function response(body, status = 200, contentType = 'application/json; charset=utf-8') {
  return new Response(body, {
    status,
    headers: {
      'content-type': contentType,
      'cache-control': 'no-store',
      'x-content-type-options': 'nosniff',
      'content-security-policy': "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'",
    },
  });
}

async function readBody(request) {
  if (!request.body) return null;
  const reader = request.body.getReader();
  const chunks = [];
  let length = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      length += value.length;
      if (length > MAX_BODY_BYTES) {
        await reader.cancel();
        return null;
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.length;
  }
  return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
}

function validEvent(event) {
  return event && typeof event === 'object' && !Array.isArray(event) &&
    Object.keys(event).sort().join(',') === 'collection,installationId,version' &&
    typeof event.installationId === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(event.installationId) &&
    typeof event.version === 'string' &&
    /^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$/.test(event.version) &&
    event.version.length <= 64 && typeof event.collection === 'boolean';
}

async function ingest(request, env) {
  const ip = request.headers.get('cf-connecting-ip');
  if (!ip) return response('Missing network identity', 400, 'text/plain; charset=utf-8');
  const { success } = await env.INGEST_LIMITER.limit({ key: ip });
  if (!success) return response('Rate limit exceeded', 429, 'text/plain; charset=utf-8');
  if (request.headers.get('content-type')?.split(';')[0].trim().toLowerCase() !== 'application/json')
    return response('Expected JSON', 415, 'text/plain; charset=utf-8');
  let event;
  try {
    const body = await readBody(request);
    if (body === null) return response('Request too large or empty', 413, 'text/plain; charset=utf-8');
    event = JSON.parse(body);
  } catch (error) {
    if (error instanceof SyntaxError || error instanceof TypeError)
      return response('Invalid JSON', 400, 'text/plain; charset=utf-8');
    throw error;
  }
  if (!validEvent(event)) return response('Invalid event', 400, 'text/plain; charset=utf-8');
  const now = Date.now();
  const day = new Date(now).toISOString().slice(0, 10);
  const statement = `INSERT INTO activity (installation_id, day, kind, version, observed_at)
    VALUES (?, ?, ?, ?, ?) ON CONFLICT (installation_id, day, kind)
    DO UPDATE SET version = excluded.version, observed_at = excluded.observed_at
    WHERE excluded.observed_at >= activity.observed_at`;
  const kinds = event.collection ? ['app', 'collection'] : ['app'];
  await env.DB.batch(kinds.map(kind =>
    env.DB.prepare(statement).bind(event.installationId, day, kind, event.version, now)));
  return response(null, 204);
}

async function authorize(request, env) {
  if (!/^[a-z0-9-]+\.cloudflareaccess\.com$/.test(env.ACCESS_TEAM_DOMAIN ?? '') ||
      !/^[a-f0-9]{64}$/.test(env.ACCESS_AUDIENCE ?? ''))
    throw new Error('Cloudflare Access is not configured');
  const token = request.headers.get('cf-access-jwt-assertion');
  if (!token) return false;
  const issuer = `https://${env.ACCESS_TEAM_DOMAIN}`;
  if (!keySets.has(issuer)) keySets.set(issuer, createRemoteJWKSet(new URL(`${issuer}/cdn-cgi/access/certs`)));
  try {
    await jwtVerify(token, keySets.get(issuer), { issuer, audience: env.ACCESS_AUDIENCE, algorithms: ['RS256'] });
    return true;
  } catch (error) {
    if (error.code?.startsWith('ERR_JWT') || error.code?.startsWith('ERR_JWS') ||
        error.code === 'ERR_JWKS_NO_MATCHING_KEY') return false;
    throw error;
  }
}

async function stats(env) {
  const now = Date.now();
  const sql = `WITH ranked AS (
    SELECT kind, installation_id, version,
      ROW_NUMBER() OVER (PARTITION BY kind, installation_id ORDER BY observed_at DESC, day DESC) AS rank
    FROM activity WHERE observed_at >= ? AND observed_at <= ?
  ) SELECT kind, version, COUNT(*) AS count FROM ranked WHERE rank = 1
    GROUP BY kind, version ORDER BY kind, version`;
  const result = {};
  for (const days of [1, 7, 30]) {
    const query = await env.DB.prepare(sql).bind(now - days * DAY, now).all();
    result[days] = query.results;
  }
  return result;
}

const dashboard = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>Gateway usage</title><style>body{font:16px system-ui;max-width:70rem;margin:2rem auto;padding:0 1rem}
table{border-collapse:collapse;width:100%;margin-bottom:2rem}th,td{text-align:left;padding:.5rem;border-bottom:1px solid #bbb}
th:last-child,td:last-child{text-align:right}.error{color:#a00}</style></head>
<body><h1>Gateway usage</h1><p>Approximate unique installations, not people. Each installation is counted under its latest reported version for each activity. Data is retained for up to 30 days.</p>
<main id="results">Loading…</main><script>
async function load() {
  const target = document.getElementById('results');
  try {
    const response = await fetch('/dashboard/api', {cache:'no-store'});
    if (!response.ok) throw new Error('HTTP ' + response.status);
    const data = await response.json();
    target.replaceChildren();
    for (const days of [1, 7, 30]) {
      const heading = document.createElement('h2'); heading.textContent = 'Last ' + days + (days === 1 ? ' day' : ' days'); target.append(heading);
      for (const [kind, label] of [['app','Application running'],['collection','Collection running']]) {
        const rows = data[days].filter(row => row.kind === kind);
        const title = document.createElement('h3'); title.textContent = label + ': ' + rows.reduce((sum,row) => sum + row.count,0); target.append(title);
        const table = document.createElement('table');
        const header = table.insertRow(); for (const text of ['Version','Installations']) {const cell = document.createElement('th');cell.textContent=text;header.append(cell)}
        for (const row of rows) {const tr=table.insertRow();tr.insertCell().textContent=row.version;tr.insertCell().textContent=row.count}
        target.append(table);
      }
    }
  } catch (error) {target.className='error';target.textContent='Could not load usage statistics: ' + error.message}
}
load();
</script></body></html>`;

export default {
  async fetch(request, env) {
    try {
      const url = new URL(request.url);
      if (url.pathname === '/v1/activity' && request.method === 'POST') return await ingest(request, env);
      if ((url.pathname === '/dashboard' || url.pathname === '/dashboard/api') && request.method === 'GET') {
        if (!await authorize(request, env)) return response('Forbidden', 403, 'text/plain; charset=utf-8');
        return url.pathname === '/dashboard'
          ? response(dashboard, 200, 'text/html; charset=utf-8')
          : response(JSON.stringify(await stats(env)));
      }
      return response('Not found', 404, 'text/plain; charset=utf-8');
    } catch (error) {
      console.error('Usage service error', error);
      return response('Service unavailable', 503, 'text/plain; charset=utf-8');
    }
  },
  async scheduled(_event, env) {
    await env.DB.prepare('DELETE FROM activity WHERE observed_at < ?').bind(Date.now() - 30 * DAY).run();
  },
};
