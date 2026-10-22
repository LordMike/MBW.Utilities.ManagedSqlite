# SQLite consistency checks

Every check SQLite makes while reading a database file, as surveyed for issue #58, one table per area. Each check has
a stable ID (such as `PAGE-21`) that the code, its comments, `SqliteCorruptException.Check` and
`SqliteValidationIssue.Check` use. See `.agents/writing-code.md` for how to add a check.

| Area | File | What |
|---|---|---|
| HDR | [HDR.md](HDR.md) | The 100-byte database header and the file length |
| PAGE | [PAGE.md](PAGE.md) | B-tree pages: page numbers, page headers, cell counts, tree shape |
| CELL | [CELL.md](CELL.md) | Cells, payloads and overflow chains |
| REC | [REC.md](REC.md) | Records: header, serial types, values |
| SCHEMA | [SCHEMA.md](SCHEMA.md) | `sqlite_schema` rows |
| DDL | [DDL.md](DDL.md) | CREATE statements in the schema |
| INTEG | [INTEG.md](INTEG.md) | Checks only `PRAGMA integrity_check` makes, which do not belong on the read path |
| WALJ | [WALJ.md](WALJ.md) | WAL and rollback journal checks, for the WAL and journal work |

The tables were written against SQLite 3.53.4 and this repository at debcfb3, so their *Ours* column and the file and
line references in it describe the reader before #58; the table below lists what changed since. The links into SQLite are pinned to the `version-3.53.4` tag so
their line numbers stay valid.

## Implemented in #58

The code for each of these carries its ID in a comment.

| Checks | What the reader does |
|---|---|
| HDR-02, HDR-03, HDR-09, HDR-11, PAGE-14, PAGE-15 | Strict, as before, now raised through `Checks` with their IDs |
| HDR-05, HDR-08, HDR-14, HDR-24, SCHEMA-02 | Relaxable: Strict fails as SQLite does, Lenient reports and reads on |
| HDR-01, PAGE-17 | A 0-byte file is an empty database |
| HDR-17 | A partial last page: Strict fails reading it (stricter than SQLite), Lenient reads the missing bytes as zeros, as SQLite does |
| HDR-20, INTEG-03 | The lock-byte page is never read |
| HDR-30, REC-40, SCHEMA-01 | The text encoding field is read as SQLite reads it (`& 3`, 0 is UTF-8) and never rejected |
| PAGE-03, INTEG-05 | At most (pageSize-8)/6 cells a page, in either mode |
| PAGE-11, PAGE-12, PAGE-13, CELL-04, CELL-08, CELL-09 | Cells are parsed when they are reached, and not checked against the content area (SQLite does that only with `cell_size_check`) |
| PAGE-18 | Page 1 must be a table b-tree page |
| PAGE-19, PAGE-21 | Empty interior roots and empty child pages are corrupt; Lenient reports and walks on |
| CELL-01, CELL-02 | Payload sizes keep their low 32 bits |
| CELL-10, CELL-11 | A payload must stay in its page's usable area when read through accessPayload; one that is all on its page may run into the reserved bytes |
| CELL-13, CELL-16 | An overflow chain that ends early is corrupt; the last overflow page's next pointer is never read |
| REC-03, REC-04, REC-05, REC-10, CELL-21, CELL-22, CELL-23 | Records are decoded lazily, up to the field read, with OP_Column's checks |
| REC-21 | A rowid alias column reads the rowid even when the record is short |
| CELL-38 | The reader's own bound: a payload no larger than the database's pages could hold |
| SCHEMA-11, SCHEMA-13, SCHEMA-15, SCHEMA-17, SCHEMA-19, SCHEMA-22, SCHEMA-26, SCHEMA-58, DDL-58 | Schema rows are read as sqlite3InitCallback reads them: each value as text, cut at a NUL, and the open fails where SQLite's schema load does |
| SCHEMA-26, DDL-03 | Relaxable: a row's type and tbl_name must agree with its SQL, and a table's name with its CREATE TABLE |
| SCHEMA-22 | SQL SQLite cannot parse makes `GetTableDefinition` throw `SqliteCorruptException` with SQLite's message |
| DDL-06, DDL-07, DDL-11, DDL-12, DDL-22, SCHEMA-24, SCHEMA-29, SCHEMA-30 | Table SQL is parsed by a port of SQLite's own parser (its Lemon tables, tokenizer and grammar actions), so TEMP, IF NOT EXISTS, text after the first statement or a NUL, and parameters in a DEFAULT are accepted, as SQLite accepts them |
| DDL-18, DDL-20, DDL-21, DDL-24, DDL-25, DDL-27, DDL-29, SCHEMA-28 | Strict in both modes: syntax errors, unrecognized tokens, unknown table options and a qualified name fail as in SQLite |
| DDL-34, DDL-37, DDL-48, DDL-49, DDL-50, DDL-54, DDL-61, DDL-85 | Strict in both modes: the build.c checks that decide the record layout (duplicate columns, keys and their terms, generated keys, WITHOUT ROWID without a key, expression depth) |
| DDL-36, DDL-42, DDL-43, DDL-47, DDL-51, DDL-53, DDL-55, DDL-59, DDL-60, DDL-62 | Relaxable: build.c and resolve.c checks that do not change how rows read; Lenient reports them and keeps the table as SQLite has it at that point |
| DDL-46 | CHECK constraints are dropped, as SQLite drops them for a read-only database |
| REC-11, REC-32 | A record too short for a column reads the column's DEFAULT by name, evaluated as sqlite3ValueFromExpr does, with the column's affinity; a DEFAULT with a CAST is not evaluated. A STORED generated column reads its expression the same way, as SQLite does (sqlite3ColumnDefault on the generation expression) |
