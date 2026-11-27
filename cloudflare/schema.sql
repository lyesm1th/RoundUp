-- RoundUp Cloudflare D1 schema (run once before starting the Worker).
-- Original local GeoZamir/ZIP database remains untouched.

CREATE TABLE IF NOT EXISTS setup_guard (
  id INTEGER PRIMARY KEY CHECK (id = 1)
);

CREATE TABLE IF NOT EXISTS users (
  id TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  role TEXT NOT NULL CHECK (role IN ('admin', 'manager')),
  active INTEGER NOT NULL DEFAULT 1 CHECK (active IN (0, 1)),
  created_by TEXT REFERENCES users(id),
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS api_tokens (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token_hash TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL,
  revoked_at TEXT
);
CREATE INDEX IF NOT EXISTS idx_tokens_user ON api_tokens(user_id);

CREATE TABLE IF NOT EXISTS objects (
  id TEXT PRIMARY KEY,
  project_id TEXT NOT NULL DEFAULT 'default',
  title TEXT NOT NULL,
  lat REAL,
  lon REAL,
  status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'assigned')),
  created_by TEXT NOT NULL REFERENCES users(id),
  revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_objects_project ON objects(project_id);
CREATE INDEX IF NOT EXISTS idx_objects_author_status ON objects(created_by, status);

-- Server-side ACL. No access to objects assigned to other managers.
CREATE TABLE IF NOT EXISTS object_access (
  object_id TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
  user_id TEXT NOT NULL REFERENCES users(id),
  can_edit INTEGER NOT NULL DEFAULT 1 CHECK (can_edit IN (0, 1)),
  assigned_by TEXT NOT NULL REFERENCES users(id),
  assigned_at TEXT NOT NULL,
  PRIMARY KEY(object_id, user_id)
);
CREATE INDEX IF NOT EXISTS idx_object_access_user ON object_access(user_id, object_id);

CREATE TABLE IF NOT EXISTS visits (
  id TEXT PRIMARY KEY,
  object_id TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
  manager_id TEXT NOT NULL REFERENCES users(id),
  visit_date TEXT NOT NULL,
  note TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_visits_object_day ON visits(object_id, visit_date DESC);

CREATE TABLE IF NOT EXISTS messages (
  id TEXT PRIMARY KEY,
  object_id TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
  sender_id TEXT NOT NULL REFERENCES users(id),
  body TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_messages_object_date ON messages(object_id, created_at DESC, id);
CREATE INDEX IF NOT EXISTS idx_messages_date ON messages(created_at, id);

-- Append-only change log. Author and date are not editable by managers.
CREATE TABLE IF NOT EXISTS activity (
  id TEXT PRIMARY KEY,
  object_id TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
  actor_id TEXT NOT NULL REFERENCES users(id),
  kind TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_activity_object ON activity(object_id, created_at DESC);
