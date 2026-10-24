# Writing code

The project's standing rules for code, so an agent can follow them without the history of how they came about.

Read this before changing anything under `src/`. `AGENTS.md` covers pull requests and the `test-review` skill.

## Parity with SQLite

This library is a read-only reader whose reference implementation is **SQLite 3**: the file format and behaviour of the
current 3.x releases, as exercised by the SQLite build the tests bundle through Microsoft.Data.Sqlite. We don't target a
specific point release. When the bundled SQLite is updated, the parity tests show whether behaviour changed. For
anything the reader does, the question is "what does SQLite do here?". The answer comes from SQLite's source and from
running SQLite, not from intuition.

- A file SQLite reads must read here with the same values. A file SQLite rejects must be rejected here, at the same point
  (database open, table open, or the row), unless the next section says otherwise.
- Out of scope: writing, running SQL (including views), and indexes as a lookup structure. WAL and rollback journals are
  read (`FromStreamWithWal`, `FromStreamWithJournal`, and `FromPath`, which picks up a `-wal` or `-journal` file next to
  the database); their checks are listed in `docs/sqlite-checks/WALJ.md`.
- Virtual tables are listed, but reading one throws `NotSupportedException`.
- When behaviour is unclear, write a parity test first (see "Tests") and let SQLite decide.

## Citing SQLite source

Code that mirrors SQLite behaviour gets a comment that lets a reviewer check it against the source in under a minute:
1. **What SQLite requires, in one line,** and the function and file it is in.
2. **The SQLite condition or statement, quoted verbatim** and indented, e.g.
   `//   if( nCell>MX_CELL(pBt) ) return SQLITE_CORRUPT_PAGE(pPage);`
3. **A link pinned to a release tag:** `https://github.com/sqlite/sqlite/blob/version-3.X.Y/src/<file>#L<line>`, where
   3.X.Y is the release you read; use the newest one, which is normally the version the tests bundle. The tag only keeps
   the line number stable. It does not mean we target that release, and links to different 3.x tags can sit side by
   side. Never use `sqlite.org/src/file?name=...` without `ci=`, or a branch link, because those follow trunk and drift.
   The function name in step 1 keeps the reference useful when a later release moves the line.
4. **For file-format meaning,** also link the `https://www.sqlite.org/fileformat.html#...` section.
5. **`parse.c` is generated, so cite the `parse.y` rule instead,** e.g. `parse.y tconscomma`.

Any release's source is at `https://raw.githubusercontent.com/sqlite/sqlite/version-3.X.Y/src/<file>`.

## Code style

- **Comment density follows the surrounding code.** Code that mirrors SQLite is the exception: it gets the plentiful
  comments described above.
- **Construction:** prefer static factories (e.g. `ReaderBase.Open`) over public constructors. `Sqlite3Database` is
  opened through `FromPath`, `FromStreamWithNoJournal`, `FromStreamWithWal` and `FromStreamWithJournal`; its constructor
  is internal. Keep parsed state `readonly`.
- **Nullable annotations:** the libraries are annotated. Where the compiler cannot see that a value is non-null, use
  `!` alone. Don't add a `Debug.Assert` next to it or custom guard helpers.
- **Enum widths:** make enums as small as possible (`BTreeKind : byte`). An enum that mirrors an on-disk field keeps that
  field's width (`SqliteEncoding : uint` mirrors the 4-byte header field).
- **Targets:** the libraries target netstandard2.0 with `LangVersion latest`. Newer compiler features
  (such as interpolated string handlers) need their attributes polyfilled under `Internal/Compat/`, next to
  `NullableAttributes.cs`.
- **Errors:** damaged files surface as `SqliteCorruptException`, raised through `Checks` (next section). Damage to the
  tree (pages, cells, payloads, overflow chains) fails the row enumerator. A record is decoded lazily, as SQLite's
  OP_Column does: damage in its header or values fails the accessor that reads that field (`TryGetOrdinal`,
  `TryGetValueByName`, `ColumnData`), and only once the header is parsed that far. A caller bug (`null` argument, an
  invalid `FallbackEncoding`) is not corruption and is never converted.
- **Hot paths** (per page, per cell, per row) must not allocate on the success path. Verify that with a throwaway
  BenchmarkDotNet run and report the numbers in the PR; don't commit one-off benchmarks.

## Consistency checks

Every place the reader validates bytes from the file, or SQL from `sqlite_schema`, is a **check**. Checks follow one
rule: **fail exactly where SQLite fails**. Not earlier, not more strictly, and not more loosely. `Validation = Lenient`
relaxes only the checks where the data that follows is still intact.

### Before you write a check

1. **Find the SQLite code that does the same check.** The survey was made against SQLite 3.53.4. Look it up in the survey tables in `docs/sqlite-checks/` by area: HDR (file header),
   PAGE (b-tree pages), CELL (cells and overflow), REC (records), SCHEMA (sqlite_schema rows), DDL (CREATE statements),
   INTEG (`integrity_check` only, not for the read path except INTEG-14 and INTEG-33), WALJ (WAL and journals).
2. **If SQLite has no such check, don't add one.** That holds even when the data looks wrong; for example, SQLite reads
   reserved serial types 10 and 11 as NULL. Checks that SQLite runs only in `PRAGMA integrity_check`, `quick_check` or
   with `cell_size_check=ON` don't belong on the read path either. The exceptions are INTEG-14 (rowids in order) and INTEG-33
   (WITHOUT ROWID primary keys in order), Relaxable checks that stop a b-tree whose pages share a subtree from being
   walked exponentially often.
3. **Prove it against real SQLite.** Patch a database in the parity tests and confirm that SQLite (Microsoft.Data.Sqlite)
   fails, or reads on, at the same place.

### Pick the kind

| SQLite does | Data after the damage | Use |
|---|---|---|
| Fails (CORRUPT, NOTADB, schema error) | Unreadable or ambiguous (bad page number, unknown page type, header past payload) | `Checks.Strict` |
| Fails | Intact; the check is a formality (read version > 2, payload fractions, header page count > file) | `Checks.Relaxable`, with a fallback |
| Reads on silently | n/a | Nothing; we read on too, and a comment says why |
| Not on the read path (integrity_check, write path, seeks, indexes) | n/a | Nothing |

A Lenient fallback must never return a wrong value. If the only fallback would invent or misplace data, use
`Checks.Strict`. Examples: the lock-byte page is never payload, so it is Strict. An empty child page is Relaxable: the
walk goes on as before, and no row moves.

### Write it

```csharp
// <ID>: <one line: what SQLite requires>. <function> in <file>:
//   <the SQLite condition, quoted verbatim>
// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/<file>#L<line>
// <Lenient: what happens instead, and why the data is still intact>   (Relaxable only)
```

```csharp
// PAGE-21: a page reached through a child pointer must hold at least one cell. moveToChild in btree.c:
//   if( rc==SQLITE_OK
//    && (pCur->pPage->nCell<1 || pCur->pPage->intKey!=pCur->curIntKey)
//   ){
//     releasePage(pCur->pPage);
//     rc = SQLITE_CORRUPT_PGNO(newPgno);
// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5471 (moveToChild)
// Lenient reports it and walks on as before: an empty leaf has no rows, and an empty interior page still has its
// right-most child, so no row moves or appears.
Checks.Relaxable(child.CellCount > 0, parent.Reader.Settings, "PAGE-21",
    $"child page {childPage} has no cells", childPage, "b-tree page header");

// HDR-20: the lock-byte page is never part of the database, in any mode. getPageNormal in pager.c:
//   if( pgno==PAGER_SJ_PGNO(pPager) ){
//     rc = SQLITE_CORRUPT_BKPT;
// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/pager.c#L5628 (getPageNormal)
Checks.Strict(page != _lockBytePage, "HDR-20", $"page {page} is the lock-byte page, which holds no data", page, "page number");

// HDR-14 with a fallback: Relaxable returns false in Lenient, and the caller takes the file's count
PageCount = headerSizeValid && Checks.Relaxable(header.DatabaseSizeInPages <= filePages, settings, "HDR-14",
        $"the header says the database has {header.DatabaseSizeInPages} pages, but the file holds {filePages}",
        structure: "file length")
    ? header.DatabaseSizeInPages
    : filePages;
```

Rules:
- **Never `throw new SqliteCorruptException` directly.** Go through `Checks`, so every failure carries its ID. Nothing
  turns other exceptions into `SqliteCorruptException`: an `IndexOutOfRangeException` or similar from damaged bytes is a
  missing check, so add one where SQLite makes it, rather than catching the exception.
- **Pass the message as an interpolated string straight into the call.** The `CheckMessage` handler builds it only when
  the check fails, so a passing check allocates nothing. Don't build the message first.
- **The check ID is the survey ID.** A check with no ID gets a new row in the right area table, with the next free
  number, in the same PR (CELL-38 is an example: the reader's own bound on a payload's size, which it needs because it allocates the payload
  before SQLite would find the damage). An ID means one
  check: don't reuse SQLite's ID for a different bound.
- **A Relaxable check that can fire repeatedly** (on every read of the same page) should report once; see HDR-17 in
  `ReaderBase.ReadPage`.
- **Comment and link format:** follow "Citing SQLite source" above, and start the comment with the check ID.
- **Schema checks fail at open.** Every sqlite_schema row is parsed when the database is opened, as SQLite loads it, so
  Strict fails the open; Lenient reports the error and keeps it on the row, which then cannot be opened.
- **Structural damage fails the enumerator; a bad value fails only its cell.** Pages, cells, overflow and record headers
  belong to the first group. Invalid text belongs to the second.

### Test it

Each check gets parity tests in the corruption suite (`Tests/Corruption_Tests.cs`, a database built by
`SqliteParity.Build` and patched with `DbPatcher`):
- **Strict:** SQLite fails (`SqliteParity.AssertSqliteReportsCorrupt`, or `AssertSqliteFails` for a non-CORRUPT error),
  we throw `SqliteCorruptException` with that `Check` ID, and the database object stays usable for undamaged tables. A
  check that holds in every mode also gets a Lenient case: it still throws, and nothing is reported. A schema row's
  statement check is the exception: Lenient reports it at open, and that row's table throws from `GetTable`.
- **Relaxable, Lenient:** we read on, every row we return is intact (compare with SQLite, or with what the script
  wrote), and `OnValidationIssue` received the ID exactly once. `SqliteParity.AssertSameAsSqlite(script, patch,
  settings)` opens our side with Lenient settings.
- **Tolerated** (SQLite reads on): a parity test (`SqliteParity.AssertSameAsSqlite(script, patch)`) that we read on
  too, with the same values, so nobody adds a check later.


### Checklist

- [ ] SQLite does this check on the read path (cited by pinned link and function)
- [ ] Strict vs Relaxable chosen by the table above, and the Lenient fallback cannot produce wrong values
- [ ] The comment has the ID, the quoted condition and the link
- [ ] The ID is listed in `docs/sqlite-checks/README.md` (the survey tables' *Ours* column is not kept current)
- [ ] Parity tests for Strict, and for Lenient if Relaxable


## Tests

- **SQLite is the oracle.** Value and schema tests are `SqliteParity.AssertSameAsSqlite(script)` theories
  (`Tests/Helpers/SqliteParity.cs`). The script creates and fills its tables in real SQLite, in memory, and the
  assertion compares SQLite's view (schema, then rows) with ours. Corruption tests build a database the same way, patch
  bytes, and assert that SQLite and we fail, or read on, alike.
- **A patch asserts what it overwrites.** Before a test changes bytes or schema rows, it asserts that the target holds
  the value it expects, so a patch that drifts onto other bytes fails instead of testing something else.
  `DbPatcher.WriteByte(offset, expected, value)` takes the old byte, and its structure setters assert that they are on
  a b-tree page or header field holding a value SQLite writes. A raw edit asserts the old bytes first, and a
  `writable_schema` edit asserts how many rows it changed (`SqliteParity.Build(script, edit, rows)`).
- **Tests in one file look alike.** Each theory fixes the statement shape (e.g.
  `CREATE TABLE t ({columns}) WITHOUT ROWID; INSERT INTO t VALUES {values}`), and its `[InlineData]` rows give only what
  varies. CREATE TABLE syntax that SQLite accepts lives in `Tests/SqlCreateTable_Tests.cs`; the checks SQLite makes
  on a CREATE TABLE in `Tests/SqlCreateTableChecks_Tests.cs`, and those on the schema as a whole in
  `Tests/SqlSchemaChecks_Tests.cs`; whole schemas taken from real applications in
  `Tests/SqlRealWorldSchemas_Tests.cs`.
- **A test must add a new syntax or code path.** Don't add rows like "N columns" unless something special happens at N.
- **One API path per assertion.** Don't test `TryGetX` and `GetX` for the same lookup; keep `Try*` only where its false
  result is the behaviour under test.
- **Unit tests are only for helper contracts.** Everything else goes through parity.
- **Never skip, disable or delete a failing test to get green.** A red parity row is a finding, so report it.
- Run the `test-review` skill on the tests you changed (see `AGENTS.md`).
