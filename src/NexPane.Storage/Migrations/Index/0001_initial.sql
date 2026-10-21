-- index.db v1 (ARCHITECTURE §9.1). journal_mode / foreign_keys are applied per connection by SqliteConnectionFactory,
-- because PRAGMA journal_mode cannot change inside the migration transaction.

CREATE TABLE volumes (
    volume_id           INTEGER PRIMARY KEY,
    kind                INTEGER NOT NULL,          -- 0 NTFS fixed, 1 other local (FAT/exFAT/ReFS), 2 removable, 3 SMB, 4 FTP, 5 FTPS, 6 SFTP, 7 WebDAV
    root                TEXT    NOT NULL UNIQUE COLLATE NOCASE, -- 'C:\', '\\nas\media', 'sftp://user@host:22/home/user'
    volume_guid         TEXT,                      -- '\\?\Volume{...}\' for local volumes
    volume_serial       INTEGER,
    fs_name             TEXT,
    connection_id       TEXT,                      -- app.db network_connections.connection_id (cross-DB, no FK)
    usn_journal_id      INTEGER,
    last_usn            INTEGER,
    source              INTEGER NOT NULL DEFAULT 0, -- 0 walker, 1 usn-service, 2 usn-inproc, 3 crawler
    scan_state          INTEGER NOT NULL DEFAULT 0, -- 0 never, 1 scanning, 2 complete, 3 stale, 4 error
    last_full_scan_utc  INTEGER,
    entry_count         INTEGER NOT NULL DEFAULT 0,
    last_error          TEXT
);

CREATE TABLE entries (
    volume_id     INTEGER NOT NULL REFERENCES volumes(volume_id) ON DELETE CASCADE,
    file_id       INTEGER NOT NULL,                -- NTFS FRN (low 64 bits) or synthetic id
    file_id_hi    INTEGER NOT NULL DEFAULT 0,      -- high 64 bits for ReFS 128-bit ids
    parent_id     INTEGER NOT NULL,                -- parent file_id (root's parent = itself)
    name          TEXT    NOT NULL,
    attributes    INTEGER NOT NULL,
    size          INTEGER,                         -- NULL = not yet stat'ed
    created_utc   INTEGER,                         -- FILETIME (100 ns since 1601-01-01 UTC)
    modified_utc  INTEGER,
    PRIMARY KEY (volume_id, file_id_hi, file_id)
) WITHOUT ROWID;
CREATE INDEX ix_entries_parent ON entries(volume_id, parent_id);

CREATE TABLE documents (
    doc_id          INTEGER PRIMARY KEY,
    volume_id       INTEGER NOT NULL REFERENCES volumes(volume_id) ON DELETE CASCADE,
    file_id         INTEGER NOT NULL,
    file_id_hi      INTEGER NOT NULL DEFAULT 0,
    path            TEXT    NOT NULL COLLATE NOCASE,  -- last known full path
    size            INTEGER NOT NULL,
    modified_utc    INTEGER NOT NULL,
    mime            TEXT,
    extractor       TEXT    NOT NULL,                 -- 'text','pdfpig','openxml','mimekit','msgreader','ifilter','none'
    extract_status  INTEGER NOT NULL,                 -- 0 ok, 1 failed, 2 too large, 3 encrypted, 4 unsupported, 5 pending
    extract_error   TEXT,
    indexed_utc     INTEGER NOT NULL,
    UNIQUE (volume_id, file_id_hi, file_id)
);
CREATE INDEX ix_documents_path ON documents(path);

CREATE TABLE doc_text (
    text_id      INTEGER PRIMARY KEY,
    doc_id       INTEGER NOT NULL REFERENCES documents(doc_id) ON DELETE CASCADE,
    kind         INTEGER NOT NULL,      -- 1 content, 2 ocr, 3 transcript, 4 ai_description, 5 metadata
    lang         TEXT,                  -- BCP-47 when known
    producer     TEXT    NOT NULL,      -- e.g. 'pdfpig', 'Windows.Media.Ocr', 'whisper.net:ggml-base', 'openai:<model>'
    text         TEXT    NOT NULL,
    created_utc  INTEGER NOT NULL,
    UNIQUE (doc_id, kind)
);

CREATE VIRTUAL TABLE doc_text_fts USING fts5(
    text,
    content = 'doc_text',
    content_rowid = 'text_id',
    tokenize = 'unicode61 remove_diacritics 2',
    prefix = '2 3'
);
CREATE TRIGGER doc_text_ai AFTER INSERT ON doc_text BEGIN
    INSERT INTO doc_text_fts(rowid, text) VALUES (new.text_id, new.text);
END;
CREATE TRIGGER doc_text_ad AFTER DELETE ON doc_text BEGIN
    INSERT INTO doc_text_fts(doc_text_fts, rowid, text) VALUES ('delete', old.text_id, old.text);
END;
CREATE TRIGGER doc_text_au AFTER UPDATE ON doc_text BEGIN
    INSERT INTO doc_text_fts(doc_text_fts, rowid, text) VALUES ('delete', old.text_id, old.text);
    INSERT INTO doc_text_fts(rowid, text) VALUES (new.text_id, new.text);
END;

CREATE TABLE transcript_segments (
    doc_id     INTEGER NOT NULL REFERENCES documents(doc_id) ON DELETE CASCADE,
    seq        INTEGER NOT NULL,
    start_ms   INTEGER NOT NULL,
    end_ms     INTEGER NOT NULL,
    text       TEXT    NOT NULL,
    confidence REAL,
    PRIMARY KEY (doc_id, seq)
) WITHOUT ROWID;

CREATE TABLE ocr_lines (
    doc_id   INTEGER NOT NULL REFERENCES documents(doc_id) ON DELETE CASCADE,
    page     INTEGER NOT NULL,          -- 0 for single images
    seq      INTEGER NOT NULL,
    text     TEXT    NOT NULL,
    x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL,  -- normalized 0..1
    PRIMARY KEY (doc_id, page, seq)
) WITHOUT ROWID;

CREATE TABLE embedding_models (
    model_key    TEXT PRIMARY KEY,      -- '<providerId>:<modelId>'
    provider_id  TEXT    NOT NULL,
    model_id     TEXT    NOT NULL,
    dimensions   INTEGER NOT NULL,
    created_utc  INTEGER NOT NULL
);

CREATE TABLE chunks (
    chunk_id    INTEGER PRIMARY KEY,
    text_id     INTEGER NOT NULL REFERENCES doc_text(text_id) ON DELETE CASCADE,
    seq         INTEGER NOT NULL,
    char_start  INTEGER NOT NULL,
    char_len    INTEGER NOT NULL,
    UNIQUE (text_id, seq)
);

CREATE TABLE embeddings (
    model_key    TEXT    NOT NULL REFERENCES embedding_models(model_key) ON DELETE CASCADE,
    chunk_id     INTEGER NOT NULL REFERENCES chunks(chunk_id) ON DELETE CASCADE,
    vector       BLOB    NOT NULL,      -- float32 little-endian, L2-normalized, length = dimensions*4
    created_utc  INTEGER NOT NULL,
    PRIMARY KEY (model_key, chunk_id)
) WITHOUT ROWID;

CREATE TABLE work_queue (
    item_id         INTEGER PRIMARY KEY,
    kind            INTEGER NOT NULL,   -- 1 extract, 2 ocr, 3 transcribe, 4 describe, 5 embed
    doc_id          INTEGER REFERENCES documents(doc_id) ON DELETE CASCADE,
    path            TEXT    NOT NULL,
    priority        INTEGER NOT NULL DEFAULT 5,  -- 0 highest (user-requested) .. 9
    attempts        INTEGER NOT NULL DEFAULT 0,
    not_before_utc  INTEGER NOT NULL DEFAULT 0,
    last_error      TEXT,
    enqueued_utc    INTEGER NOT NULL,
    UNIQUE (kind, path)
);
CREATE INDEX ix_work_queue_ready ON work_queue(priority, not_before_utc);
