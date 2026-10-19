CREATE TABLE products(id INTEGER PRIMARY KEY, name TEXT NOT NULL CHECK (length(name) > 0), price REAL CHECK (price >= 0.0 AND price <= 1e6), qty INTEGER DEFAULT -1 CHECK (qty <> 0 OR qty != 1 OR qty == 2), flags INTEGER DEFAULT 0x1F CHECK (((flags & 3) | (flags << 2)) >> 1 = ~flags % 2), code TEXT DEFAULT ('a' || 'b'), ratio REAL DEFAULT (10 / 4 * 2 - .5 + 1.5E-3));
CREATE TABLE notes( /* id */ id INTEGER PRIMARY KEY, /* body */ body TEXT /* trailing */);
CREATE TABLE "my""table"("col""two" TEXT, 'sq''col' INTEGER, `bt``col` BLOB, [br col] REAL);
CREATE TABLE gen(a INTEGER, b INTEGER GENERATED ALWAYS AS (a * 2) STORED, c TEXT AS (upper(a)) VIRTUAL);
CREATE TABLE child(id INTEGER, parent_id INTEGER NOT NULL, PRIMARY KEY (id), FOREIGN KEY (parent_id) REFERENCES parent(id) ON DELETE CASCADE, CHECK (id > parent_id));
