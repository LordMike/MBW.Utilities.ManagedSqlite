# Agent instructions

## The project

MBW.Utilities.ManagedSqlite is a managed, read-only reader of SQLite 3 database files. It aims for parity with the original SQLite project in what it reads: a file SQLite wrote must read here exactly as SQLite reads it. The scope is deliberately small: open a database, list and open its tables, and enumerate their rows and values. Running queries (including views) and writing are out of scope.

## Pull requests

If your change adds, removes or alters any tests, run the `test-review` skill (`.agents/skills/test-review/SKILL.md`) just before submitting the PR, scoped to the tests you changed. Apply its findings in the same PR, and state in the PR description how many tests and lines it removed or added and what, if anything, lacks coverage.
