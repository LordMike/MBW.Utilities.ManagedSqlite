using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Tests.Helpers;
using SQLitePCL;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Tests;

/// <summary>
/// Each case runs a script against a database file with real SQLite, copies the files as a crash at the script's end
/// would leave them, and compares what SQLite reads from the copy with what <see cref="Sqlite3Database.FromPath"/>
/// reads. A script that ends inside a transaction leaves it uncommitted.
/// </summary>
public class Journal_Tests
{
    // 500 rows of 900 bytes are about 120 pages of 4096 bytes, far more than the cache of 10 pages, so the pages spill
    // to the database file (rollback mode) or to the WAL (WAL mode) before the transaction ends
    private const string ManyRows = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500) INSERT INTO t (b) SELECT printf('%.900c', char(64 + i % 26)) FROM n;";

    // Rewrites every other row, so the pages a crashed transaction spilled to the database file are pages that existed
    private const string ChangeRows = "UPDATE t SET b = 'changed' WHERE a % 2 = 0;";

    private const string Wal = "PRAGMA journal_mode = WAL; PRAGMA cache_size = 10; CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT); INSERT INTO t (b) VALUES ('one'), ('two');";

    private const string Rollback = "PRAGMA journal_mode = DELETE; PRAGMA cache_size = 10; CREATE TABLE t (a INTEGER PRIMARY KEY, b TEXT); INSERT INTO t (b) VALUES ('one'), ('two');";

    private const string Checkpoint = "PRAGMA wal_checkpoint(TRUNCATE);";

    public static TheoryData<string, string, bool> WalCases() => new()
    {
        { "rows only in the WAL", Wal + Checkpoint + "INSERT INTO t (b) VALUES ('three');", true },
        { "page 1 only in the WAL", Wal + Checkpoint + "CREATE TABLE u (x); INSERT INTO u VALUES (1);", true },
        { "the WAL grows the database", Wal + Checkpoint + ManyRows, true },
        { "the WAL shrinks the database", Wal + ManyRows + Checkpoint + "DELETE FROM t WHERE a > 2; VACUUM;", true },
        { "later commits overwrite a page", Wal + Checkpoint + "UPDATE t SET b = 'uno' WHERE a = 1; UPDATE t SET b = 'eins' WHERE a = 1; DELETE FROM t WHERE a = 2;", true },
        { "uncommitted frames after the last commit", Wal + Checkpoint + "INSERT INTO t (b) VALUES ('three'); BEGIN; " + ManyRows, true },
        { "a restarted WAL with stale frames after it", Wal + ManyRows + "PRAGMA wal_checkpoint(RESTART); INSERT INTO t (b) VALUES ('three');", true },
        { "64 KiB pages", "PRAGMA page_size = 65536;" + Wal + Checkpoint + ManyRows, true },
        { "an empty WAL after a checkpoint", Wal + ManyRows + Checkpoint, false },
    };

    [Theory]
    [MemberData(nameof(WalCases))]
    public void WalMode(string name, string script, bool walMatters)
    {
        _ = name;
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot(script);

        SqliteParity.AssertSameAsSqlite(snapshot, walMatters);
    }

    public static TheoryData<string, string, bool> RollbackCases() => new()
    {
        { "a crashed write that grew the file", Rollback + ManyRows + "BEGIN; " + ChangeRows + ManyRows, true },
        { "a crashed schema change", Rollback + ManyRows + "BEGIN; CREATE TABLE u (x); " + ChangeRows, true },
        { "a crashed delete", Rollback + ManyRows + "BEGIN; DELETE FROM t WHERE a % 3 = 0;", true },
        { "synchronous off, so the record count is unknown", "PRAGMA synchronous = OFF;" + Rollback + ManyRows + "BEGIN; " + ChangeRows, true },
        { "a 512-byte page journal", "PRAGMA page_size = 512;" + Rollback + ManyRows + "BEGIN; " + ChangeRows, true },
        { "a PERSIST journal after a commit", Rollback.Replace("DELETE", "PERSIST") + ManyRows, false },
        { "a TRUNCATE journal after a commit", Rollback.Replace("DELETE", "TRUNCATE") + ManyRows, false },
    };

    [Theory]
    [MemberData(nameof(RollbackCases))]
    public void RollbackJournal(string name, string script, bool journalMatters)
    {
        _ = name;
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot(script);

        SqliteParity.AssertSameAsSqlite(snapshot, journalMatters);
    }

    /// <summary>
    /// Damage SQLite itself tolerates: the WAL or journal is read up to the damage, or ignored. Where it is read up to
    /// the damage, the case also asserts that the part before it is read.
    /// </summary>
    public static TheoryData<string, string, Action<string>, bool> DamagedCases() => new()
    {
        // The second transaction's first frame fails its checksum, so it and every later frame are ignored
        { "a WAL frame with a bad checksum", Wal + Checkpoint + "INSERT INTO t (b) VALUES ('three'); INSERT INTO t (b) VALUES ('four');", db => Patch(db + "-wal", 32 + WalFrameSize(db) + 24 + 100, 0x55), true },
        { "a WAL cut inside its last frame", Wal + Checkpoint + "INSERT INTO t (b) VALUES ('three'); INSERT INTO t (b) VALUES ('four');", db => Truncate(db + "-wal", new FileInfo(db + "-wal").Length - 10), true },
        { "a WAL with no commit frame", Wal + Checkpoint + ManyRows, db => Truncate(db + "-wal", 32 + WalFrameSize(db) * 2), false },
        { "a WAL with a bad header checksum", Wal + Checkpoint + "INSERT INTO t (b) VALUES ('three');", db => Patch(db + "-wal", 16, 0x55), false },
        { "a WAL with big-endian checksums", Wal + Checkpoint + ManyRows, db => RewriteWalChecksums(db + "-wal", bigEndian: true), true },
        { "a journal record with a bad checksum", Rollback + ManyRows + "BEGIN; " + ChangeRows, db => Patch(db + "-journal", 512 + 4 + 4096 - 200, 0x55), false },
        { "a journal shorter than a sector", Rollback + ManyRows + "BEGIN; " + ChangeRows, db => Truncate(db + "-journal", 100), false },
    };

    [Theory]
    [MemberData(nameof(DamagedCases))]
    public void DamagedSideFile(string name, string script, Action<string> damage, bool partlyRead)
    {
        _ = name;
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot(script);
        damage(snapshot.Database);

        SqliteParity.AssertSameAsSqlite(snapshot, partlyRead);
    }

    /// <summary>
    /// SQLite fails to open a WAL in a format version it does not know (SQLITE_CANTOPEN), and so does the library
    /// </summary>
    [Fact]
    public void WalFormatVersionMustBeKnown()
    {
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot(Wal + Checkpoint + "INSERT INTO t (b) VALUES ('three');");
        string wal = snapshot.Database + "-wal";
        byte[] bytes = File.ReadAllBytes(wal);
        bytes[7]++;
        File.WriteAllBytes(wal, bytes);
        RewriteWalChecksums(wal, bigEndian: false);

        SqliteParity.AssertSqliteFails(snapshot, raw.SQLITE_CANTOPEN);
        Assert.Equal("WALJ-11", Assert.Throws<SqliteCorruptException>(() => Sqlite3Database.FromPath(snapshot.Database)).Check);

        // Not relaxable: there is no way to read a WAL of an unknown format
        Sqlite3Settings lenient = new Sqlite3Settings { Validation = SqliteValidation.Lenient };
        Assert.Equal("WALJ-11", Assert.Throws<SqliteCorruptException>(() => Sqlite3Database.FromPath(snapshot.Database, lenient)).Check);
    }

    /// <summary>
    /// A WAL whose page size differs from the database's can only come from another database (WALJ-28). This is a
    /// deliberate difference: SQLite reads such frames without an error, leaving part of each page undefined. A WAL that
    /// holds page 1 carries its own page size in that page's header, so the check must not read the header from it.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO t (b) VALUES ('three');")]
    [InlineData("CREATE TABLE u (x);")]
    public void WalPageSizeMustMatchTheDatabase(string otherWrite)
    {
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot(Wal + Checkpoint + "INSERT INTO t (b) VALUES ('three');");
        using SqliteParity.SnapshotDirectory other = SqliteParity.Snapshot("PRAGMA page_size = 1024;" + Wal + Checkpoint + otherWrite);
        File.Copy(other.Database + "-wal", snapshot.Database + "-wal", true);

        Assert.Equal("WALJ-28", Assert.Throws<SqliteCorruptException>(() => Sqlite3Database.FromPath(snapshot.Database)).Check);

        // Lenient reports it and reads the main file alone
        List<string> issues = new List<string>();
        Sqlite3Settings lenient = new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) };
        using (Sqlite3Database db = Sqlite3Database.FromPath(snapshot.Database, lenient))
            Assert.Equal(2, db.GetTable("t").EnumerateRows().Count());
        Assert.Equal(new[] { "WALJ-28" }, issues);
    }

    /// <summary>
    /// A main file whose page size field is damaged gives nothing to compare the WAL with (WALJ-28). SQLite then takes the
    /// page size from page 1 in the WAL, and reads the database.
    /// </summary>
    [Fact]
    public void DamagedPageSizeIsReadFromTheWal()
    {
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot("PRAGMA page_size = 1024;" + Wal + Checkpoint + "CREATE TABLE u (x); INSERT INTO u VALUES (1);");
        using (FileStream fs = new FileStream(snapshot.Database, FileMode.Open, FileAccess.Write))
        {
            fs.Position = 16;
            fs.Write(new byte[2], 0, 2);
        }

        SqliteParity.AssertSameAsSqlite(snapshot, true);
    }

    /// <summary>
    /// WALJ-28 also holds for the page size the database is read with: page 1 in the WAL giving another page size than
    /// the WAL's own fails, where the main file has none to compare with
    /// </summary>
    [Fact]
    public void WalPageOneMustMatchTheWalPageSize()
    {
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot("PRAGMA page_size = 1024;" + Wal + Checkpoint + "CREATE TABLE u (x); INSERT INTO u VALUES (1);");
        using (FileStream fs = new FileStream(snapshot.Database, FileMode.Open, FileAccess.Write))
        {
            fs.Position = 16;
            fs.Write(new byte[2], 0, 2);
        }

        string wal = snapshot.Database + "-wal";
        byte[] bytes = File.ReadAllBytes(wal);
        int frameSize = WalFrameSize(snapshot.Database);
        for (int frame = 32; frame + frameSize <= bytes.Length; frame += frameSize)
        {
            if (bytes[frame] == 0 && bytes[frame + 1] == 0 && bytes[frame + 2] == 0 && bytes[frame + 3] == 1)
                bytes[frame + 24 + 16] = 0x10;
        }
        File.WriteAllBytes(wal, bytes);
        RewriteWalChecksums(wal, bigEndian: false);

        Assert.Equal("WALJ-28", Assert.Throws<SqliteCorruptException>(() => Sqlite3Database.FromPath(snapshot.Database)).Check);
    }

    /// <summary>
    /// WALJ-26: the header on the WAL's page 1 says the database has more pages than the WAL's last commit. SQLite fails
    /// (SQLITE_CORRUPT); the pages that exist are intact, so Lenient reports it and reads on.
    /// </summary>
    [Fact]
    public void HeaderPageCountMustFitTheWal()
    {
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot(Wal + Checkpoint + "CREATE TABLE u (x); INSERT INTO u VALUES (1);");
        string wal = snapshot.Database + "-wal";
        byte[] bytes = File.ReadAllBytes(wal);
        int frameSize = WalFrameSize(snapshot.Database);
        for (int frame = 32; frame + frameSize <= bytes.Length; frame += frameSize)
        {
            if (bytes[frame] == 0 && bytes[frame + 1] == 0 && bytes[frame + 2] == 0 && bytes[frame + 3] == 1)
                bytes[frame + 24 + 30] += 100;
        }

        File.WriteAllBytes(wal, bytes);
        RewriteWalChecksums(wal, bigEndian: false);

        SqliteParity.AssertSqliteFails(snapshot, raw.SQLITE_CORRUPT);
        Assert.Equal("WALJ-26", Assert.Throws<SqliteCorruptException>(() => Sqlite3Database.FromPath(snapshot.Database)).Check);

        List<string> issues = new List<string>();
        Sqlite3Settings lenient = new Sqlite3Settings { Validation = SqliteValidation.Lenient, OnValidationIssue = i => issues.Add(i.Check) };
        using (Sqlite3Database db = Sqlite3Database.FromPath(snapshot.Database, lenient))
            Assert.Single(db.GetTable("u").EnumerateRows());
        Assert.Equal(new[] { "WALJ-26" }, issues);
    }

    /// <summary>
    /// WALJ-43 to WALJ-45: a journal of a transaction over several databases names a super-journal. SQLite plays it back
    /// only while that file exists; the library reads one database and always plays it back (a documented difference).
    /// </summary>
    [Fact]
    public void SuperJournalIsIgnored()
    {
        using SqliteParity.SnapshotDirectory snapshot = SqliteParity.Snapshot(Rollback + ManyRows + "BEGIN; " + ChangeRows);
        string superJournal = snapshot.Database + "-mjæ0123";
        AppendSuperJournal(snapshot.Database + "-journal", superJournal);

        // While the super-journal exists, SQLite plays the journal back too. The name in the journal points at the
        // original directory, so SQLite, which reads a copy, looks up this file.
        File.WriteAllBytes(superJournal, new byte[] { 1 });
        SqliteParity.AssertSameAsSqlite(snapshot, true);

        // Without it, the journal is still played back, so no row reads "changed"
        File.Delete(superJournal);
        using Sqlite3Database db = Sqlite3Database.FromPath(snapshot.Database);
        Assert.DoesNotContain(db.GetTable("t").EnumerateRows(), r => (string)r.ColumnData[1] == "changed");
    }

    /// <summary>
    /// Appends a super-journal record to a journal, as writeSuperJournal in pager.c writes it: the lock-byte page number,
    /// the name, its length, the sum of its bytes as signed chars, and the journal magic
    /// </summary>
    private static void AppendSuperJournal(string journal, string name)
    {
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        uint checksum = 0;
        foreach (byte b in nameBytes)
            checksum += (uint)(sbyte)b;

        using FileStream fs = new FileStream(journal, FileMode.Append, FileAccess.Write);
        byte[] record = new byte[4 + nameBytes.Length + 16];
        WriteBigEndian(record, 0, 0x40000000 / 4096 + 1);
        nameBytes.CopyTo(record, 4);
        WriteBigEndian(record, 4 + nameBytes.Length, (uint)nameBytes.Length);
        WriteBigEndian(record, 8 + nameBytes.Length, checksum);
        new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }.CopyTo(record, 12 + nameBytes.Length);
        fs.Write(record, 0, record.Length);
    }

    /// <summary>
    /// The size of a WAL frame: its 24-byte header and a page of the size in the WAL header
    /// </summary>
    private static int WalFrameSize(string db)
    {
        byte[] wal = File.ReadAllBytes(db + "-wal");
        return (wal[8] << 24 | wal[9] << 16 | wal[10] << 8 | wal[11]) + 24;
    }

    private static void Patch(string path, long offset, byte xor)
    {
        using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Position = offset;
        int value = fs.ReadByte();
        fs.Position = offset;
        fs.WriteByte((byte)(value ^ xor));
    }

    private static void Truncate(string path, long length)
    {
        using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.SetLength(length);
    }

    /// <summary>
    /// Sets the WAL's checksum byte order (the low bit of the magic) and recomputes every checksum, header and frames,
    /// in that order, as SQLite would have written them on a machine of that byte order
    /// </summary>
    private static void RewriteWalChecksums(string path, bool bigEndian)
    {
        byte[] wal = File.ReadAllBytes(path);
        wal[3] = (byte)(bigEndian ? 0x83 : 0x82);
        int pageSize = wal[8] << 24 | wal[9] << 16 | wal[10] << 8 | wal[11];

        uint s1 = 0, s2 = 0;
        Checksum(wal, 0, 24, bigEndian, ref s1, ref s2);
        WriteBigEndian(wal, 24, s1);
        WriteBigEndian(wal, 28, s2);

        for (int frame = 32; frame + 24 + pageSize <= wal.Length; frame += 24 + pageSize)
        {
            Checksum(wal, frame, 8, bigEndian, ref s1, ref s2);
            Checksum(wal, frame + 24, pageSize, bigEndian, ref s1, ref s2);
            WriteBigEndian(wal, frame + 16, s1);
            WriteBigEndian(wal, frame + 20, s2);
        }

        File.WriteAllBytes(path, wal);
    }

    private static void Checksum(byte[] data, int offset, int length, bool bigEndian, ref uint s1, ref uint s2)
    {
        for (int i = offset; i < offset + length; i += 8)
        {
            s1 += Word(data, i, bigEndian) + s2;
            s2 += Word(data, i + 4, bigEndian) + s1;
        }
    }

    private static uint Word(byte[] data, int i, bool bigEndian) => bigEndian
        ? (uint)(data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3])
        : (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);

    private static void WriteBigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
