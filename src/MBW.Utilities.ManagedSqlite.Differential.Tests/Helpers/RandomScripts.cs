using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MBW.Utilities.ManagedSqlite.Differential.Tests.Helpers;

/// <summary>
/// Deterministic generator of random schemas and rows for table <c>tbl</c>. The seeds are fixed, so every run
/// produces the same scripts and a failure can be reproduced by its seed.
/// </summary>
internal static class RandomScripts
{
    public static readonly int[] Seeds = { 1, 2, 3, 4, 5, 6, 7, 8 };

    // REAL is left out on purpose: SQLite stores integral values of REAL columns as integers on disk, which is
    // covered by its own named case in TableData_Tests rather than spread over every random case.
    private static readonly string[] DeclaredTypes = { "", "INTEGER", "int", "TEXT", "varchar(20)", "BLOB", "NUMERIC", "BOOLEAN" };

    // 65536 is left out for the same reason: it has its own named case.
    private static readonly int[] PageSizes = { 512, 1024, 4096, 8192 };
    private static readonly string[] Encodings = { "UTF-8", "UTF-16le", "UTF-16be" };

    public static string Generate(int seed)
    {
        Random rnd = new Random(seed);
        StringBuilder sb = new StringBuilder();

        sb.Append($"PRAGMA page_size = {PageSizes[rnd.Next(PageSizes.Length)]}; ");
        sb.Append($"PRAGMA encoding = '{Encodings[rnd.Next(Encodings.Length)]}'; ");

        int columns = rnd.Next(1, 7);
        sb.Append("CREATE TABLE tbl(");
        sb.Append(string.Join(", ", Enumerable.Range(0, columns).Select(i => ("c" + i + " " + DeclaredTypes[rnd.Next(DeclaredTypes.Length)]).TrimEnd())));
        sb.Append("); ");

        bool explicitRowIds = rnd.Next(2) == 0;
        HashSet<long> usedRowIds = new HashSet<long>();
        int rows = rnd.Next(1, 300);

        sb.Append("BEGIN; ");

        for (int r = 0; r < rows; r++)
        {
            List<string> values = Enumerable.Range(0, columns).Select(_ => RandomLiteral(rnd)).ToList();

            if (explicitRowIds)
            {
                long rowId;
                do
                    rowId = RandomInteger(rnd);
                while (!usedRowIds.Add(rowId));

                sb.Append($"INSERT INTO tbl(rowid, {string.Join(", ", Enumerable.Range(0, columns).Select(i => "c" + i))}) VALUES ({rowId.ToString(CultureInfo.InvariantCulture)}, {string.Join(", ", values)}); ");
            }
            else
            {
                sb.Append($"INSERT INTO tbl VALUES ({string.Join(", ", values)}); ");
            }
        }

        sb.Append("COMMIT; ");

        // Some deletes, so the tree has holes and possibly free pages
        if (rnd.Next(2) == 0)
            sb.Append($"DELETE FROM tbl WHERE rowid % {rnd.Next(2, 6)} = 0; ");

        return sb.ToString();
    }

    private static string RandomLiteral(Random rnd)
    {
        switch (rnd.Next(10))
        {
            case 0:
                return "NULL";
            case 1:
            case 2:
                return RandomInteger(rnd).ToString(CultureInfo.InvariantCulture);
            case 3:
            case 4:
                return RandomReal(rnd);
            case 5:
            case 6:
            case 7:
                return "'" + RandomText(rnd).Replace("'", "''") + "'";
            default:
                byte[] blob = new byte[RandomLength(rnd)];
                rnd.NextBytes(blob);
                return "X'" + Convert.ToHexString(blob) + "'";
        }
    }

    /// <summary>Integers of every serial type width: constants 0/1, then 1, 2, 3, 4, 6 and 8 bytes.</summary>
    private static long RandomInteger(Random rnd)
    {
        int bits = new[] { 1, 7, 15, 23, 31, 47, 63 }[rnd.Next(7)];
        long magnitude = rnd.NextInt64(0, bits == 63 ? long.MaxValue : 1L << bits);
        return rnd.Next(2) == 0 ? magnitude : -magnitude - 1;
    }

    private static string RandomReal(Random rnd)
    {
        double value = (rnd.NextDouble() - 0.5) * Math.Pow(10, rnd.Next(-300, 300));
        string literal = value.ToString("R", CultureInfo.InvariantCulture);

        // Keep it a REAL literal for SQLite; "5" would be an integer
        return literal.Contains('.') || literal.Contains('E') ? literal : literal + ".0";
    }

    private static int RandomLength(Random rnd)
    {
        // Mostly short values, sometimes long enough to overflow even 8 KB pages
        return rnd.Next(20) == 0 ? rnd.Next(1000, 9000) : rnd.Next(0, 60);
    }

    private static string RandomText(Random rnd)
    {
        int length = RandomLength(rnd);
        StringBuilder sb = new StringBuilder(length);

        for (int i = 0; i < length; i++)
        {
            switch (rnd.Next(6))
            {
                case 0:
                    sb.Append((char)rnd.Next(0x00E0, 0x0180)); // Latin extended
                    break;
                case 1:
                    sb.Append((char)rnd.Next(0x4E00, 0x9FFF)); // CJK
                    break;
                case 2:
                    sb.Append(char.ConvertFromUtf32(rnd.Next(0x1F600, 0x1F650))); // Emoji, surrogate pairs in UTF-16
                    break;
                default:
                    sb.Append((char)rnd.Next(0x20, 0x7F)); // Printable ASCII
                    break;
            }
        }

        return sb.ToString();
    }
}
