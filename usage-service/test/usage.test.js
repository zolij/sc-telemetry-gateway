import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { exportJWK, generateKeyPair, SignJWT } from 'jose';
import worker from '../src/index.js';

function environment() {
  const db = new DatabaseSync(':memory:');
  db.exec(readFileSync(new URL('../schema.sql', import.meta.url), 'utf8'));
  return {
    sql: db,
    DB: {
      prepare(sql) {
        return {
          bind(...args) {
            return {
              run: async () => db.prepare(sql).run(...args),
              all: async () => ({ results: db.prepare(sql).all(...args) }),
            };
          },
        };
      },
      async batch(statements) {
        db.exec('BEGIN');
        try {
          for (const statement of statements) await statement.run();
          db.exec('COMMIT');
        } catch (error) {
          db.exec('ROLLBACK');
          throw error;
        }
      },
    },
    INGEST_LIMITER: { limit: async () => ({ success: true }) },
    ACCESS_TEAM_DOMAIN: 'example.cloudflareaccess.com',
    ACCESS_AUDIENCE: 'a'.repeat(64),
  };
}

const id = 'a4d10f44-7748-46ae-81d1-e5087eb29e5c';
const event = { installationId: id, version: '0.2.0-preview.2', collection: false };
function request(body, options = {}) {
  return new Request('https://example.test/v1/activity', {
    method: 'POST',
    headers: { 'content-type': 'application/json', 'cf-connecting-ip': '192.0.2.1' },
    body: typeof body === 'string' ? body : JSON.stringify(body),
    ...options,
  });
}

test('deduplicates daily reports and tracks collection independently', async () => {
  const env = environment();
  assert.equal((await worker.fetch(request(event), env)).status, 204);
  assert.equal((await worker.fetch(request({ ...event, version: '0.3.0', collection: true }), env)).status, 204);
  assert.deepEqual(env.sql.prepare('SELECT kind, version FROM activity ORDER BY kind').all().map(row => ({ ...row })), [
    { kind: 'app', version: '0.3.0' },
    { kind: 'collection', version: '0.3.0' },
  ]);
});

test('rejects malformed, oversized and extra data', async () => {
  const env = environment();
  for (const body of ['{', 'x'.repeat(1025), JSON.stringify({ ...event, username: 'no' }),
    JSON.stringify({ ...event, installationId: 'not-an-id' }), JSON.stringify({ ...event, collection: 'true' })]) {
    assert.ok((await worker.fetch(request(body), env)).status >= 400);
  }
  assert.equal(env.sql.prepare('SELECT COUNT(*) AS count FROM activity').get().count, 0);
});

test('enforces rate limit before writing', async () => {
  const env = environment();
  env.INGEST_LIMITER.limit = async () => ({ success: false });
  assert.equal((await worker.fetch(request(event), env)).status, 429);
  assert.equal(env.sql.prepare('SELECT COUNT(*) AS count FROM activity').get().count, 0);
});

test('dashboard and API deny requests without Access JWT', async () => {
  const env = environment();
  for (const path of ['/dashboard', '/dashboard/api']) {
    assert.equal((await worker.fetch(new Request(`https://example.test${path}`), env)).status, 403);
  }
});

test('validates Access tokens and groups each installation by its latest version', async () => {
  const env = environment();
  const { publicKey, privateKey } = await generateKeyPair('RS256');
  const jwk = { ...await exportJWK(publicKey), kid: 'test-key', alg: 'RS256', use: 'sig' };
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => Response.json({ keys: [jwk] });
  try {
    const makeToken = audience => new SignJWT({})
      .setProtectedHeader({ alg: 'RS256', kid: 'test-key' })
      .setIssuer('https://example.cloudflareaccess.com').setAudience(audience)
      .setIssuedAt().setExpirationTime('1h').sign(privateKey);
    const old = Date.now() - 86400000;
    env.sql.prepare('INSERT INTO activity VALUES (?, ?, ?, ?, ?)').run(id, '2026-01-01', 'app', '0.2.0', old);
    await worker.fetch(request({ ...event, version: '0.3.0', collection: true }), env);
    const dashboardRequest = token => new Request('https://example.test/dashboard/api', {
      headers: { 'cf-access-jwt-assertion': token },
    });
    assert.equal((await worker.fetch(dashboardRequest(await makeToken('wrong')), env)).status, 403);
    const expired = await new SignJWT({}).setProtectedHeader({ alg: 'RS256', kid: 'test-key' })
      .setIssuer('https://example.cloudflareaccess.com').setAudience(env.ACCESS_AUDIENCE)
      .setExpirationTime('1s ago').sign(privateKey);
    assert.equal((await worker.fetch(dashboardRequest(expired), env)).status, 403);
    const noExpiry = await new SignJWT({}).setProtectedHeader({ alg: 'RS256', kid: 'test-key' })
      .setIssuer('https://example.cloudflareaccess.com').setAudience(env.ACCESS_AUDIENCE).sign(privateKey);
    assert.equal((await worker.fetch(dashboardRequest(noExpiry), env)).status, 403);
    const result = await worker.fetch(dashboardRequest(await makeToken(env.ACCESS_AUDIENCE)), env);
    assert.equal(result.status, 200);
    const stats = await result.json();
    assert.deepEqual(stats['7'], [
      { kind: 'app', version: '0.3.0', count: 1 },
      { kind: 'collection', version: '0.3.0', count: 1 },
    ]);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('scheduled cleanup removes only records older than 30 days', async () => {
  const env = environment();
  const insert = env.sql.prepare('INSERT INTO activity VALUES (?, ?, ?, ?, ?)');
  insert.run(id, '2026-01-01', 'app', '1.0.0', Date.now() - 31 * 86400000);
  insert.run(id, '2026-01-02', 'app', '1.0.0', Date.now() - 29 * 86400000);
  await worker.scheduled({}, env);
  assert.equal(env.sql.prepare('SELECT COUNT(*) AS count FROM activity').get().count, 1);
});
