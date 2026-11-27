import test from "node:test";
import assert from "node:assert/strict";
import worker from "../src/worker.mjs";

const api = "https://roundup.example.test";
const validId = "6f7c8d35-2a43-4af5-9bb7-b12f2256d112";
const managerId = "8e147afa-3bb3-497d-a91a-8faf2a5a32e7";
const secret = "test-only-setup-secret-please-do-not-reuse-in-production";
function makeRequest(path, method = "GET", json, extraHeaders = {}) {
  const headers = { ...extraHeaders };
  if (json !== undefined) headers["content-type"] = "application/json";
  return new Request(api + path, { method, headers, body: json === undefined ? undefined : JSON.stringify(json) });
}
function mockDb(user = { id: managerId, display_name: "Олег", role: "manager" }, opts = {}) {
  const calls = [];
  const db = {
    calls,
    prepare(sql) {
      const item = {
        sql, args: [],
        bind(...args) { item.args = args; return item; },
        async first() {
          if (sql.includes("FROM api_tokens")) return user;
          if (sql.includes("SELECT o.* FROM objects o WHERE o.id=?")) return opts.visibleObject ?? null;
          if (sql.includes("SELECT can_edit FROM object_access")) return opts.canEdit ?? null;
          if (sql.includes("SELECT id,created_by FROM objects")) return opts.existingObject ?? null;
          if (sql.includes("SELECT id FROM objects WHERE id")) return opts.objectExists ?? null;
          if (sql.includes("SELECT manager_id FROM visits")) return null;
          if (sql.includes("SELECT sender_id FROM messages")) return null;
          return null;
        },
        async all() {
          if (sql.includes("FROM users WHERE id IN")) return { results: opts.knownUsers ?? [] };
          return { results: [] };
        },
        async run() { return { meta: { changes: opts.updateChanges ?? 1 } }; }
      };
      calls.push(item);
      return item;
    },
    async batch(statements) { calls.push({ batch: statements.length }); return statements.map(() => ({ success: true })); }
  };
  return db;
}
async function jsonResponse(request, db, env = {}) {
  const response = await worker.fetch(request, { DB: db, ...env });
  return { status: response.status, body: await response.json() };
}
const bearer = { authorization: "Bearer ru_" + "a".repeat(64) };

test("public health endpoint does not depend on D1 availability", async () => {
  const { status, body } = await jsonResponse(makeRequest("/v1/health"), undefined);
  assert.equal(status, 200);
  assert.equal(body.status, "ok");
});
test("database must be configured; silently running without D1 is forbidden", async () => {
  const { status } = await jsonResponse(makeRequest("/v1/objects"), undefined);
  assert.equal(status, 503);
});
test("requests without a valid bearer token are rejected", async () => {
  const db = mockDb();
  const a = await jsonResponse(makeRequest("/v1/objects"), db);
  const b = await jsonResponse(makeRequest("/v1/objects", "GET", undefined, { authorization: "Bearer bogus" }), db);
  assert.equal(a.status, 401);
  assert.equal(b.status, 401);
});
test("manager cannot grant or revoke access by direct API call", async () => {
  const db = mockDb();
  const result = await jsonResponse(makeRequest("/v1/objects/" + validId + "/assignments", "PUT", { userIds: [managerId] }, bearer), db);
  assert.equal(result.status, 403);
  assert.equal(db.calls.some(c => c.sql?.includes("DELETE FROM object_access")), false);
});
test("unassigned object cannot be fetched by another manager", async () => {
  const db = mockDb();
  const result = await jsonResponse(makeRequest("/v1/objects/" + validId, "GET", undefined, bearer), db);
  assert.equal(result.status, 404);
  assert.ok(db.calls.some(c => c.sql?.includes("object_access") && c.sql?.includes("created_by")));
});
test("manager object list is filtered in SQL, not only UI", async () => {
  const db = mockDb();
  const result = await jsonResponse(makeRequest("/v1/objects", "GET", undefined, bearer), db);
  assert.equal(result.status, 200);
  const query = db.calls.find(c => c.sql?.startsWith("SELECT o.* FROM objects o WHERE o.id>?"));
  assert.ok(query);
  assert.match(query.sql, /EXISTS \(SELECT 1 FROM object_access/);
  assert.deepEqual(query.args.slice(1), ["manager", managerId, managerId]);
});
test("read-only assignment cannot edit", async () => {
  const db = mockDb(undefined, { visibleObject: { id: validId, status: "assigned", created_by: "someone-else", title: "Об'єкт", lat: 49.8, lon: 24, revision: 1 }, canEdit: { can_edit: 0 } });
  const result = await jsonResponse(makeRequest("/v1/objects/" + validId, "PATCH", { expectedRevision: 1, title: "Спроба" }, bearer), db);
  assert.equal(result.status, 403);
});
test("outdated object revision is rejected instead of overwriting teammates", async () => {
  const db = mockDb(undefined, { visibleObject: { id: validId, status: "pending", created_by: managerId, title: "Назва", lat: 49.8, lon: 24, revision: 2 }, updateChanges: 0 });
  const result = await jsonResponse(makeRequest("/v1/objects/" + validId, "PATCH", { expectedRevision: 1, title: "Нова назва" }, bearer), db);
  assert.equal(result.status, 409);
});
test("new objects from a manager start pending until director assigns access", async () => {
  const db = mockDb();
  const result = await jsonResponse(makeRequest("/v1/objects", "POST", { id: validId, title: "Склад Львів", lat: 49.84, lon: 24.03 }, bearer), db);
  assert.equal(result.status, 201);
  assert.equal(result.body.status, "pending");
  const insert = db.calls.find(c => c.sql?.startsWith("INSERT INTO objects("));
  assert.ok(insert.sql.includes("'pending'"));
});
test("client-provided object IDs support idempotent offline retries", async () => {
  const db = mockDb(undefined, { existingObject: { id: validId, created_by: managerId } });
  const result = await jsonResponse(makeRequest("/v1/objects", "POST", { id: validId, title: "Склад Львів" }, bearer), db);
  assert.equal(result.status, 200);
  assert.equal(result.body.alreadyExists, true);
});
test("setup cannot run without a strong configured secret", async () => {
  const db = mockDb();
  const result = await jsonResponse(makeRequest("/v1/setup", "POST", { displayName: "Директор" }), db);
  assert.equal(result.status, 503);
});
test("bootstrap accepts correct secret exactly once via database guard", async () => {
  const db = mockDb();
  const denied = await jsonResponse(makeRequest("/v1/setup", "POST", { displayName: "Директор" }, { "x-roundup-setup-secret": "wrong" }), db, { ROUNDUP_SETUP_SECRET: secret });
  assert.equal(denied.status, 403);
  const ok = await jsonResponse(makeRequest("/v1/setup", "POST", { displayName: "Директор" }, { "x-roundup-setup-secret": secret }), db, { ROUNDUP_SETUP_SECRET: secret });
  assert.equal(ok.status, 201);
  assert.match(ok.body.apiToken, /^ru_[a-f0-9]{64}$/);
  assert.ok(db.calls.some(c => c.sql?.includes("setup_guard")));
});
test("API rejects browser origins that were not explicitly allowed", async () => {
  const db = mockDb();
  const result = await jsonResponse(makeRequest("/v1/objects", "GET", undefined, { ...bearer, origin: "https://evil.example" }), db);
  assert.equal(result.status, 403);
});
test("manager cannot open another object's chat", async () => {
  const db = mockDb();
  const result = await jsonResponse(makeRequest("/v1/objects/" + validId + "/messages", "GET", undefined, bearer), db);
  assert.equal(result.status, 404);
});
