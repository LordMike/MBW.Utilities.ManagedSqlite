-- sqlite3 IntegerData.db ".read IntegerData_Create.sql"

create table IntegerTable (Id INTEGER PRIMARY KEY, MyInt INTEGER);
insert into IntegerTable values(0, 0);          			-- 0x                 00
insert into IntegerTable values(1, 1);          			-- 0x                 01
insert into IntegerTable values(2, 255);        			-- 0x                 FF
insert into IntegerTable values(3, 256);        			-- 0x               0100
insert into IntegerTable values(4, 65535);      			-- 0x          0000 FFFF
insert into IntegerTable values(5, 65536);					-- 0x          0001 0000
insert into IntegerTable values(6, 16777215);				-- 0x          00FF FFFF
insert into IntegerTable values(7, 16777216);   			-- 0x          0100 0000
insert into IntegerTable values(8, 4294967295);				-- 0x          FFFF FFFF
insert into IntegerTable values(9, 4294967296); 			-- 0x        1 0000 0000
insert into IntegerTable values(10, 1099511627775);			-- 0x       FF FFFF FFFF
insert into IntegerTable values(11, 1099511627776); 		-- 0x     0100 0000 0000
insert into IntegerTable values(12, 281474976710655);		-- 0x     FFFF FFFF FFFF
insert into IntegerTable values(13, 281474976710656); 		-- 0x  01 0000 0000 0000
insert into IntegerTable values(14, 72057594037927935);		-- 0x  FF FFFF FFFF FFFF
insert into IntegerTable values(15, 72057594037927936); 	-- 0x0100 0000 0000 0000
insert into IntegerTable values(16, -1);					-- 0xFFFF FFFF FFFF FFFF
insert into IntegerTable values(17, -9223372036854775808); 	-- 0x8000 0000 0000 0000

insert into IntegerTable values(18, -9223372036854775807); 	-- 0x8000 0000 0000 0001
insert into IntegerTable values(19, -9223372036854775553); 	-- 0x8000 0000 0000 00FF
insert into IntegerTable values(20, -9223372036854775552); 	-- 0x8000 0000 0000 0100
insert into IntegerTable values(21, -9223372036854710273); 	-- 0x8000 0000 0000 FFFF
insert into IntegerTable values(22, -9223372036854710272); 	-- 0x8000 0000 0001 0000
insert into IntegerTable values(23, -9223372036837998593); 	-- 0x8000 0000 00FF FFFF
insert into IntegerTable values(24, -9223372036837998592); 	-- 0x8000 0000 0100 0000
insert into IntegerTable values(25, -9223372032559808513); 	-- 0x8000 0000 FFFF FFFF
insert into IntegerTable values(26, -9223372032559808512); 	-- 0x8000 0001 0000 0000
insert into IntegerTable values(27, -9223370937343148033); 	-- 0x8000 00FF FFFF FFFF
insert into IntegerTable values(28, -9223370937343148032); 	-- 0x8000 0100 0000 0000
insert into IntegerTable values(29, -9223090561878065153); 	-- 0x8000 FFFF FFFF FFFF
insert into IntegerTable values(30, -9223090561878065152); 	-- 0x8001 0000 0000 0000
insert into IntegerTable values(31, -9151314442816847873); 	-- 0x80FF FFFF FFFF FFFF
insert into IntegerTable values(32, -9151314442816847872); 	-- 0x8100 0000 0000 0000
insert into IntegerTable values(33, -8070450532247928833); 	-- 0x8FFF FFFF FFFF FFFF

insert into IntegerTable values(34, -1); 					-- 0xFFFF FFFF FFFF FFFF
insert into IntegerTable values(35, -255); 					-- 0xFFFF FFFF FFFF FF01
insert into IntegerTable values(36, -256); 					-- 0xFFFF FFFF FFFF FF00
insert into IntegerTable values(37, -65535); 				-- 0xFFFF FFFF FFFF 0001
insert into IntegerTable values(38, -65536); 				-- 0xFFFF FFFF FFFF 0000
insert into IntegerTable values(39, -16777215); 			-- 0xFFFF FFFF FF00 0001
insert into IntegerTable values(40, -16777216); 			-- 0xFFFF FFFF FF00 0000
insert into IntegerTable values(41, -4294967295); 			-- 0xFFFF FFFF 0000 0001
insert into IntegerTable values(42, -4294967296); 			-- 0xFFFF FFFF 0000 0000
insert into IntegerTable values(43, -1099511627775); 		-- 0xFFFF FF00 0000 0001
insert into IntegerTable values(44, -1099511627776); 		-- 0xFFFF FF00 0000 0000
insert into IntegerTable values(45, -281474976710655); 		-- 0xFFFF 0000 0000 0001
insert into IntegerTable values(46, -281474976710656); 		-- 0xFFFF 0000 0000 0000
insert into IntegerTable values(47, -72057594037927935); 	-- 0xFF00 0000 0000 0001
insert into IntegerTable values(48, -72057594037927936); 	-- 0xFF00 0000 0000 0000
insert into IntegerTable values(49, -72057594037927935); 	-- 0xFF00 0000 0000 0001
