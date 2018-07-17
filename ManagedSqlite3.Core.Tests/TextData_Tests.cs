using System;
using System.Collections.Generic;
using System.IO;
using ManagedSqlite3.Core.Objects.Enums;
using ManagedSqlite3.Core.Tables;
using ManagedSqlite3.Core.Tests.Helpers;
using Xunit;

namespace ManagedSqlite3.Core.Tests
{
    public class TextData_Tests
    {
        private static readonly List<Tuple<int, string, string>> TestData = new List<Tuple<int, string, string>>
        {
            Tuple.Create(0, "da", "Quizdeltagerne spiste jordbær med fløde, mens cirkusklovnen Wolther spillede på xylofon."),
            Tuple.Create(1, "es", "El pingüino Wenceslao hizo kilómetros bajo exhaustiva lluvia y frío, añoraba a su querido cachorro."),
            Tuple.Create(2, "fr", "Portez ce vieux whisky au juge blond qui fume sur son île intérieure, à côté de l'alcôve ovoïde, où les bûches se consument dans l'âtre, ce qui lui permet de penser à la cænogenèse de l'être dont il est question dans la cause ambiguë entendue à Moÿ, dans un capharnaüm qui, pense-t-il, diminue çà et là la qualité de son œuvre."),
            Tuple.Create(3, "iw", "דג סקרן שט בים מאוכזב ולפתע מצא לו חברה איך הקליטה"),
            Tuple.Create(4, "ru", "В чащах юга жил бы цитрус? Да, но фальшивый экземпляр!")
        };

        private static Stream Get(SqliteEncoding encoding)
        {
            return ResourceHelper.OpenResource($"Sqlite3RoLib.Tests.Data.DB-{encoding}.db");
        }

        public static IEnumerable<object[]> TestDataEmitter()
        {
            foreach (Tuple<int, string, string> row in TestData)
            {
                yield return new object[] { row.Item1, row.Item2, row.Item3 };
            }
        }

        public static IEnumerable<object[]> TestDataEmitterAllEncodings()
        {
            foreach (SqliteEncoding encoding in Enum.GetValues(typeof(SqliteEncoding)))
            {
                foreach (Tuple<int, string, string> row in TestData)
                {
                    yield return new object[] { encoding, row.Item1, row.Item2, row.Item3 };
                }
            }
        }

        [Theory]
        [InlineData(SqliteEncoding.UTF8)]
        [InlineData(SqliteEncoding.UTF16LE)]
        [InlineData(SqliteEncoding.UTF16BE)]
        public void TestEncodingSet(SqliteEncoding encoding)
        {
            using (Sqlite3Database db = new Sqlite3Database(Get(encoding)))
            {
                Assert.Equal(encoding, db.Header.TextEncoding);
            }
        }

        [Theory]
        [MemberData(nameof(TestDataEmitterAllEncodings))]
        public void TestStringDataContents(SqliteEncoding encoding, int id, string expectedLanguage, string expectedString)
        {
            using (Sqlite3Database db = new Sqlite3Database(Get(encoding)))
            {
                Sqlite3Table tbl = db.GetTable("EncodingTable" + encoding);
                Sqlite3Row row = tbl.GetRowById(id);

                Assert.NotNull(row);

                Assert.True(row.TryGetOrdinal(1, out string actualLang));
                Assert.True(row.TryGetOrdinal(2, out string actualText));
                Assert.Equal(expectedLanguage, actualLang);
                Assert.Equal(expectedString, actualText);
            }
        }
    }
}