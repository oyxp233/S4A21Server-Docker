-- A21 item_core 一次性补零迁移脚本。
-- 用法示例: sqlite3 game.db ".read Tool/Sqlite/expand-itemcore-to-99.sql"
-- 该脚本只接受旧 82B 或新 99B 数据，其他长度会触发 CHECK 失败并回滚。

PRAGMA foreign_keys = OFF;
BEGIN IMMEDIATE;

CREATE TEMP TABLE item_core_length_guard (
    value INTEGER NOT NULL CHECK(value = 0)
);

INSERT INTO item_core_length_guard(value)
SELECT CASE WHEN COUNT(*) = 1 THEN 0 ELSE 1 END
FROM schema_metadata
WHERE singleton_id = 1
  AND baseline_id = '86jp-database-v1'
  AND schema_version = 1
HAVING COUNT(*) <> 1;

INSERT INTO item_core_length_guard(value)
SELECT COUNT(*)
FROM character_inventory_items
WHERE item_core IS NULL OR length(item_core) NOT IN (82, 99)
HAVING COUNT(*) <> 0;

INSERT INTO item_core_length_guard(value)
SELECT COUNT(*)
FROM account_inventory_items
WHERE item_core IS NULL OR length(item_core) NOT IN (82, 99)
HAVING COUNT(*) <> 0;

INSERT INTO item_core_length_guard(value)
SELECT COUNT(*)
FROM character_titlebook_items
WHERE item_core IS NULL OR length(item_core) NOT IN (82, 99)
HAVING COUNT(*) <> 0;

INSERT INTO item_core_length_guard(value)
SELECT COUNT(*)
FROM mailbox_attachments
WHERE item_core IS NOT NULL AND length(item_core) NOT IN (82, 99)
HAVING COUNT(*) <> 0;

INSERT INTO item_core_length_guard(value)
SELECT COUNT(*)
FROM characters
WHERE appearance_blob IS NOT NULL
  AND length(appearance_blob) > 0
  AND ((length(appearance_blob) - 1) % 23) <> 0
HAVING COUNT(*) <> 0;

CREATE TABLE character_inventory_items_v2 (
    item_uid INTEGER PRIMARY KEY AUTOINCREMENT,
    character_id INTEGER NOT NULL,
    list_type INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_core BLOB NOT NULL CHECK(length(item_core) = 99),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(character_id, list_type, slot_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

INSERT INTO character_inventory_items_v2 (
    item_uid, character_id, list_type, slot_index, item_core, created_at, updated_at
)
SELECT item_uid,
       character_id,
       list_type,
       CASE
           WHEN list_type = 3 AND slot_index BETWEEN 11 AND 30 THEN slot_index + 1
           ELSE slot_index
       END,
       CASE WHEN length(item_core) = 82 THEN CAST(item_core || zeroblob(17) AS BLOB) ELSE item_core END,
       created_at,
       updated_at
FROM character_inventory_items;

DROP TABLE character_inventory_items;
ALTER TABLE character_inventory_items_v2 RENAME TO character_inventory_items;
CREATE INDEX IF NOT EXISTS idx_character_inventory_items_character_space
    ON character_inventory_items(character_id, list_type, slot_index);

CREATE TABLE account_inventory_items_v2 (
    item_uid INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_core BLOB NOT NULL CHECK(length(item_core) = 99),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(account_id, slot_index),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

INSERT INTO account_inventory_items_v2 (
    item_uid, account_id, slot_index, item_core, created_at, updated_at
)
SELECT item_uid,
       account_id,
       slot_index,
       CASE WHEN length(item_core) = 82 THEN CAST(item_core || zeroblob(17) AS BLOB) ELSE item_core END,
       created_at,
       updated_at
FROM account_inventory_items;

DROP TABLE account_inventory_items;
ALTER TABLE account_inventory_items_v2 RENAME TO account_inventory_items;

CREATE TABLE character_titlebook_items_v2 (
    character_id INTEGER NOT NULL,
    category INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_core BLOB NOT NULL CHECK(length(item_core) = 99),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, category, slot_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

INSERT INTO character_titlebook_items_v2 (
    character_id, category, slot_index, item_core, updated_at
)
SELECT character_id,
       category,
       slot_index,
       CASE WHEN length(item_core) = 82 THEN CAST(item_core || zeroblob(17) AS BLOB) ELSE item_core END,
       updated_at
FROM character_titlebook_items;

DROP TABLE character_titlebook_items;
ALTER TABLE character_titlebook_items_v2 RENAME TO character_titlebook_items;

CREATE TABLE mailbox_attachments_v2 (
    attachment_id INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id INTEGER NOT NULL,
    ordinal INTEGER NOT NULL DEFAULT 0,
    item_type INTEGER NOT NULL DEFAULT 0,
    source_list_type INTEGER NOT NULL DEFAULT 0,
    source_slot_index INTEGER NOT NULL DEFAULT 0,
    source_item_uid INTEGER NOT NULL DEFAULT 0,
    item_template_id INTEGER NOT NULL CHECK(item_template_id > 0),
    item_kind TEXT NOT NULL DEFAULT 'unknown',
    item_count INTEGER NOT NULL CHECK(item_count > 0),
    instance_value INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_flag INTEGER NOT NULL DEFAULT 0,
    option_value INTEGER NOT NULL DEFAULT 0,
    equipment_lock_id INTEGER NOT NULL DEFAULT 0,
    expire_time INTEGER NOT NULL DEFAULT 0,
    marker_16 INTEGER NOT NULL DEFAULT -1,
    pet_serial_or_handle INTEGER NOT NULL DEFAULT 0,
    extra_json TEXT NOT NULL DEFAULT '{}',
    item_core BLOB CHECK(item_core IS NULL OR length(item_core) = 99),
    detail_json TEXT NOT NULL DEFAULT '',
    claimed_flag INTEGER NOT NULL DEFAULT 0 CHECK(claimed_flag IN (0, 1, 2)),
    claimed_at TEXT,
    FOREIGN KEY (message_id) REFERENCES mailbox_messages(message_id) ON DELETE CASCADE
);

INSERT INTO mailbox_attachments_v2 (
    attachment_id, message_id, ordinal, item_type, source_list_type, source_slot_index,
    source_item_uid, item_template_id, item_kind, item_count, instance_value, durability,
    seal_flag, option_value, equipment_lock_id, expire_time, marker_16,
    pet_serial_or_handle, extra_json, item_core, detail_json, claimed_flag, claimed_at
)
SELECT attachment_id,
       message_id,
       ordinal,
       item_type,
       source_list_type,
       source_slot_index,
       source_item_uid,
       item_template_id,
       item_kind,
       item_count,
       instance_value,
       durability,
       seal_flag,
       option_value,
       equipment_lock_id,
       expire_time,
       marker_16,
       pet_serial_or_handle,
       extra_json,
       CASE
           WHEN item_core IS NULL THEN NULL
           WHEN length(item_core) = 82 THEN CAST(item_core || zeroblob(17) AS BLOB)
           ELSE item_core
       END,
       detail_json,
       claimed_flag,
       claimed_at
FROM mailbox_attachments;

DROP TABLE mailbox_attachments;
ALTER TABLE mailbox_attachments_v2 RENAME TO mailbox_attachments;
CREATE INDEX IF NOT EXISTS idx_mailbox_attachments_message
    ON mailbox_attachments(message_id, ordinal);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mailbox_attachments_message_ordinal
    ON mailbox_attachments(message_id, ordinal);

UPDATE characters
SET appearance_blob = (
    WITH RECURSIVE build(i, data) AS (
        SELECT 0, substr(appearance_blob, 1, 1)
        UNION ALL
        SELECT i + 1,
               data ||
               CASE
                   WHEN unicode(CAST(substr(appearance_blob, 2 + i * 23, 1) AS TEXT)) BETWEEN 11 AND 30
                   THEN CAST(char(unicode(CAST(substr(appearance_blob, 2 + i * 23, 1) AS TEXT)) + 1) AS BLOB)
                   ELSE substr(appearance_blob, 2 + i * 23, 1)
               END ||
               substr(appearance_blob, 3 + i * 23, 22)
        FROM build
        WHERE i < ((length(appearance_blob) - 1) / 23)
    )
    SELECT data
    FROM build
    ORDER BY i DESC
    LIMIT 1
),
    updated_at = CURRENT_TIMESTAMP
WHERE appearance_blob IS NOT NULL
  AND length(appearance_blob) > 1;

UPDATE schema_metadata
SET schema_version = 2,
    updated_at = CURRENT_TIMESTAMP
WHERE singleton_id = 1
  AND baseline_id = '86jp-database-v1'
  AND schema_version < 2;

PRAGMA user_version = 2;

DROP TABLE item_core_length_guard;
COMMIT;
PRAGMA foreign_keys = ON;
