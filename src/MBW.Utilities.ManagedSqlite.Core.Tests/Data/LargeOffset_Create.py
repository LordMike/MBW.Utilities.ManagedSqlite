# python3 LargeOffset_Create.py
# Builds LargeOffset.sparse: a real SQLite database of about 4.5 GB whose LargeOffsetTable lies past 4 GiB,
# stored as only its non-zero pages. Needs about 4.5 GB of free disk while it runs.
#
# SQLite writes the database: a padding table pushes LargeOffsetTable past 4 GiB, then is dropped with
# secure_delete on, so its pages are zeroed. The only non-zero pages left are then page 1, the freelist trunk
# pages and LargeOffsetTable's pages. The freelist is emptied (header offsets 32 and 36) and its trunk pages
# zeroed, so reading the file needs nothing but the table pages. SQLite still reads it; its integrity_check only
# reports the emptied pages as "never used".
#
# Sparse format: for each non-zero page, a 4-byte big-endian page number followed by the page itself.
# Page size and page count come from the header in page 1. Every page not listed is all zeros.
import os
import sqlite3
import struct
import tempfile

PAGE_SIZE = 4096
OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "LargeOffset.sparse")

with tempfile.TemporaryDirectory() as tmp:
    path = os.path.join(tmp, "LargeOffset.db")

    db = sqlite3.connect(path, isolation_level=None)
    for pragma in ["page_size=%d" % PAGE_SIZE, "journal_mode=OFF", "synchronous=OFF", "secure_delete=ON"]:
        db.execute("PRAGMA " + pragma)

    db.execute("CREATE TABLE Padding (Data BLOB)")
    for _ in range(5):
        db.execute("INSERT INTO Padding VALUES (zeroblob(900000000))")

    db.execute("CREATE TABLE LargeOffsetTable (Id INTEGER PRIMARY KEY, Name TEXT)")
    db.execute("INSERT INTO LargeOffsetTable VALUES (1, 'one')")
    db.execute("INSERT INTO LargeOffsetTable VALUES (2, 'two')")
    # 5000 'x' characters, which spill onto an overflow page
    db.execute("INSERT INTO LargeOffsetTable VALUES (3, replace(hex(zeroblob(2500)), '0', 'x'))")
    db.execute("DROP TABLE Padding")

    root_page = db.execute("SELECT rootpage FROM sqlite_master WHERE name = 'LargeOffsetTable'").fetchone()[0]
    db.close()

    assert (root_page - 1) * PAGE_SIZE > 4 * 1024 ** 3, "LargeOffsetTable must start past 4 GiB"

    with open(path, "r+b") as f:
        header = bytearray(f.read(100))

        # Zero each freelist trunk page, following the chain from the header
        trunk = struct.unpack(">I", header[32:36])[0]
        while trunk:
            f.seek((trunk - 1) * PAGE_SIZE)
            next_trunk = struct.unpack(">I", f.read(4))[0]
            f.seek((trunk - 1) * PAGE_SIZE)
            f.write(bytes(PAGE_SIZE))
            trunk = next_trunk

        header[32:40] = bytes(8)
        f.seek(0)
        f.write(header)

    zero = bytes(PAGE_SIZE)
    pages = []
    with open(path, "rb") as f:
        number = 0
        while True:
            page = f.read(PAGE_SIZE)
            if not page:
                break
            number += 1
            if page != zero:
                pages.append(number)
                with open(OUTPUT + ".tmp", "ab") as out:
                    out.write(struct.pack(">I", number) + page)

    os.replace(OUTPUT + ".tmp", OUTPUT)
    print("root page", root_page, "non-zero pages", pages)
