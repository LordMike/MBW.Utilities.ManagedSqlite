---
name: "test-review"
description: "Review this repo's tests or the tests in a diff; propose which to delete, merge or trim, and which reader behaviours lack a test. Use when tests are added or changed, or when asked to trim or extend the suite."
---

# Test review (MBW.Utilities.ManagedSqlite)

The goal is fewer tests, fewer lines in the tests that remain, and the right tests. A test earns its place by guarding a behaviour a caller of the library relies on that no other test already guards. The review also names the behaviours no test guards. The reviewer finds and proposes; the author deletes after verifying.

The library is a read-only, 1:1 reader of SQLite files: open a database, list and open tables, enumerate rows and their values, and (Sql package) parse the schema and resolve columns by name, including the rowid alias. Writes and SQL execution are out of scope, so tests for them are out of scope too. Corrupt files must fail gracefully.

Code coverage is not the measure. Never keep or add a test only to cover a line, and do not ask for tests of guard clauses such as argument null checks.

## The oracle is real SQLite

For anything SQLite itself can answer (which tables and schema objects exist, which rows, in which order, with which values and storage classes), the expected result comes from real SQLite, not from hand-written constants. The preferred form is the differential test: the case is a SQL script that real SQLite runs on the fly to build a database (any schema, any number of tables, pragmas such as `encoding` or `page_size` first); the test then reads that database both through SQLite and through this library and asserts that the two agree. A test thereby states "this SQL must read the same as in SQLite", and new cases are new scripts.

So a hand-written test that builds or ships a database and asserts specific values is a candidate to become one differential script, which removes its fixture, its setup and its constants. Keep a checked-in `.db` fixture only when SQLite cannot produce the case on the fly in a test run (a huge or sparse file, a deliberately corrupted page, an old file-format version).

## Tiers

1. **Low-level helpers with a defined contract** (varint decoding, serial-type to value mapping, stream helpers, the SQL tokenizer and CREATE TABLE parser): direct unit tests with chosen cases, so faults surface cheaply.
2. **Reader behaviour** (open, tables, rows, values, rowid alias, page sizes, overflow pages, encodings, ALTER TABLE defaults): differential theories against real SQLite.
3. **Corruption contract**: named, structure-aware corruptions (bad header, page number out of range, cyclic overflow chain, truncated file, bad cell offset). Each asserts the contract: it terminates, it throws the library's single corruption exception type (not a NullReference, IndexOutOfRange or hang), damage stays local where the format allows, and the database object stays usable for undamaged tables. A seeded fuzz smoke test covers the rest; one is enough.

A test in between, such as one that re-walks the varint cases through a whole database, or that re-checks a value mapping the differential tests already compare, is the usual candidate for removal.

## Principles

1. **Test the public surface.** `Sqlite3Database`, `GetTables`/`GetTable`, `Sqlite3Table.EnumerateRows`, `Sqlite3Row` values, and the Sql package's table definition and by-name lookup. A test that reaches into `BTreePage` or other internals to pin a step breaks on refactors that change nothing a caller sees, unless it is a tier-1 helper test.
2. **One API path per assertion.** Use `GetTable()` and assert on its result. Use `TryGetTable` (or another `Try*`) only where its false result is the behaviour under test. Testing both `TryGetX` and `GetX` for the same lookup is one test too many.
3. **One behaviour, one test.** Two tests that fail for the same defect are one test. Separate test classes per data type that each build a database, open it and enumerate rows are usually scripts of one differential test.
4. **Chosen cases, not matrices.** For values, the cases that matter are the storage-class boundaries SQLite itself distinguishes: each integer serial type width and its sign edge, the constants 0 and 1 (serial types 8 and 9), REAL, empty and non-empty TEXT in each encoding, BLOB, NULL, and values that spill to overflow pages. A grid that repeats a case within the same serial type guards nothing new.
5. **Setup is cost.** Generated fixtures, helper streams and hand-built pages that exist for one test suggest the test is at the wrong tier. Setup longer than the assertion is a finding. Prefer a SQL script over a new fixture file.
6. **No framework or language tests.** Do not test xUnit, Microsoft.Data.Sqlite, `Stream` behaviour, record equality or trivial getters.
7. **Exceptions only for exceptional states.** Assert the exception type for corruption and for a missing table via `GetTable`; never assert an exception message's exact text.
8. **Fast and deterministic.** No sleeps, network or clock. Fuzzing uses a fixed seed and a bounded iteration count in CI. Multi-GB or slow fixtures are kept sparse or marked so they do not slow the default run.
9. **Surviving tests stay small.** The name states the behaviour, the arrange holds only what the assertion needs, and incidental fields are not asserted.
10. **Specification tests are kept.** A test that guards documented SQLite file-format behaviour (a format rule from sqlite.org/fileformat2.html, a README example) stays even when its code is simple; say which rule it guards when keeping it on that ground.
11. **Failing differential cases are information.** A case that fails because the library differs from SQLite is a found bug, not a bad test. Do not propose deleting or weakening it; when the user has asked for such rows to land red, leave them red.

## Procedure

1. Scope: the test projects (`src/*.Tests`) or the test files in the diff. Read the code under test only as far as needed to judge the surface each test exercises.
2. Per test, note the surface, the behaviour it guards, its size, its setup cost and its fixture.
3. Classify each as `keep`, `trim` (same guard, fewer lines), `merge` (into a named test or as a row of a named theory), `convert` (to a differential script, naming the script) or `delete` (name the test that still covers the behaviour, or the principle that makes it worthless). "Looks redundant" is not a reason. When unsure, `keep`.
4. Verify before proposing a deletion or merge: grep for the test or theory row that covers the behaviour; where cheap, break the code path locally and confirm the surviving test fails. Do not add coverage tooling for this.
5. Find gaps. Walk the surface and the file format: header fields the reader honours (page size including 65536, reserved bytes, text encoding, schema format), table b-tree interior and leaf pages, overflow chains, rowid order, the rowid alias rule, ALTER TABLE ADD COLUMN defaults, WITHOUT ROWID and virtual tables (expected behaviour, even if unsupported), files over 4 GiB, and each corruption in tier 3. A gap is a behaviour a caller or the file format relies on that no test would notice breaking. For each, name the tier and the smallest test: prefer a new differential script, then a named corruption case, then a helper unit test.
6. Report findings only, per test class: path; test and line count; the `delete`/`merge`/`convert`/`trim` entries with reasons; estimated tests and lines removed; any coverage lost. Then the `add` entries (behaviour, tier, smallest test). Close with project totals.

## Applying

The author makes the changes in one small PR per area (for example, one for converting value-type tests to differential scripts, one for the corruption suite), runs `dotnet test -c Release`, and states in the PR how many tests and lines were removed or added and what, if anything, now lacks coverage. A removal that would lose a file-format guard goes to the user as a question and is not made. Do not rebase or edit other open PRs to apply findings unless the user names them.