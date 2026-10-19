-- sqlite3 Corruption.db ".read Corruption_Create.sql"
-- Small fixture for the structure-aware corruption tests (Corruption_Tests.cs). The tests patch bytes of
-- this file in memory. 512 byte pages and ~300 byte rows give one row per leaf page, so "Deep" is a
-- 3-level b-tree (root interior -> interior -> leaf). "Small" fits on one leaf page and holds one blob
-- that overflows into an overflow chain.

PRAGMA page_size = 512;

CREATE TABLE Deep (Id INTEGER PRIMARY KEY, Payload TEXT);
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 120)
INSERT INTO Deep SELECT i, printf('%03d', i) || substr(replace(hex(zeroblob(150)), '00', 'ab'), 1, 297) FROM n;

CREATE TABLE Small (Id INTEGER PRIMARY KEY, Name TEXT, Data BLOB);
INSERT INTO Small VALUES (1, 'one', NULL);
INSERT INTO Small VALUES (2, 'two', zeroblob(1500));
INSERT INTO Small VALUES (3, 'three', x'0102');
