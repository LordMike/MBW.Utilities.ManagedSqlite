-- sqlite3 VirtualTable.db ".read VirtualTable_Create.sql"

CREATE VIRTUAL TABLE docs USING fts5(title, body);

INSERT INTO docs (rowid, title, body) VALUES (1, 'First', 'Hello world');
INSERT INTO docs (rowid, title, body) VALUES (2, 'Second', 'Another document');
INSERT INTO docs (rowid, title, body) VALUES (3, 'Third', 'Yet another one');

CREATE TABLE Plain (Id INTEGER PRIMARY KEY, Value TEXT);

INSERT INTO Plain VALUES (1, 'one');
INSERT INTO Plain VALUES (2, 'two');
