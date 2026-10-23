-- app.db v1 (ARCHITECTURE §9.2). journal_mode / foreign_keys are applied per connection by SqliteConnectionFactory,
-- because PRAGMA journal_mode cannot change inside the migration transaction.

-- Tags --------------------------------------------------------------------
CREATE TABLE tags (
    tag_id   INTEGER PRIMARY KEY,
    name     TEXT NOT NULL UNIQUE COLLATE NOCASE,
    color    TEXT NOT NULL DEFAULT '#808080'
);
CREATE TABLE file_tags (
    tag_id         INTEGER NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
    file_key       TEXT    NOT NULL,   -- 'ntfs:<volSerialHex>:<fileIdHex128>' or 'path:<normalized full path>'
    last_path      TEXT    NOT NULL COLLATE NOCASE,
    tagged_utc     INTEGER NOT NULL,
    PRIMARY KEY (tag_id, file_key)
) WITHOUT ROWID;
CREATE INDEX ix_file_tags_key ON file_tags(file_key);

-- Search categories, saved filters, column layouts -------------------------
CREATE TABLE column_layouts (
    layout_id     INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE,
    columns_json  TEXT NOT NULL        -- [{"id":"name","width":320,"visible":true}, ...]
);
CREATE TABLE saved_filters (
    filter_id    INTEGER PRIMARY KEY,
    name         TEXT NOT NULL UNIQUE,
    chain_json   TEXT NOT NULL,        -- ordered list of serialized ASTs, applied as successive refinements
    created_utc  INTEGER NOT NULL,
    updated_utc  INTEGER NOT NULL
);
CREATE TABLE search_categories (
    category_id   INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE,
    icon          TEXT,                -- glyph id from the icon set
    query_text    TEXT NOT NULL,       -- canonical printed query
    ast_json      TEXT NOT NULL,       -- canonical AST
    filter_id     INTEGER REFERENCES saved_filters(filter_id) ON DELETE SET NULL,
    layout_id     INTEGER REFERENCES column_layouts(layout_id) ON DELETE SET NULL,
    sort_spec     TEXT NOT NULL DEFAULT 'name:asc',
    scope_json    TEXT NOT NULL DEFAULT '[]', -- volume roots / folders; [] = all indexed
    sort_order    INTEGER NOT NULL DEFAULT 0,
    created_utc   INTEGER NOT NULL,
    updated_utc   INTEGER NOT NULL
);

-- Sessions / layouts --------------------------------------------------------
CREATE TABLE sessions (
    session_id    INTEGER PRIMARY KEY,
    name          TEXT NOT NULL,
    is_autosave   INTEGER NOT NULL DEFAULT 0,
    window_json   TEXT NOT NULL,       -- placement, monitor, maximized, pane orientation, link mode, splitter ratio
    dock_layout   TEXT,                -- AvalonDock XmlLayoutSerializer output
    created_utc   INTEGER NOT NULL,
    updated_utc   INTEGER NOT NULL
);
CREATE UNIQUE INDEX ux_sessions_autosave ON sessions(is_autosave) WHERE is_autosave = 1;
CREATE TABLE session_tabs (
    session_id     INTEGER NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    pane_index     INTEGER NOT NULL,   -- 0 left/top, 1 right/bottom
    tab_index      INTEGER NOT NULL,
    location       TEXT    NOT NULL,   -- VfsPath string
    title          TEXT,
    view_mode      INTEGER NOT NULL DEFAULT 0, -- 0 details, 1 list, 2 tiles, 3 thumbnails
    sort_spec      TEXT    NOT NULL DEFAULT 'name:asc',
    layout_id      INTEGER REFERENCES column_layouts(layout_id) ON DELETE SET NULL,
    query_text     TEXT,               -- non-null for search tabs
    is_active      INTEGER NOT NULL DEFAULT 0,
    is_locked      INTEGER NOT NULL DEFAULT 0, -- locked: navigation opens a new tab
    is_pinned      INTEGER NOT NULL DEFAULT 0, -- pinned: compact, left-most, not closable by Close-All
    history_json   TEXT    NOT NULL DEFAULT '{"back":[],"forward":[]}',
    PRIMARY KEY (session_id, pane_index, tab_index)
) WITHOUT ROWID;

-- Network -------------------------------------------------------------------
CREATE TABLE network_connections (
    connection_id          TEXT PRIMARY KEY,   -- GUID
    kind                   INTEGER NOT NULL,   -- 3 SMB, 4 FTP, 5 FTPS, 6 SFTP, 7 WebDAV
    display_name           TEXT NOT NULL,
    host                   TEXT NOT NULL,
    port                   INTEGER,
    root_path              TEXT NOT NULL DEFAULT '/',
    username               TEXT,
    credential_target      TEXT,               -- Credential Locker reference 'NexPane:net/<connection_id>'; secret never stored here
    options_json           TEXT NOT NULL DEFAULT '{}', -- passive mode, TLS mode, encoding, auth method, key file path
    crawl_enabled          INTEGER NOT NULL DEFAULT 0,
    crawl_interval_minutes INTEGER NOT NULL DEFAULT 360,
    created_utc            INTEGER NOT NULL
);
CREATE TABLE ssh_known_hosts (
    host            TEXT NOT NULL,
    port            INTEGER NOT NULL,
    key_type        TEXT NOT NULL,     -- 'ssh-ed25519', 'ecdsa-sha2-nistp256', 'rsa-sha2-512', ...
    fingerprint     TEXT NOT NULL,     -- 'SHA256:base64'
    first_seen_utc  INTEGER NOT NULL,
    PRIMARY KEY (host, port, key_type)
) WITHOUT ROWID;
CREATE TABLE tls_pins (
    host           TEXT NOT NULL,
    port           INTEGER NOT NULL,
    cert_sha256    TEXT NOT NULL,
    accepted_utc   INTEGER NOT NULL,
    PRIMARY KEY (host, port)
) WITHOUT ROWID;

-- Sync ------------------------------------------------------------------------
CREATE TABLE sync_pairs (
    pair_id          INTEGER PRIMARY KEY,
    name             TEXT NOT NULL,
    left_location    TEXT NOT NULL,     -- VfsPath
    right_location   TEXT NOT NULL,
    direction        INTEGER NOT NULL,  -- 0 left→right mirror, 1 left→right update, 2 right→left mirror, 3 right→left update, 4 two-way
    compare_mode     INTEGER NOT NULL,  -- bit flags: 1 timestamp, 2 size, 4 sha256
    time_tolerance_s INTEGER NOT NULL DEFAULT 2,
    conflict_policy  INTEGER NOT NULL DEFAULT 0, -- 0 ask, 1 newer wins, 2 left wins, 3 right wins, 4 keep both, 5 skip
    filters_json     TEXT NOT NULL DEFAULT '{"include":[],"exclude":[]}',
    delete_to_recycle INTEGER NOT NULL DEFAULT 1,
    last_run_utc     INTEGER
);
CREATE TABLE sync_baseline (
    pair_id        INTEGER NOT NULL REFERENCES sync_pairs(pair_id) ON DELETE CASCADE,
    rel_path       TEXT    NOT NULL COLLATE NOCASE,
    is_dir         INTEGER NOT NULL,
    size           INTEGER,
    modified_utc   INTEGER,
    sha256         BLOB,
    PRIMARY KEY (pair_id, rel_path)
) WITHOUT ROWID;

-- Batch rename undo -------------------------------------------------------------
CREATE TABLE rename_batches (
    batch_id     INTEGER PRIMARY KEY,
    created_utc  INTEGER NOT NULL,
    pattern_json TEXT    NOT NULL,
    item_count   INTEGER NOT NULL,
    state        INTEGER NOT NULL DEFAULT 0  -- 0 applied, 1 partially applied, 2 undone
);
CREATE TABLE rename_journal (
    batch_id   INTEGER NOT NULL REFERENCES rename_batches(batch_id) ON DELETE CASCADE,
    seq        INTEGER NOT NULL,
    old_path   TEXT    NOT NULL,
    new_path   TEXT    NOT NULL,
    status     INTEGER NOT NULL,             -- 0 pending, 1 done, 2 failed, 3 undone
    error      TEXT,
    PRIMARY KEY (batch_id, seq)
) WITHOUT ROWID;

-- Scripting ---------------------------------------------------------------------
CREATE TABLE scripts (
    script_id         INTEGER PRIMARY KEY,
    path              TEXT NOT NULL UNIQUE,      -- %APPDATA%\NexPane\scripts\*.js
    enabled           INTEGER NOT NULL DEFAULT 0,
    approved_sha256   TEXT,                      -- permissions approved for exactly this content
    permissions_json  TEXT NOT NULL DEFAULT '[]',
    last_run_utc      INTEGER,
    last_error        TEXT
);

-- AI ------------------------------------------------------------------------------
CREATE TABLE ai_providers (
    provider_id            TEXT PRIMARY KEY,     -- user-chosen slug, e.g. 'openai-work'
    kind                   INTEGER NOT NULL,     -- 1 AzureFoundry, 2 Gemini, 3 Anthropic, 4 OpenAI, 5 Bedrock, 6 OpenAICompatible
    display_name           TEXT NOT NULL,
    base_url               TEXT,
    region                 TEXT,                 -- Bedrock
    preset                 TEXT,                 -- 'lmstudio', 'ollama', 'custom'
    default_chat_model     TEXT,
    default_embed_model    TEXT,
    default_vision_model   TEXT,
    default_stt_model      TEXT,
    options_json           TEXT NOT NULL DEFAULT '{}', -- api-version, deployment names, max tokens, temperature
    credential_target      TEXT,                 -- CredMan target prefix
    enabled                INTEGER NOT NULL DEFAULT 1,
    egress_consent_utc     INTEGER               -- NULL until the user consents to sending content
);
CREATE TABLE ai_feature_bindings (
    feature      TEXT PRIMARY KEY,  -- 'chat','summarize','translate','reply','nl2query','embed','vision','stt'
    provider_id  TEXT NOT NULL REFERENCES ai_providers(provider_id) ON DELETE CASCADE,
    model_id     TEXT NOT NULL
);
CREATE TABLE ai_usage (
    request_id     TEXT PRIMARY KEY,
    provider_id    TEXT NOT NULL,
    model_id       TEXT NOT NULL,
    feature        TEXT NOT NULL,
    input_tokens   INTEGER,
    output_tokens  INTEGER,
    started_utc    INTEGER NOT NULL,
    duration_ms    INTEGER,
    status         INTEGER NOT NULL  -- 0 ok, 1 error, 2 cancelled, 3 refused
);

-- Preview handler safety --------------------------------------------------------------
CREATE TABLE preview_blocklist (
    clsid        TEXT PRIMARY KEY,
    extension    TEXT,
    reason       TEXT NOT NULL,      -- 'crash-sentinel', 'user', 'timeout'
    added_utc    INTEGER NOT NULL
);
