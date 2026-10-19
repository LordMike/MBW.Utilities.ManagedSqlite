-- sqlite3 MixedCaseName.db ".read MixedCaseName_Create.sql"

CREATE TABLE MixedCaseTable (Id INTEGER PRIMARY KEY, Value TEXT);

INSERT INTO MixedCaseTable VALUES (1, 'one');
INSERT INTO MixedCaseTable VALUES (2, 'two');
INSERT INTO MixedCaseTable VALUES (3, 'three');

CREATE INDEX MixedCaseIndex ON MixedCaseTable (Value);

CREATE VIEW MixedCaseView AS SELECT Value FROM MixedCaseTable;
