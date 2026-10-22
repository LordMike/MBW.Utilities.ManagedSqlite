---
name: "test-review"
description: "Review this repo's tests or the tests in a diff; propose which to delete, merge, trim or turn into SQL parity scripts, and which behaviours or helper contracts lack a test. Use when tests are added or changed, or when asked to trim or extend the suite."
---

# Test review (MBW.Utilities.ManagedSqlite)

The goal is fewer tests, fewer lines in the tests that remain, and the right tests. A test earns its place by guarding a behaviour a caller of the library relies on that no other test already guards. The review also names the behaviours no test guards. The reviewer finds and proposes; the author deletes after verifying.

The library is a read-only, 1:1 reader of SQLite files: open a database, list and open tables, enumerate rows and their values, and parse the schema and resolve columns by name, including the rowid alias. Writes and SQL execution are out of scope, so tests for them are out of scope too. Corrupt files must fail gracefully.

Code coverage is not the measure. Never keep or add a test only to cover a line, and do not ask for tests of guard clauses such as argument null checks.

## Two kinds of test

**SQL-based parity tests are the default.** For anything SQLite itself can answer (which tables and schema objects exist, which rows in which order, which values with which storage classes, the encoding and page size), the test is a SQL script and the oracle is real SQLite. `SqliteParity.AssertSameAsSqlite(script)` (Core.Tests `Helpers/`) builds the database in memory with SQLite, dumps it through SQLite and through this library into one text format, and asserts they are equal. A case therefore says "this SQL must read the same as in SQLite"; adding coverage means adding a script, not constants. Scripts are grouped into one `[Theory]` per category (values, structure, schemas, rowid alias, ...), each row a named script. `SqliteParity.Build(script)` returns the database bytes for API tests that need a database but no comparison, such as table lookup rules.

A test that ships a `.db` or asserts hand-written values for what SQLite could answer is a candidate to become a script row; this removes its fixture, setup and constants. Keep a checked-in fixture only when SQLite cannot produce the file in a test run (a multi-GB sparse file, a deliberately corrupted page, an old format version), or patch a script-built database in memory instead.

**Unit tests guard contracts.** Small helpers, utilities and services with a defined contract (even an unwritten one) get direct unit tests with chosen cases, so their faults surface early and cheaply: varint and serial-type decoding, stream and string helpers, the SQL tokenizer and CREATE TABLE parser (declared type, affinity, PK flags, which parity does not see), settings, and the public API's own rules (lookup by name, `Try*` false results, exception types). Keep these, and add them where a contract has none; do not convert them to parity scripts.

**Corruption tests guard graceful failure.** Named, structure-aware corruptions (bad header, page number out of range, cyclic overflow chain, truncated file, bad cell offset), built by patching a script-built database. Each asserts the contract: it terminates, it throws the library's single corruption exception type (not a NullReference, IndexOutOfRange or hang), damage stays local where the format allows, and the database object stays usable for undamaged tables. A seeded fuzz smoke test covers the rest; one is enough.

A test in between, such as one that re-walks the varint cases through a whole database, or that asserts a value the parity tests already compare, is the usual candidate for removal.

## Principles

1. **Test the public surface.** `Sqlite3Database`, `GetTables`/`GetTable`, `Sqlite3Table.EnumerateRows`, `Sqlite3Row` values, and the table definition and by-name lookup. A test that reaches into `BTreePage` or other internals to pin a step breaks on refactors that change nothing a caller sees, unless it is a unit test of that helper's contract.
2. **One API path per assertion.** Use `GetTable()` and assert on its result. Use `TryGetTable` (or another `Try*`) only where its false result is the behaviour under test. Testing both `TryGetX` and `GetX` for the same lookup is one test too many.
3. **One behaviour, one test.** Two tests that fail for the same defect are one test. Separate test classes per data type that each build a database, open it and enumerate rows are rows of one parity theory.
4. **Chosen cases, not matrices.** For values, the cases that matter are the storage-class boundaries SQLite itself distinguishes: each integer serial type width and its sign edge, the constants 0 and 1 (serial types 8 and 9), REAL, empty and non-empty TEXT in each encoding, BLOB, NULL, and values that spill to overflow pages. A grid that repeats a case within the same serial type guards nothing new.
5. **Setup is cost.** Generated fixtures, helper streams and hand-built pages that exist for one test suggest the test is the wrong kind. Setup longer than the assertion is a finding. Prefer a SQL script over a new fixture file.
6. **No framework or language tests.** Do not test xUnit, Microsoft.Data.Sqlite, `Stream` behaviour, record equality or trivial getters.
7. **Exceptions only for exceptional states.** Assert the exception type for corruption and for a missing table via `GetTable`; never assert an exception message's exact text.
8. **Fast and deterministic.** No sleeps, network or clock. Fuzzing uses a fixed seed and a bounded iteration count in CI. Multi-GB or slow fixtures are kept sparse or marked so they do not slow the default run.
9. **Surviving tests stay small.** The name states the behaviour, the arrange holds only what the assertion needs, and incidental fields are not asserted.
10. **Specification tests are kept.** A test that guards documented SQLite file-format behaviour (a format rule from sqlite.org/fileformat2.html, a README example) stays even when its code is simple; say which rule it guards when keeping it on that ground.
11. **Failing parity cases are information.** A case that fails because the library differs from SQLite is a found bug, not a bad test. Do not propose deleting or weakening it; when the user has asked for such rows to land red, leave them red.

## Procedure

1. Scope: the test projects (`src/*.Tests`) or the test files in the diff. Read the code under test only as far as needed to judge the surface each test exercises.
2. Per test, note the surface, the behaviour it guards, its size, its setup cost and its fixture.
3. Classify each as `keep`, `trim` (same guard, fewer lines), `merge` (into a named test or as a row of a named theory), `convert` (to a parity script, naming the theory it joins) or `delete` (name the test that still covers the behaviour, or the principle that makes it worthless). "Looks redundant" is not a reason. When unsure, `keep`.
4. Verify before proposing a deletion or merge: grep for the test or theory row that covers the behaviour; where cheap, break the code path locally and confirm the surviving test fails. Do not add coverage tooling for this.
5. Find gaps. Walk the surface and the file format: header fields the reader honours (page size including 65536, reserved bytes, text encoding, schema format), table b-tree interior and leaf pages, overflow chains, rowid order, the rowid alias rule, ALTER TABLE ADD COLUMN defaults, WITHOUT ROWID and virtual tables (expected behaviour, even if unsupported), files over 4 GiB, and each corruption case. A gap is a behaviour a caller or the file format relies on that no test would notice breaking. Also look for helpers and utilities whose contract has no unit test. For each gap, name the kind of test and the smallest one: a new parity script for reader behaviour, a unit test for a helper's contract, a named case for corruption.
6. Report findings only, per test class: path; test and line count; the `delete`/`merge`/`convert`/`trim` entries with reasons; estimated tests and lines removed; any coverage lost. Then the `add` entries (behaviour, kind of test, smallest test). Close with project totals.

## Applying

The author makes the changes in one small PR per area (for example, one for new parity scripts, one for the corruption suite), runs `dotnet test -c Release`, and states in the PR how many tests and lines were removed or added and what, if anything, now lacks coverage. A removal that would lose a file-format guard goes to the user as a question and is not made. Do not rebase or edit other open PRs to apply findings unless the user names them.