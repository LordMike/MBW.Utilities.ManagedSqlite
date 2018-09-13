using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests
{
    public class SqlCreateStatements
    {
        public static IEnumerable<object[]> CreateStatementsTestsData()
        {
            yield return new object[]{ "CREATE TABLE cookies (creation_utc INTEGER NOT NULL,host_key TEXT NOT NULL,name TEXT NOT NULL,value TEXT NOT NULL,path TEXT NOT NULL,expires_utc INTEGER NOT NULL,is_secure INTEGER NOT NULL,is_httponly INTEGER NOT NULL,last_access_utc INTEGER NOT NULL, has_expires INTEGER NOT NULL DEFAULT 1, is_persistent INTEGER NOT NULL DEFAULT 1,priority INTEGER NOT NULL DEFAULT 1,encrypted_value BLOB DEFAULT '',firstpartyonly INTEGER NOT NULL DEFAULT 0,UNIQUE (host_key, name, path));",
                    "cookies",
                    new[]
                    {
                        Integer("creation_utc", "INTEGER"),
                        Text("host_key", "TEXT"),
                        Text("name", "TEXT"),
                        Text("value", "TEXT"),
                        Text("path", "TEXT"),
                        Integer("expires_utc", "INTEGER"),
                        Integer("is_secure", "INTEGER"),
                        Integer("is_httponly", "INTEGER"),
                        Integer("last_access_utc", "INTEGER"),
                        Integer("has_expires", "INTEGER"),
                        Integer("is_persistent", "INTEGER"),
                        Integer("priority", "INTEGER"),
                        Bytes("encrypted_value", "BLOB"),
                        Integer("firstpartyonly", "INTEGER")
                    }
            };

            yield return new object[]{ "CREATE TABLE downloads (id INTEGER PRIMARY KEY,guid VARCHAR NOT NULL,current_path LONGVARCHAR NOT NULL,target_path LONGVARCHAR NOT NULL,start_time INTEGER NOT NULL,received_bytes INTEGER NOT NULL,total_bytes INTEGER NOT NULL,state INTEGER NOT NULL,danger_type INTEGER NOT NULL,interrupt_reason INTEGER NOT NULL,hash BLOB NOT NULL,end_time INTEGER NOT NULL,opened INTEGER NOT NULL,last_access_time INTEGER NOT NULL,transient INTEGER NOT NULL,referrer VARCHAR NOT NULL,site_url VARCHAR NOT NULL,tab_url VARCHAR NOT NULL,tab_referrer_url VARCHAR NOT NULL,http_method VARCHAR NOT NULL,by_ext_id VARCHAR NOT NULL,by_ext_name VARCHAR NOT NULL,etag VARCHAR NOT NULL,last_modified VARCHAR NOT NULL,mime_type VARCHAR(255) NOT NULL,original_mime_type VARCHAR(255) NOT NULL);",
                    "downloads",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Text("guid", "VARCHAR"),
                        Text("current_path", "LONGVARCHAR"),
                        Text("target_path", "LONGVARCHAR"),
                        Integer("start_time", "INTEGER"),
                        Integer("received_bytes", "INTEGER"),
                        Integer("total_bytes", "INTEGER"),
                        Integer("state", "INTEGER"),
                        Integer("danger_type", "INTEGER"),
                        Integer("interrupt_reason", "INTEGER"),
                        Bytes("hash", "BLOB"),
                        Integer("end_time", "INTEGER"),
                        Integer("opened", "INTEGER"),
                        Integer("last_access_time", "INTEGER"),
                        Integer("transient", "INTEGER"),
                        Text("referrer", "VARCHAR"),
                        Text("site_url", "VARCHAR"),
                        Text("tab_url", "VARCHAR"),
                        Text("tab_referrer_url", "VARCHAR"),
                        Text("http_method", "VARCHAR"),
                        Text("by_ext_id", "VARCHAR"),
                        Text("by_ext_name", "VARCHAR"),
                        Text("etag", "VARCHAR"),
                        Text("last_modified", "VARCHAR"),
                        Text("mime_type", "VARCHAR"),
                        Text("original_mime_type", "VARCHAR")
                    }
            };

            yield return new object[]{ "CREATE TABLE downloads_slices (download_id INTEGER NOT NULL,offset INTEGER NOT NULL,received_bytes INTEGER NOT NULL, finished INTEGER NOT NULL DEFAULT 0,PRIMARY KEY (download_id, offset) );",
                    "downloads_slices",
                    new[]
                    {
                        Integer("download_id", "INTEGER", true),
                        Integer("offset", "INTEGER", true),
                        Integer("received_bytes", "INTEGER"),
                        Integer("finished", "INTEGER")
                    }
            };

            yield return new object[]{ "CREATE TABLE downloads_url_chains (id INTEGER NOT NULL,chain_index INTEGER NOT NULL,url LONGVARCHAR NOT NULL, PRIMARY KEY (id, chain_index) );",
                "downloads_url_chains",
                new[]
                {
                    Integer("id", "INTEGER", true),
                    Integer("chain_index", "INTEGER", true),
                    Text("url", "LONGVARCHAR")
                }
            };

            yield return new object[]{ "CREATE TABLE favicon_bitmaps(id INTEGER PRIMARY KEY,icon_id INTEGER NOT NULL,last_updated INTEGER DEFAULT 0,image_data BLOB,width INTEGER DEFAULT 0,height INTEGER DEFAULT 0,last_requested INTEGER DEFAULT 0);",
                    "favicon_bitmaps",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Integer("icon_id", "INTEGER"),
                        Integer("last_updated", "INTEGER"),
                        Bytes("image_data", "BLOB"),
                        Integer("width", "INTEGER"),
                        Integer("height", "INTEGER"),
                        Integer("last_requested", "INTEGER")
                    }
            };

            yield return new object[]{ "CREATE TABLE favicons(id INTEGER PRIMARY KEY,url LONGVARCHAR NOT NULL,icon_type INTEGER DEFAULT 1);",
                    "favicons",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Text("url", "LONGVARCHAR"),
                        Integer("icon_type", "INTEGER"),
                    }
            };

            yield return new object[]{ "CREATE TABLE icon_mapping(id INTEGER PRIMARY KEY,page_url LONGVARCHAR NOT NULL,icon_id INTEGER);",
                    "icon_mapping",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Text("page_url", "LONGVARCHAR"),
                        Integer("icon_id", "INTEGER")
                    }
            };

            yield return new object[]{ "CREATE TABLE keyword_search_terms (keyword_id INTEGER NOT NULL,url_id INTEGER NOT NULL,lower_term LONGVARCHAR NOT NULL,term LONGVARCHAR NOT NULL);",
                    "keyword_search_terms",
                    new[]
                    {
                        Integer("keyword_id", "INTEGER"),
                        Integer("url_id", "INTEGER"),
                        Text("lower_term", "LONGVARCHAR"),
                        Text("term", "LONGVARCHAR")
                    }
            };

            yield return new object[]{ "CREATE TABLE meta(key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY, value LONGVARCHAR);",
                    "meta",
                    new[]
                    {
                        Text("key", "LONGVARCHAR", true),
                        Text("value", "LONGVARCHAR"),
                    }
            };

            yield return new object[]{ "CREATE TABLE segment_usage (id INTEGER PRIMARY KEY,segment_id INTEGER NOT NULL,time_slot INTEGER NOT NULL,visit_count INTEGER DEFAULT 0 NOT NULL);",
                    "segment_usage",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Integer("segment_id", "INTEGER"),
                        Integer("time_slot", "INTEGER"),
                        Integer("visit_count", "INTEGER")
                    }
            };

            yield return new object[]{ "CREATE TABLE segments (id INTEGER PRIMARY KEY,name VARCHAR,url_id INTEGER NON NULL);",
                    "segments",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Text("name", "VARCHAR"),
                        Integer("url_id", "INTEGER")
                    }
            };

            yield return new object[]{ "CREATE TABLE typed_url_sync_metadata (storage_key INTEGER PRIMARY KEY NOT NULL,value BLOB);",
                    "typed_url_sync_metadata",
                    new[]
                    {
                        Integer("storage_key", "INTEGER", true, true),
                        Bytes("value", "BLOB")
                    }
            };

            yield return new object[]{ "CREATE TABLE urls(id INTEGER PRIMARY KEY AUTOINCREMENT,url LONGVARCHAR,title LONGVARCHAR,visit_count INTEGER DEFAULT 0 NOT NULL,typed_count INTEGER DEFAULT 0 NOT NULL,last_visit_time INTEGER NOT NULL,hidden INTEGER DEFAULT 0 NOT NULL);",
                    "urls",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Text("url", "LONGVARCHAR"),
                        Text("title", "LONGVARCHAR"),
                        Integer("visit_count", "INTEGER"),
                        Integer("typed_count", "INTEGER"),
                        Integer("last_visit_time", "INTEGER"),
                        Integer("hidden", "INTEGER")
                    }
            };

            yield return new object[]{ "CREATE TABLE visit_source(id INTEGER PRIMARY KEY,source INTEGER NOT NULL);",
                    "visit_source",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Integer("source", "INTEGER"),
                    }
            };

            yield return new object[]{ "CREATE TABLE visits(id INTEGER PRIMARY KEY,url INTEGER NOT NULL,visit_time INTEGER NOT NULL,from_visit INTEGER,transition INTEGER DEFAULT 0 NOT NULL,segment_id INTEGER,visit_duration INTEGER DEFAULT 0 NOT NULL, incremented_omnibox_typed_score BOOLEAN DEFAULT FALSE NOT NULL);",
                    "visits",
                    new[]
                    {
                        Integer("id", "INTEGER", true, true),
                        Integer("url", "INTEGER"),
                        Integer("visit_time", "INTEGER"),
                        Integer("from_visit", "INTEGER"),
                        Integer("transition", "INTEGER"),
                        Integer("segment_id", "INTEGER"),
                        Integer("visit_duration", "INTEGER"),
                        Integer("incremented_omnibox_typed_score", "BOOLEAN")
                    }
            };
        }

        [Theory]
        [MemberData(nameof(CreateStatementsTestsData))]
        public void CreateStatementsTests(string sql, string tableName, params ExpectedColumn[] expectedColumns)
        {
            Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));
            Assert.NotNull(definition);

            Assert.Equal(tableName, definition.TableName);
            Assert.Equal(expectedColumns.Length, definition.Columns.Count);

            if (expectedColumns.All(s => !s.IsRowId))
                Assert.Null(definition.RowIdColumn);

            for (int i = 0; i < expectedColumns.Length; i++)
            {
                SqlTableColumn actual = definition.Columns.ElementAt(i);
                ExpectedColumn expected = expectedColumns[i];

                Assert.Equal(expected.ClrType, actual.DetectedType);
                Assert.Equal(expected.Name, actual.Name);
                Assert.Equal(expected.SqlType, actual.TypeName);
                Assert.Equal(expected.IsPrimaryKey, actual.IsPartOfPrimaryKey);

                if (expected.IsRowId)
                    Assert.Same(actual, definition.RowIdColumn);
            }
        }

        static ExpectedColumn Text(string name, string sqlType, bool isPrimaryKey = false, bool isRowId = false)
        {
            return new ExpectedColumn(name, sqlType, typeof(string), isPrimaryKey, isRowId);
        }

        static ExpectedColumn Integer(string name, string sqlType, bool isPrimaryKey = false, bool isRowId = false)
        {
            return new ExpectedColumn(name, sqlType, typeof(long), isPrimaryKey, isRowId);
        }

        static ExpectedColumn Bytes(string name, string sqlType, bool isPrimaryKey = false, bool isRowId = false)
        {
            return new ExpectedColumn(name, sqlType, typeof(byte[]), isPrimaryKey, isRowId);
        }

        public class ExpectedColumn
        {
            public string Name { get; }
            public string SqlType { get; }
            public Type ClrType { get; }
            public bool IsPrimaryKey { get; }
            public bool IsRowId { get; }

            public ExpectedColumn(string name, string sqlType, Type clrType, bool isPrimaryKey, bool isRowId)
            {
                Name = name;
                SqlType = sqlType;
                ClrType = clrType;
                IsPrimaryKey = isPrimaryKey;
                IsRowId = isRowId;
            }
        }
    }
}
