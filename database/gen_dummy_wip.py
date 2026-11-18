"""
#############################################################################
# WARNING: LOCAL TEST DB ONLY - PRODUCTION / REAL COMPANY DB PE KABHI MAT CHALAO! #
# WIP Report ke liye local FlexNet DB mein missing schema + dummy data:      #
#   1. MPI_COB_T_SERIAL_NO       -> WORKSTATION/LOCATION/PRODUCTID/          #
#                                     LASTUPDATEON columns add (agar nahi)   #
#   2. MPI_COB_T_TRANSACTION_OUTBOUND -> TRANSACTIONID column add            #
#   3. MPI_PRODUCT                -> naya table, kuch dummy products        #
#   4. WIP dummy rows (16 categories, IN-PROD/ISSUE/IN REPAIR) + Containment #
#      (ENGCONT% outbound rows) taaki WipController ka query locally chale. #
# Real DB pe ye sab already maujood hain (WipController real Oracle-mirror   #
# schema ke against likha gaya tha) - sirf local dev DB ke liye patch.       #
#############################################################################
"""
import random
from datetime import datetime, timedelta
import pyodbc

SERVER = "localhost"
DATABASE = "FlexNet"          # SAFETY: sirf local test DB
DRIVER = "ODBC Driver 17 for SQL Server"

random.seed(11)
_serial = 80000000   # naya range - FES dummy (70M+) aur real (64-65M) se alag
_wo = 4900000


def next_serial():
    global _serial
    _serial += 1
    return str(_serial)


def next_wo():
    global _wo
    _wo += 1
    return f"{_wo}-{_wo % 10}"


# (category name, WORKSTATION or None, LOCATION text or None, row count)
# WORKSTATION ranges / LOCATION text WipController.DeriveLocationSql ke
# CASE expression se exactly match karte hain.
CATEGORY_SPECS = [
    ("LINESET",        "10008", None,             8),
    ("LINESET LINE",   "10500", None,             12),
    ("OLD LINE",       "21500", None,             45),
    ("NEW LINE",       "31500", None,             50),
    ("TEST CELL LINE", "42000", None,             30),
    ("PAINT LINE",     "51000", None,             35),
    ("QUALITY DOCK",   None,    "QUALITY DOCK",   10),
    ("PAINT HOLD",     None,    "PAINT HOLD",     6),
    ("PAINT REPAIR",   None,    "PAINT REPAIR",   9),
    ("NEWLINE LOOP",   None,    "NEWLINE LOOP",   7),
    ("MRA",             None,    "MRA",            5),
    ("EQA AUDIT",       None,    "EQA AUDIT",      6),
    ("TEST REWORK",    None,    "TEST REWORK",    8),
    ("PE",               None,    "PE",             4),
    ("SHORT BUILD",     None,    "SHORT BUILD",    5),
    ("UNKNOWN",         "99999", None,             3),
]

STATUS_CHOICES = [1, 2, 6]           # IN-PROD, ISSUE, IN REPAIR
STATUS_WEIGHTS = [0.70, 0.18, 0.12]

PRODUCTS = [(1.0, "TCL-ENG-100"), (2.0, "TCL-ENG-200"), (3.0, "TCL-ENG-300"),
            (4.0, "TCL-ENG-400"), (5.0, "TCL-ENG-500"), (6.0, "TCL-ENG-600")]

CONTAINMENT_RATE = 0.08   # ~8% of generated WIP serials flagged CONTAINMENT

# STATUS=3 (FES) rows - excluded from WIP Report's STATUS IN (1,2,6) filter,
# but Model Wise Tracking's base filter includes 3, mapping to the 'FES' bucket.
FES_COUNT = 40


def rand_recent(now: datetime, max_days_back: int) -> datetime:
    """Last few days ke andar random timestamp - WIP ek 'live snapshot' hai, purana data nahi."""
    return now - timedelta(seconds=random.randint(0, max_days_back * 86400))


def ensure_column(cur, table, column, coltype):
    cur.execute(
        "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS "
        "WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=? AND COLUMN_NAME=?",
        table, column)
    if cur.fetchone()[0] == 0:
        print(f"  + adding column {table}.{column} {coltype}")
        cur.execute(f"ALTER TABLE dbo.{table} ADD {column} {coltype} NULL")
    else:
        print(f"  = {table}.{column} already exists")


def main():
    assert DATABASE.lower() == "flexnet", "Sirf local FlexNet test DB pe chalao."

    conn = pyodbc.connect(f"DRIVER={{{DRIVER}}};SERVER={SERVER};DATABASE={DATABASE};"
                          f"Trusted_Connection=yes;TrustServerCertificate=yes;")
    conn.autocommit = False
    cur = conn.cursor()

    # ── 1. Missing columns add (WIP query ko chahiye) ──────────────────────
    print("Step 1: schema patch ...")
    ensure_column(cur, "MPI_COB_T_SERIAL_NO", "WORKSTATION", "NVARCHAR(40)")
    ensure_column(cur, "MPI_COB_T_SERIAL_NO", "LOCATION", "NVARCHAR(40)")
    ensure_column(cur, "MPI_COB_T_SERIAL_NO", "PRODUCTID", "FLOAT")
    ensure_column(cur, "MPI_COB_T_SERIAL_NO", "LASTUPDATEON", "DATETIME2")
    ensure_column(cur, "MPI_COB_T_TRANSACTION_OUTBOUND", "TRANSACTIONID", "NVARCHAR(40)")
    conn.commit()

    # ── 2. MPI_PRODUCT table (WipBaseSql isse INNER JOIN karta hai) ────────
    print("Step 2: MPI_PRODUCT table ...")
    cur.execute("SELECT OBJECT_ID('dbo.MPI_PRODUCT','U')")
    if cur.fetchone()[0] is None:
        print("  creating dbo.MPI_PRODUCT")
        cur.execute("CREATE TABLE dbo.MPI_PRODUCT (ID FLOAT NULL, PRODUCTNO NVARCHAR(40) NULL)")
        conn.commit()
    cur.execute("SELECT COUNT(1) FROM dbo.MPI_PRODUCT")
    if cur.fetchone()[0] == 0:
        print(f"  seeding {len(PRODUCTS)} dummy products")
        cur.executemany("INSERT INTO dbo.MPI_PRODUCT (ID, PRODUCTNO) VALUES (?,?)", PRODUCTS)
        conn.commit()

    # ── 3. Purana WIP dummy hatao (idempotent re-run) ───────────────────────
    print("Step 3: cleaning previous WIP dummy ...")
    cur.execute("DELETE FROM dbo.MPI_COB_T_TRANSACTION_OUTBOUND WHERE TRANSACTIONID LIKE 'ENGCONT%'")
    cur.execute("DELETE FROM dbo.MPI_COB_T_SERIAL_NO WHERE TRY_CAST(SERIALNO AS BIGINT) >= 80000000")
    conn.commit()

    # ── 4. WIP dummy rows generate ──────────────────────────────────────────
    print("Step 4: generating WIP rows ...")
    now = datetime.now()
    sn_rows = []              # (SERIALNO, WORKORDERNO, STATUS, CREATEDON, WORKSTATION, LOCATION, PRODUCTID, LASTUPDATEON)
    all_generated = []        # (SERIALNO, WORKORDERNO, STATUS, CREATEDON) - containment sampling ke liye

    for name, ws, loc, count in CATEGORY_SPECS:
        for _ in range(count):
            serial = next_serial()
            wo = next_wo()
            status = random.choices(STATUS_CHOICES, weights=STATUS_WEIGHTS, k=1)[0]
            created = rand_recent(now, max_days_back=5)
            updated = created + timedelta(minutes=random.randint(1, 600))
            if updated > now:
                updated = now
            product_id = random.choice(PRODUCTS)[0]
            sn_rows.append((serial, wo, status, created, ws, loc, product_id, updated))
            all_generated.append((serial, wo, status, created))

    # FES rows (STATUS=3) - for Model Wise Tracking's FES bucket/detail rows.
    for _ in range(FES_COUNT):
        serial = next_serial()
        wo = next_wo()
        created = rand_recent(now, max_days_back=5)
        updated = created + timedelta(minutes=random.randint(1, 600))
        if updated > now:
            updated = now
        product_id = random.choice(PRODUCTS)[0]
        sn_rows.append((serial, wo, 3, created, None, None, product_id, updated))

    print(f"  {len(sn_rows)} WIP+FES rows across {len(CATEGORY_SPECS)} categories (+{FES_COUNT} FES)")
    cur.fast_executemany = True
    cur.executemany(
        "INSERT INTO dbo.MPI_COB_T_SERIAL_NO "
        "(SERIALNO, WORKORDERNO, STATUS, CREATEDON, WORKSTATION, LOCATION, PRODUCTID, LASTUPDATEON) "
        "VALUES (?,?,?,?,?,?,?,?)", sn_rows)
    conn.commit()

    # ── 5. Containment flags (~8%) - ENGCONT outbound transaction ──────────
    print("Step 5: containment flags ...")
    n_contain = max(1, int(len(all_generated) * CONTAINMENT_RATE))
    contained = random.sample(all_generated, n_contain)
    ob_rows = []
    for i, (serial, wo, status, created) in enumerate(contained, start=1):
        txn_id = f"ENGCONT{i:04d}"
        ob_time = created + timedelta(minutes=random.randint(1, 300))
        if ob_time > now:
            ob_time = now
        ob_rows.append((wo, serial, 1.0, ob_time, txn_id))

    cur.executemany(
        "INSERT INTO dbo.MPI_COB_T_TRANSACTION_OUTBOUND "
        "(WIPJOBNO, SERIALNO, OVERALLSTATUS, CREATEDON, TRANSACTIONID) VALUES (?,?,?,?,?)",
        ob_rows)
    conn.commit()
    print(f"  {len(ob_rows)} containment rows flagged")

    cur.close()
    conn.close()
    print("Done - WIP dummy data ready.")
    print(f"  Total rows (WIP+FES): {len(sn_rows)}")
    print(f"  FES (status=3):       {FES_COUNT}")
    print(f"  Containment:          {len(ob_rows)}")


if __name__ == "__main__":
    main()
