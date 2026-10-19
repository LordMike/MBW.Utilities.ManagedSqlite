using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MBW.Utilities.ManagedSqlite.Differential.Tests.Helpers;

/// <summary>
/// A database file built by real SQLite from a SQL script, deleted again on dispose.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "mbw-managedsqlite-differential");

    public string FilePath { get; }

    private TempDatabase(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>
    /// Runs <paramref name="script"/> against a new, empty database file. Pragmas that must come before the first
    /// write (page_size, encoding, auto_vacuum) can simply be the first statements of the script.
    /// </summary>
    public static TempDatabase Create(string script)
    {
        System.IO.Directory.CreateDirectory(Directory);
        TempDatabase db = new TempDatabase(Path.Combine(Directory, Guid.NewGuid().ToString("N") + ".db"));

        try
        {
            using SqliteConnection connection = db.OpenConnection(SqliteOpenMode.ReadWriteCreate);

            // Not persisted in the file, only makes building the database faster
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA synchronous = OFF;";
                pragma.ExecuteNonQuery();
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();
        }
        catch
        {
            db.Dispose();
            throw;
        }

        return db;
    }

    /// <summary>
    /// Opens a connection to the file. Pooling is off, so disposing the connection really closes the file.
    /// </summary>
    public SqliteConnection OpenConnection(SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = mode,
            Pooling = false
        };

        SqliteConnection connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    public Stream OpenRead()
    {
        return new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    public void Dispose()
    {
        foreach (string file in new[] { FilePath, FilePath + "-journal", FilePath + "-wal", FilePath + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort. A case that timed out may still hold the file open.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
