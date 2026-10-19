-- sqlite3 LargeRowId.db ".read LargeRowId_Create.sql"
-- Rows whose rowid needs a 9-byte varint (negative, or >= 2^56) in the cell header

create table LargeRowIdTable (Name TEXT, MyInt INTEGER);
insert into LargeRowIdTable (rowid, Name, MyInt) values(1, 'one', 100);
insert into LargeRowIdTable (rowid, Name, MyInt) values(2, 'two', 200);
insert into LargeRowIdTable (rowid, Name, MyInt) values(127, 'max 1 byte', 127);
insert into LargeRowIdTable (rowid, Name, MyInt) values(128, 'min 2 bytes', 128);
insert into LargeRowIdTable (rowid, Name, MyInt) values(72057594037927935, 'max 8 bytes', 72057594037927935);	-- 2^56 - 1
insert into LargeRowIdTable (rowid, Name, MyInt) values(72057594037927936, 'min 9 bytes', 72057594037927936);	-- 2^56
insert into LargeRowIdTable (rowid, Name, MyInt) values(9223372036854775807, 'long max', 9223372036854775807);
insert into LargeRowIdTable (rowid, Name, MyInt) values(-1, 'minus one', -1);
insert into LargeRowIdTable (rowid, Name, MyInt) values(-9223372036854775808, 'long min', -9223372036854775808);
