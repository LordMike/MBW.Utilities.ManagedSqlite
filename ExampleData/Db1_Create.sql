# sqlite3 Db1.db

create table Table1 (MyInt INTEGER, MyReal REAL, MyText TEXT, MyBlob BLOB);
insert into Table1 values(100, 1.5, 'We will rock you', 500);
insert into Table1 values(9223372036854775807, 1.5, 'The quick brown fox jumps over the lazy dog', X'FFFFFFFFFFFFFFFF0000000000000000FFFFFFFFFFFFFFFF0000000000000000');
