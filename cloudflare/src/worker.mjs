// RoundUp shared workspace API. Independent of the original GeoZamir process.
const MAX_BODY = 64 * 1024;
const DAY_MS = 86400000;
const RE_UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}
const bad = (status, message) => { throw new ApiError(status, message); };
const now = () => new Date().toISOString();
const respond = (value, status = 200) => new Response(JSON.stringify(value), {
  status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", "x-content-type-options": "nosniff" }
});
const fail = error => respond({ error: error instanceof ApiError ? error.message : "Внутрішня помилка сервера" }, error instanceof ApiError ? error.status : 500);
const isId = value => typeof value === "string" && RE_UUID.test(value);
const requiredId = value => isId(value) ? value : bad(400, "Некоректний ідентифікатор");
const str = (value, max = 200) => typeof value === "string" ? value.trim().slice(0, max) : "";
const randomId = () => crypto.randomUUID();
function hex(array) { return [...array].map(x => x.toString(16).padStart(2, "0")).join(""); }
function randomToken() { const bytes = crypto.getRandomValues(new Uint8Array(32)); return "ru_" + hex(bytes); }
async function tokenHash(token) {
  return hex(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(token))));
}
function constantTimeEqual(a, b) {
  const x = new TextEncoder().encode(String(a || ""));
  const y = new TextEncoder().encode(String(b || ""));
  let diff = x.length ^ y.length;
  for (let i = 0; i < Math.max(x.length, y.length); i++) diff |= (x[i] || 0) ^ (y[i] || 0);
  return diff === 0;
}
async function inputJSON(request) {
  if (Number(request.headers.get("content-length") || 0) > MAX_BODY) bad(413, "Завеликий запит");
  const body = await request.text();
  if (new TextEncoder().encode(body).length > MAX_BODY) bad(413, "Завеликий запит");
  try {
    const parsed = JSON.parse(body);
    if (!parsed || Array.isArray(parsed) || typeof parsed !== "object") bad(400, "Очікується JSON-об'єкт");
    return parsed;
  } catch (error) {
    if (error instanceof ApiError) throw error;
    bad(400, "Некоректний JSON");
  }
}
function coordinates(data) {
  const lat = data.lat == null ? null : Number(data.lat);
  const lon = data.lon == null ? null : Number(data.lon);
  if ((lat == null) !== (lon == null)) bad(400, "Координати мають містити широту й довготу");
  if (lat != null && (!Number.isFinite(lat) || !Number.isFinite(lon) || Math.abs(lat) > 90 || Math.abs(lon) > 180)) {
    bad(400, "Некоректні координати");
  }
  return { lat, lon };
}
async function auth(request, db) {
  const header = request.headers.get("authorization") || "";
  const match = /^Bearer (ru_[a-f0-9]{64})$/.exec(header);
  if (!match) bad(401, "Потрібна авторизація");
  const digest = await tokenHash(match[1]);
  const user = await db.prepare("SELECT u.id,u.display_name,u.role FROM api_tokens t JOIN users u ON u.id=t.user_id WHERE t.token_hash=? AND t.revoked_at IS NULL AND u.active=1").bind(digest).first();
  if (!user) bad(401, "Недійсний або відкликаний ключ доступу");
  return user;
}
function adminOnly(user) { if (user.role !== "admin") bad(403, "Потрібні права керівника"); }
function objectVisibilitySql() {
  return "(? = 'admin' OR (o.status='pending' AND o.created_by=?) OR EXISTS (SELECT 1 FROM object_access a WHERE a.object_id=o.id AND a.user_id=?))";
}
async function visibleObject(db, user, id) {
  const query = "SELECT o.* FROM objects o WHERE o.id=? AND " + objectVisibilitySql();
  const item = await db.prepare(query).bind(id, user.role, user.id, user.id).first();
  if (!item) bad(404, "Об'єкт не знайдено або доступ заборонено");
  return item;
}
async function editingObject(db, user, id) {
  const item = await visibleObject(db, user, id);
  if (user.role !== "admin" && !(item.status === "pending" && item.created_by === user.id)) {
    const access = await db.prepare("SELECT can_edit FROM object_access WHERE object_id=? AND user_id=?").bind(id, user.id).first();
    if (!access?.can_edit) bad(403, "Немає прав на редагування об'єкта");
  }
  return item;
}
async function audit(db, objectId, actorId, kind) {
  await db.prepare("INSERT INTO activity(id,object_id,actor_id,kind,created_at) VALUES(?,?,?,?,?)").bind(randomId(), objectId, actorId, kind, now()).run();
}
async function bootstrap(request, env) {
  if (!env.ROUNDUP_SETUP_SECRET || env.ROUNDUP_SETUP_SECRET.length < 32) bad(503, "Налаштуйте ROUNDUP_SETUP_SECRET (мінімум 32 символи)");
  if (!constantTimeEqual(request.headers.get("x-roundup-setup-secret"), env.ROUNDUP_SETUP_SECRET)) bad(403, "Недійсний секрет ініціалізації");
  const data = await inputJSON(request);
  const userId = randomId(), secret = randomToken(), date = now();
  // setup_guard has one row at most; D1 batch rolls back everything if already bootstrapped.
  try {
    await env.DB.batch([
      env.DB.prepare("INSERT INTO setup_guard (id) VALUES (1)"),
      env.DB.prepare("INSERT INTO users(id,display_name,role,created_at) VALUES(?,?,'admin',?)").bind(userId, str(data.displayName, 100) || "Керівник", date),
      env.DB.prepare("INSERT INTO api_tokens(id,user_id,token_hash,created_at) VALUES(?,?,?,?)").bind(randomId(), userId, await tokenHash(secret), date)
    ]);
  } catch (_) { bad(409, "Систему вже ініціалізовано"); }
  return respond({ userId, apiToken: secret, warning: "Збережіть ключ зараз: сервер більше його не покаже" }, 201);
}
async function listUsers(db) {
  const { results } = await db.prepare("SELECT id,display_name,role,active,created_at FROM users ORDER BY created_at").all();
  return respond({ users: results || [] });
}
async function createUser(db, request, actor) {
  const data = await inputJSON(request);
  const name = str(data.displayName, 100);
  if (name.length < 2) bad(400, "Вкажіть ім'я менеджера");
  if (data.role && data.role !== "manager") bad(400, "Керівник створює лише профілі менеджерів");
  const id = randomId(), secret = randomToken(), date = now();
  await db.batch([
    db.prepare("INSERT INTO users(id,display_name,role,created_by,created_at) VALUES(?,?,'manager',?,?)").bind(id, name, actor.id, date),
    db.prepare("INSERT INTO api_tokens(id,user_id,token_hash,created_at) VALUES(?,?,?,?)").bind(randomId(), id, await tokenHash(secret), date)
  ]);
  return respond({ user: { id, displayName: name, role: "manager" }, apiToken: secret, warning: "Ключ показується лише один раз" }, 201);
}
async function setUserActive(db, request, id) {
  const data = await inputJSON(request);
  if (typeof data.active !== "boolean") bad(400, "Вкажіть active: true або false");
  const result = await db.prepare("UPDATE users SET active=? WHERE id=? AND role='manager'").bind(data.active ? 1 : 0, id).run();
  if (!result.meta.changes) bad(404, "Менеджера не знайдено");
  return respond({ ok: true });
}
async function rotateUserToken(db, id) {
  const target = await db.prepare("SELECT role FROM users WHERE id=? AND active=1").bind(id).first();
  if (!target || target.role !== "manager") bad(404, "Активного менеджера не знайдено");
  const token = randomToken(), date = now();
  await db.batch([
    db.prepare("UPDATE api_tokens SET revoked_at=? WHERE user_id=? AND revoked_at IS NULL").bind(date, id),
    db.prepare("INSERT INTO api_tokens(id,user_id,token_hash,created_at) VALUES(?,?,?,?)").bind(randomId(), id, await tokenHash(token), date)
  ]);
  return respond({ apiToken: token, warning: "Старі ключі відкликані; новий показується один раз" });
}
async function listObjects(db, user, url) {
  const after = url.searchParams.get("after") || "";
  if (after && !isId(after)) bad(400, "Некоректний курсор сторінки");
  const sql = "SELECT o.* FROM objects o WHERE o.id>? AND " + objectVisibilitySql() + " ORDER BY o.id LIMIT 201";
  const { results = [] } = await db.prepare(sql).bind(after, user.role, user.id, user.id).all();
  const items = results.slice(0, 200);
  return respond({ objects: items, nextAfter: results.length > 200 ? items[items.length - 1].id : null });
}
async function createObject(db, request, user) {
  const data = await inputJSON(request);
  const id = data.id ? requiredId(data.id) : randomId();
  const title = str(data.title, 160);
  if (title.length < 2) bad(400, "Вкажіть назву об'єкта");
  const projectId = str(data.projectId || "default", 100);
  if (!/^[\w.:/-]{1,100}$/.test(projectId)) bad(400, "Некоректний ідентифікатор проєкту");
  const { lat, lon } = coordinates(data);
  const existing = await db.prepare("SELECT id,created_by FROM objects WHERE id=?").bind(id).first();
  if (existing) {
    if (existing.created_by === user.id) return respond({ objectId: id, alreadyExists: true }, 200);
    bad(409, "Ідентифікатор об'єкта вже існує");
  }
  const date = now();
  await db.batch([
    db.prepare("INSERT INTO objects(id,project_id,title,lat,lon,status,created_by,created_at,updated_at) VALUES(?,?,?,?,?,'pending',?,?,?)").bind(id, projectId, title, lat, lon, user.id, date, date),
    db.prepare("INSERT INTO activity(id,object_id,actor_id,kind,created_at) VALUES(?,?,?,'created',?)").bind(randomId(), id, user.id, date)
  ]);
  return respond({ objectId: id, status: "pending", revision: 1 }, 201);
}
async function patchObject(db, request, user, id) {
  const item = await editingObject(db, user, id);
  const data = await inputJSON(request);
  if (!Number.isInteger(data.expectedRevision) || data.expectedRevision < 1) bad(400, "Потрібна expectedRevision");
  const title = data.title === undefined ? item.title : str(data.title, 160);
  if (title.length < 2) bad(400, "Некоректна назва");
  const coords = data.lat === undefined && data.lon === undefined
    ? { lat: item.lat, lon: item.lon } : coordinates(data);
  const date = now();
  const result = await db.prepare("UPDATE objects SET title=?,lat=?,lon=?,revision=revision+1,updated_at=? WHERE id=? AND revision=?").bind(
    title, coords.lat, coords.lon, date, id, data.expectedRevision).run();
  if (!result.meta.changes) bad(409, "Об'єкт змінено на іншому пристрої. Оновіть дані перед повторним редагуванням.");
  await audit(db, id, user.id, "updated");
  return respond({ objectId: id, revision: data.expectedRevision + 1 });
}
async function setAssignments(db, request, actor, id) {
  const obj = await db.prepare("SELECT id FROM objects WHERE id=?").bind(id).first();
  if (!obj) bad(404, "Об'єкт не знайдено");
  const data = await inputJSON(request);
  if (!Array.isArray(data.userIds) || data.userIds.length > 50 ||
      data.userIds.some(x => !isId(x)) || new Set(data.userIds).size !== data.userIds.length) bad(400, "Некоректний список менеджерів");
  if (data.userIds.length) {
    const placeholders = data.userIds.map(() => "?").join(",");
    const { results = [] } = await db.prepare("SELECT id FROM users WHERE id IN (" + placeholders + ") AND role='manager' AND active=1").bind(...data.userIds).all();
    if (results.length !== data.userIds.length) bad(400, "Деякі менеджери не існують або заблоковані");
  }
  const date = now();
  const statements = [
    db.prepare("DELETE FROM object_access WHERE object_id=?").bind(id),
    db.prepare("UPDATE objects SET status='assigned',revision=revision+1,updated_at=? WHERE id=?").bind(date, id)
  ];
  for (const uid of data.userIds) statements.push(db.prepare("INSERT INTO object_access(object_id,user_id,can_edit,assigned_by,assigned_at) VALUES(?,?,1,?,?)").bind(id, uid, actor.id, date));
  statements.push(db.prepare("INSERT INTO activity(id,object_id,actor_id,kind,created_at) VALUES(?,?,?,'assignments_changed',?)").bind(randomId(), id, actor.id, date));
  await db.batch(statements);
  return respond({ objectId: id, userIds: data.userIds });
}
async function getAssignments(db, id) {
  const { results = [] } = await db.prepare("SELECT u.id,u.display_name,a.can_edit,a.assigned_at FROM object_access a JOIN users u ON a.user_id=u.id WHERE a.object_id=? ORDER BY u.display_name").bind(id).all();
  return respond({ assignments: results });
}
async function listVisits(db, id) {
  const { results = [] } = await db.prepare("SELECT v.*,u.display_name AS manager_name FROM visits v JOIN users u ON u.id=v.manager_id WHERE v.object_id=? ORDER BY v.visit_date DESC, v.created_at DESC LIMIT 200").bind(id).all();
  return respond({ visits: results });
}
async function addVisit(db, request, user, id) {
  const data = await inputJSON(request);
  const day = String(data.visitDate || "");
  if (!/^\d{4}-\d{2}-\d{2}$/.test(day) || Number.isNaN(Date.parse(day + "T00:00:00Z")) || new Date(day + "T00:00:00Z").toISOString().slice(0, 10) !== day) bad(400, "Некоректна дата виїзду");
  const idempotentId = data.id ? requiredId(data.id) : randomId();
  const note = str(data.note, 4000);
  const present = await db.prepare("SELECT manager_id FROM visits WHERE id=?").bind(idempotentId).first();
  if (present) {
    if (present.manager_id === user.id) return respond({ visitId: idempotentId, alreadyExists: true });
    bad(409, "Ідентифікатор виїзду вже використовується");
  }
  const date = now();
  await db.batch([
    db.prepare("INSERT INTO visits(id,object_id,manager_id,visit_date,note,created_at) VALUES(?,?,?,?,?,?)").bind(idempotentId, id, user.id, day, note, date),
    db.prepare("INSERT INTO activity(id,object_id,actor_id,kind,created_at) VALUES(?,?,?,'visit_added',?)").bind(randomId(), id, user.id, date)
  ]);
  return respond({ visitId: idempotentId }, 201);
}
async function listMessages(db, id) {
  const { results = [] } = await db.prepare("SELECT m.id,m.body,m.created_at,m.sender_id,u.display_name AS sender_name FROM messages m JOIN users u ON u.id=m.sender_id WHERE m.object_id=? ORDER BY m.created_at DESC,m.id DESC LIMIT 100").bind(id).all();
  return respond({ messages: results.reverse() });
}
async function addMessage(db, request, user, id) {
  const data = await inputJSON(request), body = str(data.body, 2500);
  if (!body) bad(400, "Порожнє повідомлення");
  const messageId = data.id ? requiredId(data.id) : randomId();
  const seen = await db.prepare("SELECT sender_id FROM messages WHERE id=?").bind(messageId).first();
  if (seen) {
    if (seen.sender_id === user.id) return respond({ messageId, alreadyExists: true });
    bad(409, "Ідентифікатор повідомлення вже існує");
  }
  const date = now();
  await db.batch([
    db.prepare("INSERT INTO messages(id,object_id,sender_id,body,created_at) VALUES(?,?,?,?,?)").bind(messageId, id, user.id, body, date),
    db.prepare("INSERT INTO activity(id,object_id,actor_id,kind,created_at) VALUES(?,?,?,'message_sent',?)").bind(randomId(), id, user.id, date)
  ]);
  return respond({ messageId }, 201);
}
async function listNotifications(db, user, url) {
  const after = url.searchParams.get("after") || new Date(Date.now() - 7 * DAY_MS).toISOString();
  if (Number.isNaN(Date.parse(after))) bad(400, "Некоректна дата");
  const afterId = url.searchParams.get("afterId") || "";
  if (afterId && !isId(afterId)) bad(400, "Некоректний курсор сповіщень");
  const sql = "SELECT m.id,m.object_id,m.sender_id,m.body,m.created_at,o.title FROM messages m JOIN objects o ON o.id=m.object_id WHERE (m.created_at>? OR (m.created_at=? AND m.id>?)) AND m.sender_id<>? AND " + objectVisibilitySql() + " ORDER BY m.created_at,m.id LIMIT 100";
  const { results = [] } = await db.prepare(sql).bind(after, after, afterId, user.id, user.role, user.id, user.id).all();
  return respond({ notifications: results, nextAfter: results.length ? results[results.length - 1].created_at : after, nextAfterId: results.length ? results[results.length - 1].id : afterId });
}
async function listActivity(db, id) {
  const { results = [] } = await db.prepare("SELECT a.id,a.kind,a.actor_id,a.created_at,u.display_name AS actor_name FROM activity a JOIN users u ON u.id=a.actor_id WHERE a.object_id=? ORDER BY a.created_at DESC LIMIT 200").bind(id).all();
  return respond({ history: results });
}
async function route(request, env) {
  const url = new URL(request.url);
  const path = url.pathname.split("/").filter(Boolean);
  const method = request.method.toUpperCase();
  if (method === "GET" && url.pathname === "/v1/health") return respond({ status: "ok", service: "RoundUp Workspace API" });
  if (!env.DB) bad(503, "D1 DB не налаштована");
  // Native .NET clients send no Origin. Browser requests are denied unless explicitly configured.
  const origin = request.headers.get("origin");
  if (origin && origin !== env.ROUNDUP_UI_ORIGIN) bad(403, "Недозволене джерело запиту");
  if (method === "POST" && url.pathname === "/v1/setup") return bootstrap(request, env);
  const db = env.DB, user = await auth(request, db);
  if (method === "GET" && url.pathname === "/v1/me") return respond({ user });
  if (path.join("/") === "v1/users") {
    adminOnly(user);
    if (method === "GET") return listUsers(db);
    if (method === "POST") return createUser(db, request, user);
  }
  if (path[0] === "v1" && path[1] === "users" && path.length >= 3) {
    adminOnly(user);
    const id = requiredId(path[2]);
    if (path.length === 3 && method === "PATCH") return setUserActive(db, request, id);
    if (path[3] === "rotate-token" && path.length === 4 && method === "POST") return rotateUserToken(db, id);
  }
  if (path.join("/") === "v1/objects") {
    if (method === "GET") return listObjects(db, user, url);
    if (method === "POST") return createObject(db, request, user);
  }
  if (path[0] === "v1" && path[1] === "objects" && path.length >= 3) {
    const id = requiredId(path[2]);
    if (path.length === 3) {
      if (method === "GET") return respond({ object: await visibleObject(db, user, id) });
      if (method === "PATCH") return patchObject(db, request, user, id);
    }
    if (path.length === 4 && path[3] === "assignments") {
      if (method === "PUT") { adminOnly(user); return setAssignments(db, request, user, id); }
      if (method === "GET") { await visibleObject(db, user, id); return getAssignments(db, id); }
    }
    if (path.length === 4 && path[3] === "visits") {
      if (method === "GET") { await visibleObject(db, user, id); return listVisits(db, id); }
      if (method === "POST") { await editingObject(db, user, id); return addVisit(db, request, user, id); }
    }
    if (path.length === 4 && path[3] === "messages") {
      if (method === "GET") { await visibleObject(db, user, id); return listMessages(db, id); }
      if (method === "POST") { await visibleObject(db, user, id); return addMessage(db, request, user, id); }
    }
    if (path.length === 4 && path[3] === "history" && method === "GET") {
      await visibleObject(db, user, id); return listActivity(db, id);
    }
  }
  if (method === "GET" && url.pathname === "/v1/notifications") return listNotifications(db, user, url);
  bad(404, "Невідомий запит");
}
export default {
  async fetch(request, env) {
    try { return await route(request, env); }
    catch (error) { if (!(error instanceof ApiError)) console.error("RoundUp workspace API error", error); return fail(error); }
  }
};
