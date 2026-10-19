-- sqlite3 RowIdAlias.db ".read RowIdAlias_Create.sql"

-- Not rowid aliases: the declared type is not exactly INTEGER, so the id is stored in the record and the rowid is 1
CREATE TABLE IntPk (id INT PRIMARY KEY, v TEXT);
INSERT INTO IntPk (id, v) VALUES (100, 'value');

CREATE TABLE BigIntPk (id BIGINT PRIMARY KEY, v TEXT);
INSERT INTO BigIntPk (id, v) VALUES (200, 'value');

-- Rowid aliases: the record stores NULL for id, and the value lives in the rowid
create table LowerIntegerPk (id integer primary key, v text);
insert into LowerIntegerPk (id, v) values (42, 'value');

CREATE TABLE TableLevelPk (id INTEGER, v TEXT, PRIMARY KEY (id));
INSERT INTO TableLevelPk (id, v) VALUES (77, 'value');
