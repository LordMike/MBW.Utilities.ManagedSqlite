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
| PAGE-03, INTEG-05 | At most (pageSize-8)/6 cells a page; Lenient allows up to PAGE-39 |
| PAGE-11, PAGE-12, PAGE-13, CELL-04, CELL-08, CELL-09 | Cells are parsed when they are reached, and not checked against the content area (SQLite does that only with `cell_size_check`) |
| PAGE-18 | Page 1 must be a table b-tree page |
| PAGE-19, PAGE-21 | Empty interior roots and empty child pages are corrupt; Lenient reports and walks on |
| CELL-01, CELL-02 | Payload sizes keep their low 32 bits |
| CELL-10, CELL-11 | A payload must stay in its page's usable area when read through accessPayload; one that is all on its page may run into the reserved bytes |
| CELL-13, CELL-16 | An overflow chain that ends early is corrupt; the last overflow page's next pointer is never read |
| REC-03, REC-04, REC-05, REC-10, CELL-21, CELL-22, CELL-23 | Records are decoded lazily, up to the field read, with OP_Column's checks |
| REC-21 | A rowid alias column reads the rowid even when the record is short |
