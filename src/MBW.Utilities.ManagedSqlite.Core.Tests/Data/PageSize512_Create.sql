-- sqlite3 PageSize512.db ".read PageSize512_Create.sql"
-- Blob contents: the numbers 1, 2, 3, ... each zero-padded to 6 characters and concatenated, cut to the wanted length

PRAGMA page_size=512;

CREATE TABLE SmallTable (Id INTEGER PRIMARY KEY, Name TEXT);
INSERT INTO SmallTable VALUES (1, 'One');
INSERT INTO SmallTable VALUES (2, 'Two');
INSERT INTO SmallTable VALUES (3, 'Three');

CREATE TABLE BlobTable (Id INTEGER PRIMARY KEY, Data BLOB);
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40000)
INSERT INTO BlobTable SELECT 1, CAST(substr(group_concat(printf('%06d', i), ''), 1, 60000) AS BLOB) FROM (SELECT i FROM n ORDER BY i);
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40000)
INSERT INTO BlobTable SELECT 2, CAST(substr(group_concat(printf('%06d', i), ''), 1, 200000) AS BLOB) FROM (SELECT i FROM n ORDER BY i);
