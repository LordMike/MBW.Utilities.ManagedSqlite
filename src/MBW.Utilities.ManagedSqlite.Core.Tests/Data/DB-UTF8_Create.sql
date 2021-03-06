-- sqlite3 DB-UTF8.db ".read DB-UTF8_Create.sql"

PRAGMA encoding = "utf-8";

CREATE TABLE EncodingTableUTF8 (Id INTEGER PRIMARY KEY, Language TEXT, Value TEXT);

INSERT INTO EncodingTableUTF8 VALUES (0, "da", "Quizdeltagerne spiste jordbær med fløde, mens cirkusklovnen Wolther spillede på xylofon.");
INSERT INTO EncodingTableUTF8 VALUES (1, "es", "El pingüino Wenceslao hizo kilómetros bajo exhaustiva lluvia y frío, añoraba a su querido cachorro.");
INSERT INTO EncodingTableUTF8 VALUES (2, "fr", "Portez ce vieux whisky au juge blond qui fume sur son île intérieure, à côté de l'alcôve ovoïde, où les bûches se consument dans l'âtre, ce qui lui permet de penser à la cænogenèse de l'être dont il est question dans la cause ambiguë entendue à Moÿ, dans un capharnaüm qui, pense-t-il, diminue çà et là la qualité de son œuvre.");
INSERT INTO EncodingTableUTF8 VALUES (3, "iw", "דג סקרן שט בים מאוכזב ולפתע מצא לו חברה איך הקליטה");
INSERT INTO EncodingTableUTF8 VALUES (4, "ru", "В чащах юга жил бы цитрус? Да, но фальшивый экземпляр!");

