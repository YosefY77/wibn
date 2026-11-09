-- Harmoni Hub (Cloudflare D1) — الحسابات المشتركة بين كل نسخ هارموني
CREATE TABLE IF NOT EXISTS accounts (
  uid      TEXT PRIMARY KEY,
  username TEXT NOT NULL UNIQUE,   -- lower-case, without @
  salt     TEXT NOT NULL,
  iter     INTEGER NOT NULL,
  vh       TEXT NOT NULL,          -- sha256(pbkdf2 hash) — the hub never sees passwords
  profile  TEXT NOT NULL,          -- public: name, avatar, banner, bio, colors, likes, following
  data     TEXT NOT NULL,          -- private: playlists, history, stats, taste
  score    REAL NOT NULL DEFAULT 0,
  updated  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS accounts_score ON accounts(score);
CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT NOT NULL);
-- غرف الاستماع المشتركة (الغرفة نفسها Durable Object؛ هذا الجدول بس لقائمة الغرف)
CREATE TABLE IF NOT EXISTS rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, owner TEXT NOT NULL, members INTEGER NOT NULL, max_members INTEGER NOT NULL, has_password INTEGER NOT NULL, can_add INTEGER NOT NULL, updated INTEGER NOT NULL);
