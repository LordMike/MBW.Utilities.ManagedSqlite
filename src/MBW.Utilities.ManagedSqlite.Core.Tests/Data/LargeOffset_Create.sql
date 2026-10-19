-- sqlite3 LargeOffset.db ".read LargeOffset_Create.sql"
-- The trailing spaces in the CREATE TABLE statement are intentional: LargeOffset_Tests patches
-- sqlite_master in memory so the root page lies beyond 4 GiB, and removes 3 of those spaces to make
-- room for the wider 4-byte rootpage integer.

PRAGMA page_size=4096;

CREATE TABLE LargeOffsetTable (Id INTEGER PRIMARY KEY, Name TEXT   );
INSERT INTO LargeOffsetTable VALUES (1, 'one');
INSERT INTO LargeOffsetTable VALUES (2, 'two');
-- 5000 'x' characters, which spill onto an overflow page
INSERT INTO LargeOffsetTable VALUES (3, replace(hex(zeroblob(2500)), '0', 'x'));
