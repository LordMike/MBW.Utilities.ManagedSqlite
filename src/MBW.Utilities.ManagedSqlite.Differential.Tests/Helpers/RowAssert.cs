using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using Xunit.Sdk;

namespace MBW.Utilities.ManagedSqlite.Differential.Tests.Helpers;

/// <summary>
/// Strict comparison of the library's rows with SQLite's: same rowids, same values with the same CLR types, same order.
/// </summary>
internal static class RowAssert
{
    private const int MaxReportedDifferences = 10;

    /// <summary>
    /// The value the library should put in <see cref="Sqlite3Row.ColumnData"/> for column <paramref name="ordinal"/>.
    /// That is SQLite's value, except for an INTEGER PRIMARY KEY column, which SQLite stores as NULL in the record.
    /// </summary>
    public static object ExpectedColumnData(ExpectedTable table, ExpectedRow row, int ordinal)
    {
        return ordinal == table.RowIdAliasOrdinal ? null : row.Values[ordinal];
    }

    /// <summary>
    /// Checks the rows first as a set keyed by rowid (so a value bug is reported as such), then the order.
    /// </summary>
    public static void Equal(ExpectedTable expected, IReadOnlyList<Sqlite3Row> actual)
    {
        if (expected.WithoutRowId)
        {
            // No rowid to key on: compare values in primary key order, which is SQLite's order for these tables.
            List<string> diffs = new List<string>();
            if (expected.Rows.Count != actual.Count)
                diffs.Add($"row count: expected {expected.Rows.Count}, actual {actual.Count}");

            for (int i = 0; i < Math.Min(expected.Rows.Count, actual.Count) && diffs.Count < MaxReportedDifferences; i++)
                CompareColumns(expected, expected.Rows[i], actual[i], $"row #{i}", diffs);

            Fail(expected, "rows differ", diffs);
            return;
        }

        EqualIgnoringOrder(expected, actual);
        SameRowOrder(expected, actual);
    }

    /// <summary>Compares the rows as a set keyed by rowid.</summary>
    public static void EqualIgnoringOrder(ExpectedTable expected, IReadOnlyList<Sqlite3Row> actual)
    {
        List<string> diffs = new List<string>();

        Dictionary<long, Sqlite3Row> actualById = new Dictionary<long, Sqlite3Row>();
        foreach (Sqlite3Row row in actual)
        {
            if (!actualById.TryAdd(row.RowId, row))
                diffs.Add($"rowid {row.RowId}: returned more than once");
        }

        HashSet<long> expectedIds = new HashSet<long>();
        foreach (ExpectedRow row in expected.Rows)
        {
            long rowId = row.RowId!.Value;
            expectedIds.Add(rowId);

            if (!actualById.TryGetValue(rowId, out Sqlite3Row actualRow))
                diffs.Add($"rowid {rowId}: missing");
            else
                CompareColumns(expected, row, actualRow, $"rowid {rowId}", diffs);
        }

        foreach (Sqlite3Row row in actual.Where(s => !expectedIds.Contains(s.RowId)))
            diffs.Add($"rowid {row.RowId}: not in SQLite's result");

        Fail(expected, $"rows differ (expected {expected.Rows.Count} rows, actual {actual.Count})", diffs);
    }

    /// <summary>Checks that the rows come in SQLite's order, ascending rowid.</summary>
    public static void SameRowOrder(ExpectedTable expected, IReadOnlyList<Sqlite3Row> actual)
    {
        long[] expectedIds = expected.Rows.Select(s => s.RowId!.Value).ToArray();
        long[] actualIds = actual.Select(s => s.RowId).ToArray();

        int firstDiff = Enumerable.Range(0, Math.Min(expectedIds.Length, actualIds.Length)).FirstOrDefault(i => expectedIds[i] != actualIds[i], -1);
        if (firstDiff < 0 && expectedIds.Length == actualIds.Length)
            return;

        if (firstDiff < 0)
            firstDiff = Math.Min(expectedIds.Length, actualIds.Length);

        string Window(long[] ids) => string.Join(", ", ids.Skip(Math.Max(0, firstDiff - 2)).Take(6));

        Fail(expected, "row order differs from SQLite (ascending rowid)", new List<string>
        {
            $"first difference at position {firstDiff}",
            $"expected rowids: ... {Window(expectedIds)} ...",
            $"actual rowids:   ... {Window(actualIds)} ..."
        });
    }

    private static void CompareColumns(ExpectedTable table, ExpectedRow expected, Sqlite3Row actual, string where, List<string> diffs)
    {
        if (diffs.Count >= MaxReportedDifferences)
            return;

        if (actual.ColumnData.Length != expected.Values.Length)
        {
            diffs.Add($"{where}: expected {expected.Values.Length} columns, actual {actual.ColumnData.Length} " +
                      $"(expected [{string.Join(", ", expected.Values.Select((_, i) => Describe(ExpectedColumnData(table, expected, i))))}], " +
                      $"actual [{string.Join(", ", actual.ColumnData.Select(Describe))}])");
            return;
        }

        for (int i = 0; i < expected.Values.Length; i++)
        {
            object expectedValue = ExpectedColumnData(table, expected, i);
            if (!ValueEquals(expectedValue, actual.ColumnData[i]))
                diffs.Add($"{where}, column {i} '{table.ColumnNames[i]}': expected {Describe(expectedValue)}, actual {Describe(actual.ColumnData[i])}");
        }
    }

    /// <summary>
    /// Same CLR type and same value. Doubles compare by bit pattern, so -0.0 and 0.0 differ.
    /// </summary>
    public static bool ValueEquals(object expected, object actual)
    {
        switch (expected)
        {
            case null:
                return actual == null;
            case long l:
                return actual is long al && al == l;
            case double d:
                return actual is double ad && BitConverter.DoubleToInt64Bits(ad) == BitConverter.DoubleToInt64Bits(d);
            case string s:
                return actual is string @as && string.Equals(s, @as, StringComparison.Ordinal);
            case byte[] b:
                return actual is byte[] ab && b.AsSpan().SequenceEqual(ab);
            default:
                throw new ArgumentException($"Unexpected expected value of type {expected.GetType()}");
        }
    }

    public static string Describe(object value)
    {
        const int maxShown = 40;

        switch (value)
        {
            case null:
                return "NULL";
            case long l:
                return $"long {l}";
            case double d:
                return $"double {d:R} (0x{BitConverter.DoubleToInt64Bits(d):X16})";
            case string s:
                return $"string[{s.Length}] \"{(s.Length > maxShown ? s.Substring(0, maxShown) + "..." : s)}\"";
            case byte[] b:
                return $"byte[{b.Length}] {Convert.ToHexString(b, 0, Math.Min(b.Length, maxShown / 2))}{(b.Length > maxShown / 2 ? "..." : "")}";
            default:
                return $"{value.GetType().Name} {value}";
        }
    }

    private static void Fail(ExpectedTable table, string what, List<string> diffs)
    {
        if (diffs.Count == 0)
            return;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Table '{table.Name}': {what}:");
        foreach (string diff in diffs.Take(MaxReportedDifferences))
            sb.AppendLine("  " + diff);
        if (diffs.Count >= MaxReportedDifferences)
            sb.AppendLine("  (further differences not shown)");

        throw new XunitException(sb.ToString());
    }
}
