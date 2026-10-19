-- sqlite3 RowOrder.db ".read RowOrder_Create.sql"
-- A small page size gives multi-level table b-trees (interior pages) with few rows.

PRAGMA page_size = 512;

-- 5000 rows inserted in rowid order, 1..5000
CREATE TABLE OrderedTable (Id INTEGER PRIMARY KEY, Value TEXT);
WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM seq WHERE x < 5000)
INSERT INTO OrderedTable SELECT x, 'row ' || x FROM seq;

-- 2000 rows inserted in a scrambled order (7919 is coprime to 2000, so every id 1..2000 appears once),
-- then a range is deleted and reinserted in reverse order, so cells are physically out of key order
CREATE TABLE ShuffledTable (Id INTEGER PRIMARY KEY, Value TEXT);
WITH RECURSIVE seq(x) AS (SELECT 0 UNION ALL SELECT x + 1 FROM seq WHERE x < 1999)
INSERT INTO ShuffledTable SELECT ((x * 7919) % 2000) + 1, 'row ' || (((x * 7919) % 2000) + 1) FROM seq;
DELETE FROM ShuffledTable WHERE Id BETWEEN 500 AND 1500;
WITH RECURSIVE seq(x) AS (SELECT 1500 UNION ALL SELECT x - 1 FROM seq WHERE x > 500)
INSERT INTO ShuffledTable SELECT x, 'row ' || x FROM seq;
