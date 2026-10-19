using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class TextData_Tests
{
    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public void TextInEachEncoding(string encoding)
    {
        SqliteParity.AssertSameAsSqlite($@"
PRAGMA encoding = '{encoding}';
CREATE TABLE tbl (lang TEXT, v TEXT);
INSERT INTO tbl VALUES ('da', 'Quizdeltagerne spiste jordbær med fløde, mens cirkusklovnen Wolther spillede på xylofon.');
INSERT INTO tbl VALUES ('fr', 'Portez ce vieux whisky au juge blond qui fume sur son île intérieure, à côté de l''alcôve ovoïde, où les bûches se consument dans l''âtre, ce qui lui permet de penser à la cænogenèse de l''être dont il est question dans la cause ambiguë entendue à Moÿ, dans un capharnaüm qui, pense-t-il, diminue çà et là la qualité de son œuvre.');
INSERT INTO tbl VALUES ('iw', 'דג סקרן שט בים מאוכזב ולפתע מצא לו חברה איך הקליטה');
INSERT INTO tbl VALUES ('ru', 'В чащах юга жил бы цитрус? Да, но фальшивый экземпляр!');
INSERT INTO tbl VALUES ('emoji', '😀 outside the BMP');
INSERT INTO tbl VALUES ('empty', '');");
    }
}
