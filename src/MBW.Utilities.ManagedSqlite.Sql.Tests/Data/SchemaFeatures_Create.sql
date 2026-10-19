-- python3 -c "import sqlite3; c = sqlite3.connect('SchemaFeatures.db'); c.executescript(open('SchemaFeatures_Create.sql').read()); c.close()"
-- SQLite keeps CREATE statements verbatim (including comments) in sqlite_master.sql

CREATE TABLE checks(id INTEGER PRIMARY KEY, a INTEGER CHECK (a > 0), b TEXT DEFAULT 'x');
INSERT INTO checks VALUES (1, 5, 'hello');

CREATE TABLE comments( -- the id
  a INTEGER, /* note */ b TEXT
);
INSERT INTO comments VALUES (7, 'seven');

CREATE TABLE "quo""ted"("col""two" TEXT, 'sq''col' INTEGER, `bt``col` REAL);
INSERT INTO "quo""ted" VALUES ('two', 2, 2.5);

CREATE TABLE generated(a INTEGER, b INTEGER GENERATED ALWAYS AS (a*2) VIRTUAL);
INSERT INTO generated(a) VALUES (3);

CREATE VIEW checks_view AS SELECT a FROM checks;
