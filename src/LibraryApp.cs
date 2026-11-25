using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LibraryManagementSystem
{
    // =========================================================================
    // 1. NATIVE WINDOWS SQLITE ENGINE (winsqlite3.dll - Zero External Dependencies)
    // =========================================================================
    public sealed class SqliteEngine : IDisposable
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;

        private const int SQLITE_INTEGER = 1;
        private const int SQLITE_FLOAT = 2;
        private const int SQLITE_TEXT = 3;
        private const int SQLITE_NULL = 5;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open(IntPtr filenameUtf8, out IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, IntPtr sqlUtf8, int nByte, out IntPtr stmt, out IntPtr pzTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_type(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern double sqlite3_column_double(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_changes(IntPtr db);

        private IntPtr _db = IntPtr.Zero;
        public string DbPath { get; private set; }
        public event Action<string> OnSqlExecuted;

        public SqliteEngine(string dbPath)
        {
            DbPath = dbPath;
            Open(dbPath);
        }

        private void Open(string path)
        {
            IntPtr pathPtr = ToUtf8Ptr(path);
            try
            {
                int rc = sqlite3_open(pathPtr, out _db);
                if (rc != SQLITE_OK)
                    throw new InvalidOperationException("Failed to open SQLite database: " + GetErrorMessage());
                ExecuteNonQuery("PRAGMA foreign_keys = ON;", false);
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        public void ReopenFresh()
        {
            Close();
            if (File.Exists(DbPath))
                File.Delete(DbPath);
            Open(DbPath);
        }

        private static IntPtr ToUtf8Ptr(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((text ?? string.Empty) + "\0");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        private static string FromUtf8Ptr(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        private string GetErrorMessage()
        {
            return _db == IntPtr.Zero ? "Database closed" : FromUtf8Ptr(sqlite3_errmsg(_db));
        }

        public int ExecuteNonQuery(string sql, bool notify = true)
        {
            int totalChanges = 0;
            ExecuteInternal(sql, out totalChanges);
            if (notify && OnSqlExecuted != null)
                OnSqlExecuted(CompactSql(sql));
            return totalChanges;
        }

        public DataTable Query(string sql, bool notify = false)
        {
            int changes;
            DataTable dt = ExecuteInternal(sql, out changes);
            if (notify && OnSqlExecuted != null)
                OnSqlExecuted(CompactSql(sql));
            return dt;
        }

        public object ExecuteScalar(string sql)
        {
            DataTable dt = Query(sql, false);
            if (dt.Rows.Count > 0 && dt.Columns.Count > 0 && dt.Rows[0][0] != DBNull.Value)
                return dt.Rows[0][0];
            return null;
        }

        private DataTable ExecuteInternal(string sql, out int totalChanges)
        {
            totalChanges = 0;
            DataTable lastTable = new DataTable();
            byte[] utf8 = Encoding.UTF8.GetBytes((sql ?? string.Empty) + "\0");
            IntPtr buf = Marshal.AllocHGlobal(utf8.Length);
            try
            {
                Marshal.Copy(utf8, 0, buf, utf8.Length);
                IntPtr cur = buf;
                long endAddr = buf.ToInt64() + utf8.Length - 1;

                while (cur.ToInt64() < endAddr && Marshal.ReadByte(cur) != 0)
                {
                    IntPtr stmt = IntPtr.Zero;
                    IntPtr tail = IntPtr.Zero;
                    int rc = sqlite3_prepare_v2(_db, cur, -1, out stmt, out tail);
                    if (rc != SQLITE_OK)
                    {
                        if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                        throw new InvalidOperationException(GetErrorMessage());
                    }

                    if (stmt == IntPtr.Zero)
                    {
                        if (tail == IntPtr.Zero || tail == cur) break;
                        cur = tail;
                        continue;
                    }

                    try
                    {
                        int colCount = sqlite3_column_count(stmt);
                        if (colCount > 0)
                        {
                            DataTable dt = new DataTable();
                            HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < colCount; i++)
                            {
                                string baseName = FromUtf8Ptr(sqlite3_column_name(stmt, i));
                                if (string.IsNullOrEmpty(baseName)) baseName = "Column" + (i + 1);
                                string colName = baseName;
                                int suffix = 2;
                                while (usedNames.Contains(colName))
                                    colName = baseName + "_" + (suffix++);
                                usedNames.Add(colName);
                                dt.Columns.Add(colName, typeof(object));
                            }

                            while (true)
                            {
                                int stepRc = sqlite3_step(stmt);
                                if (stepRc == SQLITE_ROW)
                                {
                                    object[] row = new object[colCount];
                                    for (int i = 0; i < colCount; i++)
                                    {
                                        int ctype = sqlite3_column_type(stmt, i);
                                        switch (ctype)
                                        {
                                            case SQLITE_INTEGER:
                                                row[i] = sqlite3_column_int64(stmt, i);
                                                break;
                                            case SQLITE_FLOAT:
                                                row[i] = Math.Round(sqlite3_column_double(stmt, i), 2);
                                                break;
                                            case SQLITE_NULL:
                                                row[i] = DBNull.Value;
                                                break;
                                            case SQLITE_TEXT:
                                            default:
                                                row[i] = FromUtf8Ptr(sqlite3_column_text(stmt, i));
                                                break;
                                        }
                                    }
                                    dt.Rows.Add(row);
                                }
                                else if (stepRc == SQLITE_DONE)
                                {
                                    break;
                                }
                                else
                                {
                                    throw new InvalidOperationException(GetErrorMessage());
                                }
                            }
                            lastTable = dt;
                        }
                        else
                        {
                            while (true)
                            {
                                int stepRc = sqlite3_step(stmt);
                                if (stepRc == SQLITE_DONE) break;
                                if (stepRc != SQLITE_ROW)
                                    throw new InvalidOperationException(GetErrorMessage());
                            }
                            totalChanges += sqlite3_changes(_db);
                        }
                    }
                    finally
                    {
                        sqlite3_finalize(stmt);
                    }

                    if (tail == IntPtr.Zero || tail == cur) break;
                    cur = tail;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return lastTable;
        }

        public static string SqlQuote(string val)
        {
            if (val == null) return "NULL";
            return "'" + val.Replace("'", "''") + "'";
        }

        private static string CompactSql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
            string oneLine = string.Join(" ", sql.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return oneLine.Length > 135 ? oneLine.Substring(0, 132) + "..." : oneLine;
        }

        public void Close()
        {
            if (_db != IntPtr.Zero)
            {
                sqlite3_close(_db);
                _db = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Close();
        }
    }

    // =========================================================================
    // 2. SCHEMA, TRIGGERS, VIEWS & BUSINESS LOGIC REPOSITORY
    // =========================================================================
    public static class LibraryBootstrap
    {
        public static void EnsureInitialized(SqliteEngine db)
        {
            object exists = db.ExecuteScalar("SELECT name FROM sqlite_master WHERE type='table' AND name='Books';");
            if (exists != null) return;
            ResetAndSeed(db);
        }

        public static void ResetAndSeed(SqliteEngine db)
        {
            db.ReopenFresh();

            string ddl = @"
CREATE TABLE SimConfig (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    sim_date TEXT NOT NULL
);
INSERT INTO SimConfig (id, sim_date) VALUES (1, '2026-11-25');

CREATE TABLE Categories (
    category_id INTEGER PRIMARY KEY AUTOINCREMENT,
    category_name TEXT NOT NULL UNIQUE,
    shelf_code TEXT NOT NULL
);

CREATE TABLE Members (
    member_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    email TEXT NOT NULL UNIQUE,
    phone TEXT NOT NULL,
    membership_tier TEXT NOT NULL DEFAULT 'Student' CHECK (membership_tier IN ('Student', 'Premium', 'Faculty')),
    max_books_allowed INTEGER NOT NULL DEFAULT 3 CHECK (max_books_allowed BETWEEN 1 AND 15),
    joined_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Active' CHECK (status IN ('Active', 'Suspended'))
);

CREATE TABLE Books (
    book_id INTEGER PRIMARY KEY,
    isbn TEXT NOT NULL UNIQUE,
    title TEXT NOT NULL,
    author TEXT NOT NULL,
    category_id INTEGER NOT NULL REFERENCES Categories(category_id),
    total_copies INTEGER NOT NULL DEFAULT 1 CHECK (total_copies >= 0),
    available_copies INTEGER NOT NULL DEFAULT 1 CHECK (available_copies >= 0 AND available_copies <= total_copies)
);

CREATE TABLE Borrowings (
    borrow_id INTEGER PRIMARY KEY AUTOINCREMENT,
    member_id INTEGER NOT NULL REFERENCES Members(member_id),
    book_id INTEGER NOT NULL REFERENCES Books(book_id),
    issue_date TEXT NOT NULL,
    due_date TEXT NOT NULL,
    return_date TEXT DEFAULT NULL,
    renew_count INTEGER NOT NULL DEFAULT 0 CHECK (renew_count BETWEEN 0 AND 2),
    fine_assessed REAL NOT NULL DEFAULT 0.0
);

CREATE TABLE Reservations (
    reservation_id INTEGER PRIMARY KEY AUTOINCREMENT,
    member_id INTEGER NOT NULL REFERENCES Members(member_id),
    book_id INTEGER NOT NULL REFERENCES Books(book_id),
    reserved_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Fulfilled', 'Cancelled'))
);

CREATE TABLE FinePayments (
    payment_id INTEGER PRIMARY KEY AUTOINCREMENT,
    borrow_id INTEGER NOT NULL REFERENCES Borrowings(borrow_id),
    member_id INTEGER NOT NULL REFERENCES Members(member_id),
    amount_paid REAL NOT NULL CHECK (amount_paid > 0),
    payment_date TEXT NOT NULL,
    payment_method TEXT NOT NULL DEFAULT 'UPI' CHECK (payment_method IN ('UPI', 'Cash', 'Card'))
);

CREATE TABLE ActivityLog (
    log_id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_type TEXT NOT NULL,
    description TEXT NOT NULL,
    event_time TEXT NOT NULL
);

-- TRIGGER 1: Validate member status, tier limit & book stock BEFORE issuing
CREATE TRIGGER trg_before_borrow_insert
BEFORE INSERT ON Borrowings
FOR EACH ROW
BEGIN
    SELECT CASE
        WHEN (SELECT status FROM Members WHERE member_id = NEW.member_id) <> 'Active'
        THEN RAISE(ABORT, 'Cannot issue book: Member account is Suspended!')
    END;
    SELECT CASE
        WHEN NEW.return_date IS NULL
             AND (SELECT COUNT(*) FROM Borrowings WHERE member_id = NEW.member_id AND return_date IS NULL)
                 >= (SELECT max_books_allowed FROM Members WHERE member_id = NEW.member_id)
        THEN RAISE(ABORT, 'Cannot issue book: Member reached their tier borrowing limit!')
    END;
    SELECT CASE
        WHEN NEW.return_date IS NULL
             AND (SELECT available_copies FROM Books WHERE book_id = NEW.book_id) <= 0
        THEN RAISE(ABORT, 'Cannot issue book: 0 available copies in stock! Place a Reservation instead.')
    END;
END;

-- TRIGGER 2: Decrement available_copies and write audit log AFTER issuing
CREATE TRIGGER trg_after_borrow_insert
AFTER INSERT ON Borrowings
FOR EACH ROW
BEGIN
    UPDATE Books
    SET available_copies = available_copies - 1
    WHERE book_id = NEW.book_id AND NEW.return_date IS NULL;

    INSERT INTO ActivityLog (event_type, description, event_time)
    VALUES (
        'BOOK_ISSUED',
        'Borrow #' || NEW.borrow_id || ': ' || (SELECT name FROM Members WHERE member_id = NEW.member_id)
            || ' borrowed ""' || (SELECT title FROM Books WHERE book_id = NEW.book_id) || '"" (Due ' || NEW.due_date || ')',
        NEW.issue_date
    );
END;

-- TRIGGER 3: Restore available_copies on return & log returns/renewals
CREATE TRIGGER trg_after_borrow_update
AFTER UPDATE ON Borrowings
FOR EACH ROW
BEGIN
    UPDATE Books
    SET available_copies = available_copies + 1
    WHERE book_id = NEW.book_id AND OLD.return_date IS NULL AND NEW.return_date IS NOT NULL;

    INSERT INTO ActivityLog (event_type, description, event_time)
    SELECT 'BOOK_RETURNED',
           'Borrow #' || NEW.borrow_id || ': ""' || (SELECT title FROM Books WHERE book_id = NEW.book_id)
               || '"" returned on ' || NEW.return_date || ' (Fine: Rs. ' || printf('%.2f', NEW.fine_assessed) || ')',
           NEW.return_date
    WHERE OLD.return_date IS NULL AND NEW.return_date IS NOT NULL;

    INSERT INTO ActivityLog (event_type, description, event_time)
    SELECT 'BOOK_RENEWED',
           'Borrow #' || NEW.borrow_id || ': ""' || (SELECT title FROM Books WHERE book_id = NEW.book_id)
               || '"" renewed to ' || NEW.due_date || ' (Renewal #' || NEW.renew_count || ')',
           (SELECT sim_date FROM SimConfig WHERE id = 1)
    WHERE NEW.renew_count > OLD.renew_count;
END;

-- TRIGGER 4: Log new reservations
CREATE TRIGGER trg_after_reservation_insert
AFTER INSERT ON Reservations
FOR EACH ROW
WHEN NEW.status = 'Pending'
BEGIN
    INSERT INTO ActivityLog (event_type, description, event_time)
    VALUES (
        'HOLD_PLACED',
        'Hold #' || NEW.reservation_id || ': ' || (SELECT name FROM Members WHERE member_id = NEW.member_id)
            || ' queued for ""' || (SELECT title FROM Books WHERE book_id = NEW.book_id) || '""',
        NEW.reserved_date
    );
END;

-- TRIGGER 5: Log fine collections
CREATE TRIGGER trg_after_payment_insert
AFTER INSERT ON FinePayments
FOR EACH ROW
BEGIN
    INSERT INTO ActivityLog (event_type, description, event_time)
    VALUES (
        'FINE_PAID',
        'Payment #' || NEW.payment_id || ': ' || (SELECT name FROM Members WHERE member_id = NEW.member_id)
            || ' paid Rs. ' || printf('%.2f', NEW.amount_paid) || ' via ' || NEW.payment_method || ' (Borrow #' || NEW.borrow_id || ')',
        NEW.payment_date
    );
END;

-- VIEW 1: Active Borrowings with Simulated Clock & Tier-Based Fine Rates
CREATE VIEW vw_ActiveBorrowings AS
SELECT
    b.borrow_id,
    m.member_id,
    m.name AS member_name,
    m.membership_tier AS tier,
    bk.book_id,
    bk.title AS book_title,
    b.issue_date,
    b.due_date,
    b.renew_count AS renewals,
    CAST(julianday(b.due_date) - julianday((SELECT sim_date FROM SimConfig WHERE id = 1)) AS INTEGER) AS days_left,
    MAX(0, CAST(julianday((SELECT sim_date FROM SimConfig WHERE id = 1)) - julianday(b.due_date) AS INTEGER)) AS days_overdue,
    ROUND(
        MAX(0, CAST(julianday((SELECT sim_date FROM SimConfig WHERE id = 1)) - julianday(b.due_date) AS INTEGER)) *
        CASE m.membership_tier
            WHEN 'Student' THEN 5.0
            WHEN 'Premium' THEN 3.0
            WHEN 'Faculty' THEN 2.0
        END, 2
    ) AS live_fine_rs,
    CASE
        WHEN julianday((SELECT sim_date FROM SimConfig WHERE id = 1)) > julianday(b.due_date) THEN 'OVERDUE'
        WHEN julianday(b.due_date) - julianday((SELECT sim_date FROM SimConfig WHERE id = 1)) <= 2 THEN 'DUE SOON'
        ELSE 'ON TIME'
    END AS loan_status
FROM Borrowings b
JOIN Members m ON b.member_id = m.member_id
JOIN Books bk ON b.book_id = bk.book_id
WHERE b.return_date IS NULL;

-- VIEW 2: Complete Member Financial & Circulation Ledger
CREATE VIEW vw_MemberLedger AS
SELECT
    m.member_id,
    m.name,
    m.email,
    m.membership_tier AS tier,
    m.status,
    m.max_books_allowed AS max_limit,
    (SELECT COUNT(*) FROM Borrowings b WHERE b.member_id = m.member_id AND b.return_date IS NULL) AS active_loans,
    (SELECT COUNT(*) FROM Borrowings b WHERE b.member_id = m.member_id) AS total_borrows,
    ROUND(COALESCE((SELECT SUM(b.fine_assessed) FROM Borrowings b WHERE b.member_id = m.member_id), 0.0), 2) AS closed_fines_rs,
    ROUND(COALESCE((SELECT SUM(av.live_fine_rs) FROM vw_ActiveBorrowings av WHERE av.member_id = m.member_id), 0.0), 2) AS live_overdue_rs,
    ROUND(COALESCE((SELECT SUM(fp.amount_paid) FROM FinePayments fp WHERE fp.member_id = m.member_id), 0.0), 2) AS paid_rs,
    ROUND(
        COALESCE((SELECT SUM(b.fine_assessed) FROM Borrowings b WHERE b.member_id = m.member_id), 0.0)
        + COALESCE((SELECT SUM(av.live_fine_rs) FROM vw_ActiveBorrowings av WHERE av.member_id = m.member_id), 0.0)
        - COALESCE((SELECT SUM(fp.amount_paid) FROM FinePayments fp WHERE fp.member_id = m.member_id), 0.0),
        2
    ) AS net_due_rs
FROM Members m;

-- VIEW 3: Book Catalog, Popularity & Hold Queue
CREATE VIEW vw_BookPopularity AS
SELECT
    bk.book_id,
    bk.isbn,
    bk.title,
    bk.author,
    c.category_name AS category,
    c.shelf_code AS shelf,
    bk.available_copies AS available,
    bk.total_copies AS total,
    COUNT(DISTINCT b.borrow_id) AS times_borrowed,
    (SELECT COUNT(*) FROM Reservations r WHERE r.book_id = bk.book_id AND r.status = 'Pending') AS pending_holds
FROM Books bk
JOIN Categories c ON bk.category_id = c.category_id
LEFT JOIN Borrowings b ON bk.book_id = b.book_id
GROUP BY bk.book_id, bk.isbn, bk.title, bk.author, c.category_name, c.shelf_code, bk.available_copies, bk.total_copies;
";
            db.ExecuteNonQuery(ddl, false);

            string seed = @"
INSERT INTO Categories (category_id, category_name, shelf_code) VALUES
(1, 'Programming', 'CS-A1'),
(2, 'Fiction',     'FI-B2'),
(3, 'Science',     'SC-C1'),
(4, 'Databases',   'CS-A2'),
(5, 'History',     'HI-D1'),
(6, 'AI & ML',     'CS-A3');

INSERT INTO Members (member_id, name, email, phone, membership_tier, max_books_allowed, joined_date, status) VALUES
(1, 'Aarav Sharma',   'aarav@gmail.com', '9876543210', 'Student', 3, '2026-08-10', 'Active'),
(2, 'Riya Patel',     'riya@gmail.com',  '9876543211', 'Premium', 5, '2026-08-15', 'Active'),
(3, 'Karan Verma',    'karan@gmail.com', '9876543212', 'Student', 3, '2026-09-01', 'Active'),
(4, 'Sneha Iyer',     'sneha@gmail.com', '9876543213', 'Student', 3, '2026-09-12', 'Active'),
(5, 'Dr. Vikram Rao', 'vikram@univ.edu', '9876543214', 'Faculty', 8, '2026-07-01', 'Active'),
(6, 'Meera Nair',     'meera@gmail.com', '9876543215', 'Premium', 5, '2026-10-05', 'Active'),
(7, 'Rohan Das',      'rohan@gmail.com', '9876543216', 'Student', 3, '2026-10-20', 'Suspended');

INSERT INTO Books (book_id, isbn, title, author, category_id, total_copies, available_copies) VALUES
(101, '978-0321563842', 'C++ Basics',                       'Bjarne Stroustrup',    1, 4, 4),
(102, '978-0747532699', 'Harry Potter & Sorcerer''s Stone', 'J.K. Rowling',         2, 2, 2),
(103, '978-1118230718', 'Fundamentals of Physics',          'David Halliday',       3, 4, 4),
(104, '978-0078022159', 'Database System Concepts',         'Abraham Silberschatz', 4, 3, 3),
(105, '978-0132350884', 'Clean Code',                       'Robert C. Martin',     1, 1, 1),
(106, '978-0553380163', 'A Brief History of Time',          'Stephen Hawking',      3, 2, 2),
(107, '978-0060959581', 'India After Gandhi',               'Ramachandra Guha',     5, 3, 3),
(108, '978-0262035613', 'Deep Learning',                    'Ian Goodfellow',       6, 2, 2);

INSERT INTO Borrowings (borrow_id, member_id, book_id, issue_date, due_date, return_date, renew_count, fine_assessed) VALUES
(1, 1, 101, '2026-11-01', '2026-11-11', '2026-11-09', 0,  0.00),
(2, 4, 104, '2026-11-01', '2026-11-11', '2026-11-17', 0, 30.00),
(3, 1, 105, '2026-11-02', '2026-11-12', '2026-11-15', 0, 15.00),
(4, 2, 102, '2026-11-02', '2026-11-17', NULL,         0,  0.00),
(5, 3, 101, '2026-11-05', '2026-11-15', NULL,         1,  0.00),
(6, 6, 105, '2026-11-16', '2026-12-01', NULL,         0,  0.00),
(7, 5, 108, '2026-11-18', '2026-12-18', NULL,         0,  0.00),
(8, 4, 104, '2026-11-20', '2026-11-30', NULL,         0,  0.00);

INSERT INTO Reservations (reservation_id, member_id, book_id, reserved_date, status) VALUES
(1, 1, 105, '2026-11-19', 'Pending'),
(2, 3, 105, '2026-11-21', 'Pending'),
(3, 2, 104, '2026-11-10', 'Fulfilled');

INSERT INTO FinePayments (payment_id, borrow_id, member_id, amount_paid, payment_date, payment_method) VALUES
(1, 2, 4, 20.00, '2026-11-18', 'UPI'),
(2, 3, 1, 15.00, '2026-11-15', 'Cash');
";
            db.ExecuteNonQuery(seed, false);
        }

        public static string GetSimDate(SqliteEngine db)
        {
            return Convert.ToString(db.ExecuteScalar("SELECT sim_date FROM SimConfig WHERE id = 1;"));
        }

        public static void AdvanceSimDays(SqliteEngine db, int days)
        {
            string mod = (days >= 0 ? "+" : "") + days.ToString(CultureInfo.InvariantCulture) + " days";
            db.ExecuteNonQuery("UPDATE SimConfig SET sim_date = date(sim_date, " + SqliteEngine.SqlQuote(mod) + ") WHERE id = 1;");
            string newDate = GetSimDate(db);
            db.ExecuteNonQuery(
                "INSERT INTO ActivityLog (event_type, description, event_time) VALUES ('TIME_TRAVEL', " +
                SqliteEngine.SqlQuote("Simulated library clock advanced by " + days + " day(s) -> " + newDate) + ", " +
                SqliteEngine.SqlQuote(newDate) + ");", false);
        }

        public static void TierRules(string tier, out int maxBooks, out int loanDays, out int finePerDay)
        {
            if (tier == "Faculty") { maxBooks = 8; loanDays = 30; finePerDay = 2; }
            else if (tier == "Premium") { maxBooks = 5; loanDays = 15; finePerDay = 3; }
            else { maxBooks = 3; loanDays = 10; finePerDay = 5; }
        }

        public static void IssueBook(SqliteEngine db, long memberId, long bookId)
        {
            string tier = Convert.ToString(db.ExecuteScalar("SELECT membership_tier FROM Members WHERE member_id = " + memberId + ";"));
            if (string.IsNullOrEmpty(tier)) throw new InvalidOperationException("Member not found.");

            int maxBooks, loanDays, rate;
            TierRules(tier, out maxBooks, out loanDays, out rate);

            string simDate = GetSimDate(db);
            string sql = string.Format(CultureInfo.InvariantCulture,
                "INSERT INTO Borrowings (member_id, book_id, issue_date, due_date) VALUES ({0}, {1}, {2}, date({2}, '+{3} days'));",
                memberId, bookId, SqliteEngine.SqlQuote(simDate), loanDays);
            db.ExecuteNonQuery(sql);
        }

        public static double ReturnBook(SqliteEngine db, long borrowId, out long returnedBookId)
        {
            DataTable dt = db.Query(
                "SELECT b.book_id, b.due_date, m.membership_tier FROM Borrowings b " +
                "JOIN Members m ON b.member_id = m.member_id " +
                "WHERE b.borrow_id = " + borrowId + " AND b.return_date IS NULL;");
            if (dt.Rows.Count == 0)
                throw new InvalidOperationException("Borrowing record not found or already returned.");

            returnedBookId = Convert.ToInt64(dt.Rows[0]["book_id"]);
            string dueStr = Convert.ToString(dt.Rows[0]["due_date"]);
            string tier = Convert.ToString(dt.Rows[0]["membership_tier"]);
            string simDate = GetSimDate(db);

            int maxB, loanD, rate;
            TierRules(tier, out maxB, out loanD, out rate);

            DateTime due = DateTime.ParseExact(dueStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            DateTime ret = DateTime.ParseExact(simDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            int lateDays = Math.Max(0, (int)(ret - due).TotalDays);
            double fine = lateDays * rate;

            string sql = string.Format(CultureInfo.InvariantCulture,
                "UPDATE Borrowings SET return_date = {0}, fine_assessed = {1:F2} WHERE borrow_id = {2};",
                SqliteEngine.SqlQuote(simDate), fine, borrowId);
            db.ExecuteNonQuery(sql);
            return fine;
        }

        public static void RenewBook(SqliteEngine db, long borrowId)
        {
            DataTable dt = db.Query("SELECT book_id, renew_count, due_date FROM Borrowings WHERE borrow_id = " + borrowId + " AND return_date IS NULL;");
            if (dt.Rows.Count == 0)
                throw new InvalidOperationException("Loan not found or already returned.");

            long bookId = Convert.ToInt64(dt.Rows[0]["book_id"]);
            long renews = Convert.ToInt64(dt.Rows[0]["renew_count"]);
            if (renews >= 2)
                throw new InvalidOperationException("Cannot renew: Maximum limit of 2 renewals reached!");

            long holds = Convert.ToInt64(db.ExecuteScalar("SELECT COUNT(*) FROM Reservations WHERE book_id = " + bookId + " AND status = 'Pending';"));
            if (holds > 0)
                throw new InvalidOperationException("Cannot renew: Another member has a Pending Reservation on this book!");

            db.ExecuteNonQuery("UPDATE Borrowings SET due_date = date(due_date, '+7 days'), renew_count = renew_count + 1 WHERE borrow_id = " + borrowId + ";");
        }

        public static string SimulateRushHour(SqliteEngine db, Random rng)
        {
            List<string> events = new List<string>();
            AdvanceSimDays(db, 1);
            string simDate = GetSimDate(db);
            events.Add("📅 Clock advanced to " + simDate);

            // 1. Return up to 1 active loan randomly
            DataTable activeLoans = db.Query("SELECT borrow_id, member_name, book_title FROM vw_ActiveBorrowings;");
            if (activeLoans.Rows.Count > 0 && rng.NextDouble() < 0.65)
            {
                DataRow r = activeLoans.Rows[rng.Next(activeLoans.Rows.Count)];
                long bid = Convert.ToInt64(r["borrow_id"]);
                long retBookId;
                double fine = ReturnBook(db, bid, out retBookId);
                events.Add("✅ " + r["member_name"] + " returned \"" + r["book_title"] + "\" (Fine: ₹" + fine.ToString("F2") + ")");

                // Auto-fulfill first pending reservation if any!
                DataTable hold = db.Query("SELECT r.reservation_id, r.member_id, m.name, m.status, m.max_books_allowed, " +
                    "(SELECT COUNT(*) FROM Borrowings b WHERE b.member_id = m.member_id AND b.return_date IS NULL) AS active_cnt " +
                    "FROM Reservations r JOIN Members m ON r.member_id = m.member_id " +
                    "WHERE r.book_id = " + retBookId + " AND r.status = 'Pending' ORDER BY r.reservation_id ASC LIMIT 1;");
                if (hold.Rows.Count > 0 &&
                    Convert.ToString(hold.Rows[0]["status"]) == "Active" &&
                    Convert.ToInt64(hold.Rows[0]["active_cnt"]) < Convert.ToInt64(hold.Rows[0]["max_books_allowed"]))
                {
                    long resId = Convert.ToInt64(hold.Rows[0]["reservation_id"]);
                    long memId = Convert.ToInt64(hold.Rows[0]["member_id"]);
                    IssueBook(db, memId, retBookId);
                    db.ExecuteNonQuery("UPDATE Reservations SET status = 'Fulfilled' WHERE reservation_id = " + resId + ";");
                    events.Add("🔔 Auto-fulfilled Hold #" + resId + " for " + hold.Rows[0]["name"] + "!");
                }
            }

            // 2. 1 or 2 random active members borrow or reserve a book
            DataTable eligibleMembers = db.Query(
                "SELECT member_id, name FROM vw_MemberLedger WHERE status = 'Active' AND active_loans < max_limit;");
            DataTable allBooks = db.Query("SELECT book_id, title, available_copies FROM Books;");

            int attempts = rng.Next(1, 3);
            for (int i = 0; i < attempts && eligibleMembers.Rows.Count > 0 && allBooks.Rows.Count > 0; i++)
            {
                DataRow mem = eligibleMembers.Rows[rng.Next(eligibleMembers.Rows.Count)];
                DataRow bk = allBooks.Rows[rng.Next(allBooks.Rows.Count)];
                long memId = Convert.ToInt64(mem["member_id"]);
                long bookId = Convert.ToInt64(bk["book_id"]);
                long avail = Convert.ToInt64(bk["available_copies"]);

                // Re-check live availability
                avail = Convert.ToInt64(db.ExecuteScalar("SELECT available_copies FROM Books WHERE book_id = " + bookId + ";"));
                long activeCnt = Convert.ToInt64(db.ExecuteScalar("SELECT COUNT(*) FROM Borrowings WHERE member_id = " + memId + " AND return_date IS NULL;"));
                long maxAllowed = Convert.ToInt64(db.ExecuteScalar("SELECT max_books_allowed FROM Members WHERE member_id = " + memId + ";"));
                if (activeCnt >= maxAllowed) continue;

                if (avail > 0)
                {
                    IssueBook(db, memId, bookId);
                    events.Add("📤 " + mem["name"] + " borrowed \"" + bk["title"] + "\"");
                }
                else
                {
                    long existingHold = Convert.ToInt64(db.ExecuteScalar(
                        "SELECT COUNT(*) FROM Reservations WHERE member_id = " + memId + " AND book_id = " + bookId + " AND status = 'Pending';"));
                    if (existingHold == 0)
                    {
                        db.ExecuteNonQuery(string.Format(CultureInfo.InvariantCulture,
                            "INSERT INTO Reservations (member_id, book_id, reserved_date, status) VALUES ({0}, {1}, {2}, 'Pending');",
                            memId, bookId, SqliteEngine.SqlQuote(simDate)));
                        events.Add("📌 " + mem["name"] + " placed a hold on out-of-stock \"" + bk["title"] + "\"");
                    }
                }
            }

            return string.Join("\n", events);
        }

        public static string ExportMySqlDump(SqliteEngine db)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("-- ============================================================");
            sb.AppendLine("-- Exported Live Snapshot from LibraryOS Desktop Simulator");
            sb.AppendLine("-- Simulated Date: " + GetSimDate(db));
            sb.AppendLine("-- Compatible with MySQL 8.0+ / MySQL Workbench");
            sb.AppendLine("-- ============================================================");
            sb.AppendLine();
            sb.AppendLine("USE LibraryDB;");
            sb.AppendLine("SET FOREIGN_KEY_CHECKS = 0;");
            sb.AppendLine("TRUNCATE TABLE FinePayments;");
            sb.AppendLine("TRUNCATE TABLE Reservations;");
            sb.AppendLine("TRUNCATE TABLE Borrowings;");
            sb.AppendLine("TRUNCATE TABLE Books;");
            sb.AppendLine("TRUNCATE TABLE Members;");
            sb.AppendLine("TRUNCATE TABLE Categories;");
            sb.AppendLine("SET FOREIGN_KEY_CHECKS = 1;");
            sb.AppendLine();

            AppendInsertDump(db, sb, "Categories", "SELECT category_id, category_name, shelf_code FROM Categories ORDER BY category_id;");
            AppendInsertDump(db, sb, "Members", "SELECT member_id, name, email, phone, membership_tier, max_books_allowed, joined_date, status FROM Members ORDER BY member_id;");
            AppendInsertDump(db, sb, "Books", "SELECT book_id, isbn, title, author, category_id, total_copies, available_copies FROM Books ORDER BY book_id;");
            AppendInsertDump(db, sb, "Borrowings", "SELECT borrow_id, member_id, book_id, issue_date, due_date, return_date, renew_count, fine_assessed FROM Borrowings ORDER BY borrow_id;");
            AppendInsertDump(db, sb, "Reservations", "SELECT reservation_id, member_id, book_id, reserved_date, status FROM Reservations ORDER BY reservation_id;");
            AppendInsertDump(db, sb, "FinePayments", "SELECT payment_id, borrow_id, member_id, amount_paid, payment_date, payment_method FROM FinePayments ORDER BY payment_id;");

            return sb.ToString();
        }

        private static void AppendInsertDump(SqliteEngine db, StringBuilder sb, string tableName, string query)
        {
            DataTable dt = db.Query(query);
            if (dt.Rows.Count == 0) return;

            List<string> cols = new List<string>();
            foreach (DataColumn c in dt.Columns) cols.Add(c.ColumnName);

            sb.AppendLine("-- Data for " + tableName);
            sb.AppendLine("INSERT INTO " + tableName + " (" + string.Join(", ", cols) + ") VALUES");
            for (int r = 0; r < dt.Rows.Count; r++)
            {
                List<string> vals = new List<string>();
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    object v = dt.Rows[r][c];
                    if (v == null || v == DBNull.Value) vals.Add("NULL");
                    else if (v is long || v is int) vals.Add(Convert.ToString(v, CultureInfo.InvariantCulture));
                    else if (v is double || v is float || v is decimal) vals.Add(Convert.ToDouble(v).ToString("F2", CultureInfo.InvariantCulture));
                    else vals.Add(SqliteEngine.SqlQuote(Convert.ToString(v)));
                }
                sb.Append("  (" + string.Join(", ", vals) + ")");
                sb.AppendLine(r == dt.Rows.Count - 1 ? ";" : ",");
            }
            sb.AppendLine();
        }
    }

    // =========================================================================
    // 3. UI HELPER MODELS & STYLING
    // =========================================================================
    public sealed class ComboItem
    {
        public long Id { get; set; }
        public string Label { get; set; }
        public string Extra { get; set; }
        public double Amount { get; set; }
        public override string ToString() { return Label; }
    }

    public sealed class PresetQuery
    {
        public string Title { get; set; }
        public string Sql { get; set; }
        public override string ToString() { return Title; }
    }

    // =========================================================================
    // 4. MAIN INTERACTIVE WINDOWS DESKTOP FORM
    // =========================================================================
    public sealed class MainForm : Form
    {
        private static readonly Color ColSidebar = Color.FromArgb(15, 23, 42);      // #0F172A
        private static readonly Color ColSidebarHover = Color.FromArgb(30, 41, 59); // #1E293B
        private static readonly Color ColSidebarActive = Color.FromArgb(37, 99, 235);// #2563EB
        private static readonly Color ColBg = Color.FromArgb(241, 245, 249);        // #F1F5F9
        private static readonly Color ColCard = Color.White;
        private static readonly Color ColText = Color.FromArgb(15, 23, 42);
        private static readonly Color ColMuted = Color.FromArgb(100, 116, 139);
        private static readonly Color ColPrimary = Color.FromArgb(37, 99, 235);
        private static readonly Color ColSuccess = Color.FromArgb(5, 150, 105);
        private static readonly Color ColWarning = Color.FromArgb(217, 119, 6);
        private static readonly Color ColDanger = Color.FromArgb(220, 38, 38);
        private static readonly Color ColPurple = Color.FromArgb(124, 58, 237);

        private readonly SqliteEngine _db;
        private readonly Random _rng = new Random();

        private readonly List<Button> _navButtons = new List<Button>();
        private readonly List<Panel> _pages = new List<Panel>();

        private Label _lblPageTitle;
        private Label _lblSimDate;
        private Label _lblSqlTicker;

        // Dashboard controls
        private Label _kpiBooksVal, _kpiBooksSub;
        private Label _kpiLoansVal, _kpiLoansSub;
        private Label _kpiOverdueVal, _kpiOverdueSub;
        private Label _kpiHoldsVal, _kpiHoldsSub;
        private Label _kpiFinesVal, _kpiFinesSub;
        private DataGridView _gridDashActive;
        private DataGridView _gridDashLog;

        // Circulation Desk controls
        private ComboBox _cmbIssueMember;
        private ComboBox _cmbIssueBook;
        private Label _lblLoanRulePreview;
        private DataGridView _gridCircActive;

        // Books controls
        private TextBox _txtBookSearch;
        private ComboBox _cmbBookCatFilter;
        private CheckBox _chkAvailOnly;
        private TextBox _txtNewIsbn, _txtNewTitle, _txtNewAuthor;
        private ComboBox _cmbNewBookCat;
        private NumericUpDown _numNewCopies;
        private DataGridView _gridBooks;

        // Members controls
        private TextBox _txtMemName, _txtMemEmail, _txtMemPhone;
        private ComboBox _cmbMemTier;
        private DataGridView _gridMembers;

        // Holds & Cashier controls
        private ComboBox _cmbHoldMember, _cmbHoldBook;
        private DataGridView _gridHolds;
        private ComboBox _cmbUnpaidBorrow;
        private NumericUpDown _numPayAmount;
        private ComboBox _cmbPayMethod;
        private DataGridView _gridPayments;

        // Live SQL Studio controls
        private ComboBox _cmbPresets;
        private TextBox _txtSqlEditor;
        private Label _lblSqlResultMeta;
        private DataGridView _gridSqlResult;

        private SplitContainer _splitDash;
        private SplitContainer _splitHoldsCashier;
        private SplitContainer _splitSqlStudio;

        public MainForm(SqliteEngine db)
        {
            _db = db;
            _db.OnSqlExecuted += sql =>
            {
                if (_lblSqlTicker != null && !_lblSqlTicker.IsDisposed)
                    _lblSqlTicker.Text = "⚡ SQL > " + sql;
            };

            Text = "LibraryOS — Interactive Library Management System & SQL Studio";
            Size = new Size(1320, 830);
            MinimumSize = new Size(1120, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            BackColor = ColBg;
            ForeColor = ColText;
            KeyPreview = true;

            BuildShell();
            SwitchPage(0, "📊  Dashboard & Live Overview");
            RefreshAll();

            Shown += (s, e) => ApplySplitterProportions();
        }

        private void ApplySplitterProportions()
        {
            try
            {
                if (_splitDash != null && _splitDash.Height > 200)
                    _splitDash.SplitterDistance = (int)(_splitDash.Height * 0.54);
                if (_splitHoldsCashier != null && _splitHoldsCashier.Width > 300)
                    _splitHoldsCashier.SplitterDistance = (int)(_splitHoldsCashier.Width * 0.50);
                if (_splitSqlStudio != null && _splitSqlStudio.Height > 200)
                    _splitSqlStudio.SplitterDistance = Math.Max(155, (int)(_splitSqlStudio.Height * 0.32));
            }
            catch { }
        }

        private void BuildShell()
        {
            // 1. Bottom SQL Execution Ticker
            Panel bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                BackColor = ColSidebar,
                Padding = new Padding(12, 0, 12, 0)
            };

            Button btnJumpSql = CreateFlatButton("Open SQL Studio →", ColSidebarHover, Color.FromArgb(125, 211, 252), 145, 24);
            btnJumpSql.Dock = DockStyle.Right;
            btnJumpSql.Font = new Font("Segoe UI Semibold", 8.5f);
            btnJumpSql.Click += (s, e) => SwitchPage(5, "💻  Live SQL Studio & Viva Queries");

            _lblSqlTicker = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Consolas", 9.0f),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "⚡ SQL > Engine ready (winsqlite3 relational engine • PRAGMA foreign_keys = ON)"
            };
            bottomBar.Controls.Add(_lblSqlTicker);
            bottomBar.Controls.Add(btnJumpSql);

            // 2. Left Navigation Sidebar
            Panel sidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 235,
                BackColor = ColSidebar
            };

            Panel brandBox = new Panel
            {
                Dock = DockStyle.Top,
                Height = 82,
                Padding = new Padding(18, 16, 12, 10)
            };
            Label lblBrand = new Label
            {
                Text = "📚 LibraryOS",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = Color.White,
                Dock = DockStyle.Top,
                Height = 34
            };
            Label lblSubBrand = new Label
            {
                Text = "SQL Simulator & DBMS Viva Studio",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(148, 163, 184),
                Dock = DockStyle.Top,
                Height = 22
            };
            brandBox.Controls.Add(lblSubBrand);
            brandBox.Controls.Add(lblBrand);

            string[] navTitles = new[]
            {
                "📊  Dashboard",
                "🔄  Circulation Desk",
                "📚  Books & Catalog",
                "👥  Members & Tiers",
                "📌  Holds & Cashier",
                "💻  Live SQL Studio"
            };
            string[] pageHeaders = new[]
            {
                "📊  Dashboard & Live Overview",
                "🔄  Circulation Desk (Issue • Return • Renew)",
                "📚  Book Inventory & Popularity",
                "👥  Library Members & Tier Ledger",
                "📌  Hold Queue & Fine Cashier",
                "💻  Live SQL Studio & Viva Queries"
            };

            Panel navHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6) };
            for (int i = navTitles.Length - 1; i >= 0; i--)
            {
                int idx = i;
                Button b = new Button
                {
                    Text = "  " + navTitles[i],
                    Dock = DockStyle.Top,
                    Height = 44,
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI Semibold", 10f),
                    ForeColor = Color.White,
                    BackColor = ColSidebar,
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = ColSidebarHover;
                b.Click += (s, e) => SwitchPage(idx, pageHeaders[idx]);
                _navButtons.Insert(0, b);
                navHost.Controls.Add(b);
            }

            Panel sideBottom = new Panel { Dock = DockStyle.Bottom, Height = 98, Padding = new Padding(12, 6, 12, 12) };
            Button btnExportSql = CreateFlatButton("💾  Export MySQL (.sql)", Color.FromArgb(30, 41, 59), Color.White, 200, 36);
            btnExportSql.Dock = DockStyle.Top;
            btnExportSql.Click += OnExportMySql;

            Panel spacer = new Panel { Dock = DockStyle.Top, Height = 8 };

            Button btnResetDb = CreateFlatButton("♻️  Reset Demo DB", Color.FromArgb(69, 10, 10), Color.FromArgb(252, 165, 165), 200, 34);
            btnResetDb.Dock = DockStyle.Top;
            btnResetDb.Click += OnResetDatabase;

            sideBottom.Controls.Add(btnResetDb);
            sideBottom.Controls.Add(spacer);
            sideBottom.Controls.Add(btnExportSql);

            sidebar.Controls.Add(navHost);
            sidebar.Controls.Add(sideBottom);
            sidebar.Controls.Add(brandBox);

            // 3. Main Workspace (Top Simulator Header + Content Pages)
            Panel workspace = new Panel { Dock = DockStyle.Fill, BackColor = ColBg };

            Panel topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.White,
                Padding = new Padding(20, 10, 16, 10)
            };
            topBar.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.FromArgb(226, 232, 240), 1))
                    e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };

            _lblPageTitle = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 13.5f),
                ForeColor = ColText,
                TextAlign = ContentAlignment.MiddleLeft
            };

            FlowLayoutPanel simBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 575,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 3, 0, 0)
            };

            Button btnRushHour = CreateFlatButton("🎲 Rush Hour!", ColPurple, Color.White, 125, 34);
            btnRushHour.Click += OnSimulateRushHour;

            Button btnPlus7 = CreateFlatButton("⏩ +7 Days", ColPrimary, Color.White, 102, 34);
            btnPlus7.Click += (s, e) => { LibraryBootstrap.AdvanceSimDays(_db, 7); RefreshAll(); };

            Button btnPlus1 = CreateFlatButton("▶️ +1 Day", Color.FromArgb(2, 132, 199), Color.White, 96, 34);
            btnPlus1.Click += (s, e) => { LibraryBootstrap.AdvanceSimDays(_db, 1); RefreshAll(); };

            _lblSimDate = new Label
            {
                AutoSize = false,
                Size = new Size(215, 34),
                BackColor = Color.FromArgb(254, 243, 199),
                ForeColor = Color.FromArgb(146, 64, 14),
                Font = new Font("Segoe UI Semibold", 9.5f),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(4, 3, 8, 3),
                Text = "📅 Sim Date: 2026-11-25"
            };

            simBar.Controls.Add(btnRushHour);
            simBar.Controls.Add(btnPlus7);
            simBar.Controls.Add(btnPlus1);
            simBar.Controls.Add(_lblSimDate);

            topBar.Controls.Add(_lblPageTitle);
            topBar.Controls.Add(simBar);

            Panel contentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };

            _pages.Add(BuildDashboardPage());
            _pages.Add(BuildCirculationPage());
            _pages.Add(BuildBooksPage());
            _pages.Add(BuildMembersPage());
            _pages.Add(BuildHoldsAndCashierPage());
            _pages.Add(BuildSqlStudioPage());

            foreach (Panel p in _pages)
            {
                p.Dock = DockStyle.Fill;
                p.Visible = false;
                contentHost.Controls.Add(p);
            }

            workspace.Controls.Add(contentHost);
            workspace.Controls.Add(topBar);

            Controls.Add(workspace);
            Controls.Add(sidebar);
            Controls.Add(bottomBar);
        }

        private void SwitchPage(int index, string headerTitle)
        {
            _lblPageTitle.Text = headerTitle;
            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].Visible = (i == index);
                _navButtons[i].BackColor = (i == index) ? ColSidebarActive : ColSidebar;
            }
        }

        // =====================================================================
        // PAGE 1: DASHBOARD
        // =====================================================================
        private Panel BuildDashboardPage()
        {
            Panel page = new Panel();

            TableLayoutPanel kpiRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 104,
                ColumnCount = 5,
                RowCount = 1,
                Margin = new Padding(0)
            };
            for (int i = 0; i < 5; i++)
                kpiRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));

            kpiRow.Controls.Add(CreateKpiCard("📚 Available Stock", ColPrimary, out _kpiBooksVal, out _kpiBooksSub), 0, 0);
            kpiRow.Controls.Add(CreateKpiCard("🔄 Active Loans", ColSuccess, out _kpiLoansVal, out _kpiLoansSub), 1, 0);
            kpiRow.Controls.Add(CreateKpiCard("⚠️ Overdue Loans", ColDanger, out _kpiOverdueVal, out _kpiOverdueSub), 2, 0);
            kpiRow.Controls.Add(CreateKpiCard("📌 Hold Queue", ColWarning, out _kpiHoldsVal, out _kpiHoldsSub), 3, 0);
            kpiRow.Controls.Add(CreateKpiCard("💰 Net Fines Due", ColPurple, out _kpiFinesVal, out _kpiFinesSub), 4, 0);

            Panel gap = new Panel { Dock = DockStyle.Top, Height = 12 };

            _splitDash = new SplitContainer
            {
                Size = new Size(1000, 600),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 310,
                SplitterWidth = 8
            };

            _gridDashActive = CreateStyledGrid();
            _gridDashActive.CellFormatting += FormatActiveBorrowingsGrid;
            _splitDash.Panel1.Controls.Add(WrapGridCard("🔥 Active & Overdue Borrowings (SQL View: vw_ActiveBorrowings)", _gridDashActive));

            _gridDashLog = CreateStyledGrid();
            _splitDash.Panel2.Controls.Add(WrapGridCard("⚡ Automated SQL Trigger Audit Trail (Table: ActivityLog)", _gridDashLog));

            page.Controls.Add(_splitDash);
            page.Controls.Add(gap);
            page.Controls.Add(kpiRow);
            return page;
        }

        private Panel CreateKpiCard(string title, Color accent, out Label valLbl, out Label subLbl)
        {
            Panel card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ColCard,
                Margin = new Padding(5, 0, 5, 0),
                Padding = new Padding(14, 10, 14, 8)
            };
            Panel stripe = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = accent };
            Label lblTitle = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI Semibold", 9f),
                ForeColor = ColMuted
            };
            valLbl = new Label
            {
                Text = "0",
                Dock = DockStyle.Top,
                Height = 38,
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                ForeColor = ColText
            };
            subLbl = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = ColMuted
            };
            card.Controls.Add(subLbl);
            card.Controls.Add(valLbl);
            card.Controls.Add(lblTitle);
            card.Controls.Add(stripe);
            return card;
        }

        // =====================================================================
        // PAGE 2: CIRCULATION DESK (ISSUE / RETURN / RENEW)
        // =====================================================================
        private Panel BuildCirculationPage()
        {
            Panel page = new Panel();

            Panel issueCard = new Panel
            {
                Dock = DockStyle.Top,
                Height = 118,
                BackColor = ColCard,
                Padding = new Padding(16, 12, 16, 12)
            };

            Label lblHeader = new Label
            {
                Text = "📤 Issue or Reserve a Book (Enforces Tier Limits, Member Status & Stock via SQL Triggers)",
                Dock = DockStyle.Top,
                Height = 24,
                Font = new Font("Segoe UI Semibold", 10f),
                ForeColor = ColText
            };

            FlowLayoutPanel issueRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };

            _cmbIssueMember = new ComboBox { Width = 310, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 12, 0) };
            _cmbIssueMember.SelectedIndexChanged += UpdateLoanRulePreview;

            _cmbIssueBook = new ComboBox { Width = 360, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 12, 0) };

            Button btnIssue = CreateFlatButton("📤 Issue Book", ColSuccess, Color.White, 140, 32);
            btnIssue.Click += OnIssueBookClick;

            Button btnReserveInstead = CreateFlatButton("📌 Place Hold", ColWarning, Color.White, 130, 32);
            btnReserveInstead.Click += OnQuickReserveFromDesk;

            issueRow.Controls.Add(MakeFieldLabel("Member:"));
            issueRow.Controls.Add(_cmbIssueMember);
            issueRow.Controls.Add(MakeFieldLabel("Book:"));
            issueRow.Controls.Add(_cmbIssueBook);
            issueRow.Controls.Add(btnIssue);
            issueRow.Controls.Add(btnReserveInstead);

            _lblLoanRulePreview = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Italic),
                ForeColor = ColPrimary,
                TextAlign = ContentAlignment.BottomLeft,
                Text = "Select a member to preview their tier loan duration and fine rate."
            };

            issueCard.Controls.Add(_lblLoanRulePreview);
            issueCard.Controls.Add(issueRow);
            issueCard.Controls.Add(lblHeader);

            Panel gap = new Panel { Dock = DockStyle.Top, Height = 12 };

            Panel gridCard = new Panel { Dock = DockStyle.Fill, BackColor = ColCard, Padding = new Padding(14) };
            Panel actionHeader = new Panel { Dock = DockStyle.Top, Height = 42 };

            Label lblGridTitle = new Label
            {
                Text = "🔄 Active Loans — Select a row below to Return or Renew",
                Dock = DockStyle.Left,
                Width = 430,
                Font = new Font("Segoe UI Semibold", 10f),
                TextAlign = ContentAlignment.MiddleLeft
            };

            FlowLayoutPanel actionBtns = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 370,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };

            Button btnRenew = CreateFlatButton("🔁 Renew (+7 Days)", ColPrimary, Color.White, 165, 32);
            btnRenew.Click += OnRenewSelectedClick;

            Button btnReturn = CreateFlatButton("✅ Return Book", ColSuccess, Color.White, 160, 32);
            btnReturn.Click += OnReturnSelectedClick;

            actionBtns.Controls.Add(btnRenew);
            actionBtns.Controls.Add(btnReturn);
            actionHeader.Controls.Add(lblGridTitle);
            actionHeader.Controls.Add(actionBtns);

            _gridCircActive = CreateStyledGrid();
            _gridCircActive.CellFormatting += FormatActiveBorrowingsGrid;

            gridCard.Controls.Add(_gridCircActive);
            gridCard.Controls.Add(actionHeader);

            page.Controls.Add(gridCard);
            page.Controls.Add(gap);
            page.Controls.Add(issueCard);
            return page;
        }

        // =====================================================================
        // PAGE 3: BOOKS & CATALOG
        // =====================================================================
        private Panel BuildBooksPage()
        {
            Panel page = new Panel();

            Panel topCard = new Panel
            {
                Dock = DockStyle.Top,
                Height = 112,
                BackColor = ColCard,
                Padding = new Padding(14, 10, 14, 10)
            };

            FlowLayoutPanel filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false };
            _txtBookSearch = new TextBox { Width = 220, Margin = new Padding(0, 5, 12, 0) };
            _txtBookSearch.TextChanged += (s, e) => RefreshBooksGrid();

            _cmbBookCatFilter = new ComboBox { Width = 155, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 12, 0) };
            _cmbBookCatFilter.SelectedIndexChanged += (s, e) => RefreshBooksGrid();

            _chkAvailOnly = new CheckBox { Text = "In-Stock Only", AutoSize = true, Margin = new Padding(4, 7, 16, 0) };
            _chkAvailOnly.CheckedChanged += (s, e) => RefreshBooksGrid();

            Button btnRestock = CreateFlatButton("➕ Restock (+1 Copy)", ColPrimary, Color.White, 165, 30);
            btnRestock.Click += OnRestockBookClick;

            Button btnDeleteBook = CreateFlatButton("🗑️ Delete Unused", ColDanger, Color.White, 145, 30);
            btnDeleteBook.Click += OnDeleteBookClick;

            filterRow.Controls.Add(MakeFieldLabel("Search:"));
            filterRow.Controls.Add(_txtBookSearch);
            filterRow.Controls.Add(MakeFieldLabel("Category:"));
            filterRow.Controls.Add(_cmbBookCatFilter);
            filterRow.Controls.Add(_chkAvailOnly);
            filterRow.Controls.Add(btnRestock);
            filterRow.Controls.Add(btnDeleteBook);

            FlowLayoutPanel addRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            _txtNewIsbn = new TextBox { Width = 120, Text = "978-0131103627", Margin = new Padding(0, 5, 10, 0) };
            _txtNewTitle = new TextBox { Width = 195, Text = "The C Programming Language", Margin = new Padding(0, 5, 10, 0) };
            _txtNewAuthor = new TextBox { Width = 150, Text = "Brian Kernighan", Margin = new Padding(0, 5, 10, 0) };
            _cmbNewBookCat = new ComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 10, 0) };
            _numNewCopies = new NumericUpDown { Width = 58, Minimum = 1, Maximum = 50, Value = 3, Margin = new Padding(0, 5, 10, 0) };

            Button btnAddBook = CreateFlatButton("➕ Add Book", ColSuccess, Color.White, 120, 30);
            btnAddBook.Click += OnAddBookClick;

            addRow.Controls.Add(MakeFieldLabel("ISBN:"));
            addRow.Controls.Add(_txtNewIsbn);
            addRow.Controls.Add(MakeFieldLabel("Title:"));
            addRow.Controls.Add(_txtNewTitle);
            addRow.Controls.Add(MakeFieldLabel("Author:"));
            addRow.Controls.Add(_txtNewAuthor);
            addRow.Controls.Add(MakeFieldLabel("Cat:"));
            addRow.Controls.Add(_cmbNewBookCat);
            addRow.Controls.Add(MakeFieldLabel("Qty:"));
            addRow.Controls.Add(_numNewCopies);
            addRow.Controls.Add(btnAddBook);

            topCard.Controls.Add(addRow);
            topCard.Controls.Add(filterRow);

            Panel gap = new Panel { Dock = DockStyle.Top, Height = 12 };

            _gridBooks = CreateStyledGrid();
            _gridBooks.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || _gridBooks.Columns["available"] == null) return;
                object v = _gridBooks.Rows[e.RowIndex].Cells["available"].Value;
                if (v != null && v != DBNull.Value && Convert.ToInt64(v) == 0)
                    _gridBooks.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(254, 243, 199);
            };

            page.Controls.Add(WrapGridCard("📚 Book Catalog, Stock & Borrow Popularity (SQL View: vw_BookPopularity)", _gridBooks));
            page.Controls.Add(gap);
            page.Controls.Add(topCard);
            return page;
        }

        // =====================================================================
        // PAGE 4: MEMBERS & TIERS
        // =====================================================================
        private Panel BuildMembersPage()
        {
            Panel page = new Panel();

            Panel topCard = new Panel
            {
                Dock = DockStyle.Top,
                Height = 104,
                BackColor = ColCard,
                Padding = new Padding(14, 10, 14, 10)
            };

            Label lblInfo = new Label
            {
                Text = "👥 Tier Rules: Student (Max 3 • 10d • ₹5/day)  |  Premium (Max 5 • 15d • ₹3/day)  |  Faculty (Max 8 • 30d • ₹2/day)",
                Dock = DockStyle.Top,
                Height = 24,
                Font = new Font("Segoe UI Semibold", 9.2f),
                ForeColor = ColPrimary
            };

            FlowLayoutPanel regRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
            _txtMemName = new TextBox { Width = 155, Text = "Ananya Gupta", Margin = new Padding(0, 5, 10, 0) };
            _txtMemEmail = new TextBox { Width = 175, Text = "ananya@gmail.com", Margin = new Padding(0, 5, 10, 0) };
            _txtMemPhone = new TextBox { Width = 115, Text = "9876500111", Margin = new Padding(0, 5, 10, 0) };
            _cmbMemTier = new ComboBox { Width = 115, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 10, 0) };
            _cmbMemTier.Items.AddRange(new object[] { "Student", "Premium", "Faculty" });
            _cmbMemTier.SelectedIndex = 1;

            Button btnAddMem = CreateFlatButton("➕ Register Member", ColSuccess, Color.White, 155, 30);
            btnAddMem.Click += OnRegisterMemberClick;

            Button btnToggleStatus = CreateFlatButton("⏯️ Suspend / Activate", ColWarning, Color.White, 165, 30);
            btnToggleStatus.Click += OnToggleMemberStatusClick;

            regRow.Controls.Add(MakeFieldLabel("Name:"));
            regRow.Controls.Add(_txtMemName);
            regRow.Controls.Add(MakeFieldLabel("Email:"));
            regRow.Controls.Add(_txtMemEmail);
            regRow.Controls.Add(MakeFieldLabel("Phone:"));
            regRow.Controls.Add(_txtMemPhone);
            regRow.Controls.Add(MakeFieldLabel("Tier:"));
            regRow.Controls.Add(_cmbMemTier);
            regRow.Controls.Add(btnAddMem);
            regRow.Controls.Add(btnToggleStatus);

            topCard.Controls.Add(regRow);
            topCard.Controls.Add(lblInfo);

            Panel gap = new Panel { Dock = DockStyle.Top, Height = 12 };

            _gridMembers = CreateStyledGrid();
            _gridMembers.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || _gridMembers.Columns["status"] == null) return;
                string st = Convert.ToString(_gridMembers.Rows[e.RowIndex].Cells["status"].Value);
                if (st == "Suspended")
                    _gridMembers.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(254, 226, 226);
            };

            page.Controls.Add(WrapGridCard("👥 Member Circulation & Financial Ledger (SQL View: vw_MemberLedger)", _gridMembers));
            page.Controls.Add(gap);
            page.Controls.Add(topCard);
            return page;
        }

        // =====================================================================
        // PAGE 5: RESERVATIONS (HOLD QUEUE) & FINE CASHIER
        // =====================================================================
        private Panel BuildHoldsAndCashierPage()
        {
            Panel page = new Panel();

            _splitHoldsCashier = new SplitContainer
            {
                Size = new Size(1040, 600),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 510,
                SplitterWidth = 10
            };

            // Left: Reservations
            Panel leftCard = new Panel { Dock = DockStyle.Fill, BackColor = ColCard, Padding = new Padding(14) };
            Label lblHoldTitle = new Label
            {
                Text = "📌 Book Hold Queue (Table: Reservations)",
                Dock = DockStyle.Top,
                Height = 26,
                Font = new Font("Segoe UI Semibold", 10.5f)
            };

            FlowLayoutPanel holdAddRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false };
            _cmbHoldMember = new ComboBox { Width = 165, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 8, 0) };
            _cmbHoldBook = new ComboBox { Width = 185, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 8, 0) };
            Button btnAddHold = CreateFlatButton("📌 Place Hold", ColWarning, Color.White, 115, 30);
            btnAddHold.Click += OnPlaceHoldClick;
            holdAddRow.Controls.Add(_cmbHoldMember);
            holdAddRow.Controls.Add(_cmbHoldBook);
            holdAddRow.Controls.Add(btnAddHold);

            FlowLayoutPanel holdActionRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
            Button btnFulfillHold = CreateFlatButton("✅ Fulfill & Issue Selected", ColSuccess, Color.White, 195, 30);
            btnFulfillHold.Click += OnFulfillHoldClick;
            Button btnCancelHold = CreateFlatButton("❌ Cancel Hold", ColDanger, Color.White, 130, 30);
            btnCancelHold.Click += OnCancelHoldClick;
            holdActionRow.Controls.Add(btnFulfillHold);
            holdActionRow.Controls.Add(btnCancelHold);

            _gridHolds = CreateStyledGrid();
            leftCard.Controls.Add(_gridHolds);
            leftCard.Controls.Add(holdActionRow);
            leftCard.Controls.Add(holdAddRow);
            leftCard.Controls.Add(lblHoldTitle);
            _splitHoldsCashier.Panel1.Controls.Add(leftCard);

            // Right: Fine Cashier
            Panel rightCard = new Panel { Dock = DockStyle.Fill, BackColor = ColCard, Padding = new Padding(14) };
            Label lblCashierTitle = new Label
            {
                Text = "💰 Fine Cashier & Payment Receipts (Table: FinePayments)",
                Dock = DockStyle.Top,
                Height = 26,
                Font = new Font("Segoe UI Semibold", 10.5f)
            };

            FlowLayoutPanel payRow1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false };
            _cmbUnpaidBorrow = new ComboBox { Width = 380, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 0, 0) };
            _cmbUnpaidBorrow.SelectedIndexChanged += (s, e) =>
            {
                ComboItem item = _cmbUnpaidBorrow.SelectedItem as ComboItem;
                if (item != null && item.Amount > 0 && _numPayAmount != null)
                {
                    decimal clamped = Math.Min(_numPayAmount.Maximum, Math.Max(_numPayAmount.Minimum, (decimal)item.Amount));
                    _numPayAmount.Value = clamped;
                }
            };
            payRow1.Controls.Add(MakeFieldLabel("Unpaid Fine:"));
            payRow1.Controls.Add(_cmbUnpaidBorrow);

            FlowLayoutPanel payRow2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, WrapContents = false };
            _numPayAmount = new NumericUpDown { Width = 95, DecimalPlaces = 2, Minimum = 0.50m, Maximum = 50000m, Value = 10m, Margin = new Padding(0, 5, 10, 0) };
            _cmbPayMethod = new ComboBox { Width = 95, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 10, 0) };
            _cmbPayMethod.Items.AddRange(new object[] { "UPI", "Cash", "Card" });
            _cmbPayMethod.SelectedIndex = 0;

            Button btnCollectFine = CreateFlatButton("💳 Collect Fine Payment", ColPurple, Color.White, 185, 30);
            btnCollectFine.Click += OnCollectFineClick;

            payRow2.Controls.Add(MakeFieldLabel("Amount (₹):"));
            payRow2.Controls.Add(_numPayAmount);
            payRow2.Controls.Add(MakeFieldLabel("Via:"));
            payRow2.Controls.Add(_cmbPayMethod);
            payRow2.Controls.Add(btnCollectFine);

            _gridPayments = CreateStyledGrid();
            rightCard.Controls.Add(_gridPayments);
            rightCard.Controls.Add(payRow2);
            rightCard.Controls.Add(payRow1);
            rightCard.Controls.Add(lblCashierTitle);
            _splitHoldsCashier.Panel2.Controls.Add(rightCard);

            page.Controls.Add(_splitHoldsCashier);
            return page;
        }

        // =====================================================================
        // PAGE 6: LIVE SQL STUDIO & VIVA QUERIES
        // =====================================================================
        private Panel BuildSqlStudioPage()
        {
            Panel page = new Panel();

            Panel topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = ColCard,
                Padding = new Padding(14, 9, 14, 9)
            };

            _cmbPresets = new ComboBox { Width = 540, DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Left };
            PopulatePresetQueries();
            _cmbPresets.SelectedIndexChanged += (s, e) =>
            {
                PresetQuery pq = _cmbPresets.SelectedItem as PresetQuery;
                if (pq != null && _txtSqlEditor != null)
                {
                    _txtSqlEditor.Text = pq.Sql;
                    RunCustomSql();
                }
            };

            Button btnRunSql = CreateFlatButton("▶️ Execute SQL (F5)", ColSuccess, Color.White, 165, 32);
            btnRunSql.Dock = DockStyle.Right;
            btnRunSql.Click += (s, e) => RunCustomSql();

            topBar.Controls.Add(btnRunSql);
            topBar.Controls.Add(_cmbPresets);

            Panel gap = new Panel { Dock = DockStyle.Top, Height = 10 };

            _splitSqlStudio = new SplitContainer
            {
                Size = new Size(1000, 600),
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 175,
                SplitterWidth = 8
            };

            _txtSqlEditor = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsTab = true,
                Font = new Font("Consolas", 10.5f),
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(226, 232, 240),
                BorderStyle = BorderStyle.None
            };
            _txtSqlEditor.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.Enter))
                {
                    e.SuppressKeyPress = true;
                    RunCustomSql();
                }
            };

            Panel editorCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 42), Padding = new Padding(12) };
            editorCard.Controls.Add(_txtSqlEditor);
            _splitSqlStudio.Panel1.Controls.Add(editorCard);

            Panel resCard = new Panel { Dock = DockStyle.Fill, BackColor = ColCard, Padding = new Padding(12) };
            _lblSqlResultMeta = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Font = new Font("Segoe UI Semibold", 9.2f),
                ForeColor = ColPrimary,
                Text = "Query Output"
            };
            _gridSqlResult = CreateStyledGrid();
            resCard.Controls.Add(_gridSqlResult);
            resCard.Controls.Add(_lblSqlResultMeta);
            _splitSqlStudio.Panel2.Controls.Add(resCard);

            page.Controls.Add(_splitSqlStudio);
            page.Controls.Add(gap);
            page.Controls.Add(topBar);

            if (_cmbPresets.Items.Count > 0)
                _cmbPresets.SelectedIndex = 0;

            return page;
        }

        private void PopulatePresetQueries()
        {
            _cmbPresets.Items.AddRange(new object[]
            {
                new PresetQuery
                {
                    Title = "1. [VIEW] Active Borrowings + Live Tier-Based Fines (vw_ActiveBorrowings)",
                    Sql = "SELECT * FROM vw_ActiveBorrowings\r\nORDER BY days_overdue DESC, borrow_id ASC;"
                },
                new PresetQuery
                {
                    Title = "2. [VIEW] Complete Member Financial Ledger (vw_MemberLedger)",
                    Sql = "SELECT * FROM vw_MemberLedger\r\nORDER BY net_due_rs DESC, total_borrows DESC;"
                },
                new PresetQuery
                {
                    Title = "3. [JOIN + GROUP BY] Book Popularity & Hold Queue (vw_BookPopularity)",
                    Sql = "SELECT * FROM vw_BookPopularity\r\nORDER BY times_borrowed DESC, pending_holds DESC;"
                },
                new PresetQuery
                {
                    Title = "4. [SUBQUERY] Books That Have NEVER Been Borrowed",
                    Sql = "SELECT book_id, isbn, title, author, available_copies\r\nFROM Books\r\nWHERE book_id NOT IN (\r\n    SELECT DISTINCT book_id FROM Borrowings\r\n);"
                },
                new PresetQuery
                {
                    Title = "5. [MULTI-JOIN] Full Borrowing History with Return Status & Assessed Fines",
                    Sql = "SELECT b.borrow_id, m.name AS member, m.membership_tier AS tier,\r\n       bk.title AS book, b.issue_date, b.due_date,\r\n       COALESCE(b.return_date, 'STILL BORROWED') AS return_status,\r\n       b.renew_count, b.fine_assessed\r\nFROM Borrowings b\r\nJOIN Members m ON b.member_id = m.member_id\r\nJOIN Books bk ON b.book_id = bk.book_id\r\nORDER BY b.borrow_id DESC;"
                },
                new PresetQuery
                {
                    Title = "6. [GROUP BY + AGGREGATE] Circulation & Stock Analytics by Category",
                    Sql = "SELECT c.category_name, c.shelf_code,\r\n       COUNT(DISTINCT bk.book_id) AS distinct_titles,\r\n       COALESCE(SUM(bk.total_copies), 0) AS total_copies,\r\n       COUNT(b.borrow_id) AS total_borrows\r\nFROM Categories c\r\nLEFT JOIN Books bk ON c.category_id = bk.category_id\r\nLEFT JOIN Borrowings b ON bk.book_id = b.book_id\r\nGROUP BY c.category_id, c.category_name, c.shelf_code\r\nORDER BY total_borrows DESC;"
                },
                new PresetQuery
                {
                    Title = "7. [FIFO QUEUE] Pending Book Reservations Ranked by Wait Priority",
                    Sql = "SELECT r.reservation_id, bk.title AS requested_book, bk.available_copies,\r\n       m.name AS waiting_member, m.membership_tier, r.reserved_date\r\nFROM Reservations r\r\nJOIN Books bk ON r.book_id = bk.book_id\r\nJOIN Members m ON r.member_id = m.member_id\r\nWHERE r.status = 'Pending'\r\nORDER BY r.book_id, r.reservation_id ASC;"
                },
                new PresetQuery
                {
                    Title = "8. [SCHEMA INTROSPECTION] Inspect All SQL Triggers & Views in Engine",
                    Sql = "SELECT type, name, tbl_name\r\nFROM sqlite_master\r\nWHERE type IN ('table', 'view', 'trigger')\r\nORDER BY type, name;"
                }
            });
        }

        private void RunCustomSql()
        {
            string sql = _txtSqlEditor.Text;
            if (string.IsNullOrWhiteSpace(sql)) return;

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                DataTable dt = _db.Query(sql, true);
                sw.Stop();
                if (dt.Columns.Count > 0)
                {
                    _gridSqlResult.DataSource = dt;
                    _lblSqlResultMeta.ForeColor = ColSuccess;
                    _lblSqlResultMeta.Text = string.Format("✅ Returned {0} row(s) in {1} ms", dt.Rows.Count, sw.ElapsedMilliseconds);
                }
                else
                {
                    RefreshAll();
                    DataTable msgDt = new DataTable();
                    msgDt.Columns.Add("Execution Result");
                    msgDt.Rows.Add("Statement executed successfully in " + sw.ElapsedMilliseconds + " ms.");
                    _gridSqlResult.DataSource = msgDt;
                    _lblSqlResultMeta.ForeColor = ColSuccess;
                    _lblSqlResultMeta.Text = "✅ DML / DDL statement executed successfully";
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _lblSqlResultMeta.ForeColor = ColDanger;
                _lblSqlResultMeta.Text = "❌ SQL Error: " + ex.Message;
                MessageBox.Show(ex.Message, "SQL Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // =====================================================================
        // EVENT HANDLERS & INTERACTIVE ACTIONS
        // =====================================================================
        private void UpdateLoanRulePreview(object sender, EventArgs e)
        {
            ComboItem item = _cmbIssueMember.SelectedItem as ComboItem;
            if (item == null) return;
            int maxB, days, rate;
            LibraryBootstrap.TierRules(item.Extra, out maxB, out days, out rate);
            _lblLoanRulePreview.Text = string.Format(
                "✨ Selected Tier [{0}]: Max {1} active books  •  {2}-day loan window  •  Late fine ₹{3}/day",
                item.Extra, maxB, days, rate);
        }

        private void OnIssueBookClick(object sender, EventArgs e)
        {
            ComboItem mem = _cmbIssueMember.SelectedItem as ComboItem;
            ComboItem bk = _cmbIssueBook.SelectedItem as ComboItem;
            if (mem == null || bk == null) return;

            try
            {
                LibraryBootstrap.IssueBook(_db, mem.Id, bk.Id);
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Trigger / Business Rule Blocked Issue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnQuickReserveFromDesk(object sender, EventArgs e)
        {
            ComboItem mem = _cmbIssueMember.SelectedItem as ComboItem;
            ComboItem bk = _cmbIssueBook.SelectedItem as ComboItem;
            if (mem == null || bk == null) return;
            PlaceReservationInternal(mem.Id, bk.Id);
        }

        private void OnReturnSelectedClick(object sender, EventArgs e)
        {
            if (_gridCircActive.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select an active borrowing row first.", "Return Book", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            long borrowId = Convert.ToInt64(_gridCircActive.SelectedRows[0].Cells["borrow_id"].Value);
            string bookTitle = Convert.ToString(_gridCircActive.SelectedRows[0].Cells["book_title"].Value);

            try
            {
                long returnedBookId;
                double fine = LibraryBootstrap.ReturnBook(_db, borrowId, out returnedBookId);
                RefreshAll();

                string msg = fine > 0
                    ? string.Format("Book \"{0}\" returned!\n\n⚠️ Late Return Fine Assessed: ₹{1:F2}\n(You can collect payment in the Holds & Cashier tab.)", bookTitle, fine)
                    : string.Format("Book \"{0}\" returned on time! (Fine: ₹0.00)", bookTitle);
                MessageBox.Show(msg, "Book Returned", MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Check if someone is waiting in the Hold Queue!
                DataTable nextHold = _db.Query(
                    "SELECT r.reservation_id, r.member_id, m.name FROM Reservations r " +
                    "JOIN Members m ON r.member_id = m.member_id " +
                    "WHERE r.book_id = " + returnedBookId + " AND r.status = 'Pending' ORDER BY r.reservation_id ASC LIMIT 1;");
                if (nextHold.Rows.Count > 0)
                {
                    long resId = Convert.ToInt64(nextHold.Rows[0]["reservation_id"]);
                    long waitMemId = Convert.ToInt64(nextHold.Rows[0]["member_id"]);
                    string waitName = Convert.ToString(nextHold.Rows[0]["name"]);

                    DialogResult dr = MessageBox.Show(
                        string.Format("🔔 Hold Queue Alert!\n\n{0} is #1 in the waitlist for \"{1}\".\nFulfill Hold #{2} and issue the book to {0} right now?",
                            waitName, bookTitle, resId),
                        "Pending Reservation Found",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (dr == DialogResult.Yes)
                    {
                        LibraryBootstrap.IssueBook(_db, waitMemId, returnedBookId);
                        _db.ExecuteNonQuery("UPDATE Reservations SET status = 'Fulfilled' WHERE reservation_id = " + resId + ";");
                        RefreshAll();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Return Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnRenewSelectedClick(object sender, EventArgs e)
        {
            if (_gridCircActive.SelectedRows.Count == 0)
            {
                MessageBox.Show("Select an active borrowing row first.", "Renew Loan", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            long borrowId = Convert.ToInt64(_gridCircActive.SelectedRows[0].Cells["borrow_id"].Value);
            try
            {
                LibraryBootstrap.RenewBook(_db, borrowId);
                RefreshAll();
                MessageBox.Show("Loan #" + borrowId + " extended by +7 days!", "Loan Renewed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Renewal Blocked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnAddBookClick(object sender, EventArgs e)
        {
            ComboItem cat = _cmbNewBookCat.SelectedItem as ComboItem;
            if (cat == null || string.IsNullOrWhiteSpace(_txtNewTitle.Text) || string.IsNullOrWhiteSpace(_txtNewIsbn.Text))
            {
                MessageBox.Show("Please enter ISBN, Title, Author, and Category.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                long nextId = Convert.ToInt64(_db.ExecuteScalar("SELECT COALESCE(MAX(book_id), 100) + 1 FROM Books;"));
                int qty = (int)_numNewCopies.Value;
                string sql = string.Format(CultureInfo.InvariantCulture,
                    "INSERT INTO Books (book_id, isbn, title, author, category_id, total_copies, available_copies) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {5});",
                    nextId,
                    SqliteEngine.SqlQuote(_txtNewIsbn.Text.Trim()),
                    SqliteEngine.SqlQuote(_txtNewTitle.Text.Trim()),
                    SqliteEngine.SqlQuote(_txtNewAuthor.Text.Trim()),
                    cat.Id, qty);
                _db.ExecuteNonQuery(sql);
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Could Not Add Book", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnRestockBookClick(object sender, EventArgs e)
        {
            if (_gridBooks.SelectedRows.Count == 0) return;
            long bookId = Convert.ToInt64(_gridBooks.SelectedRows[0].Cells["book_id"].Value);
            _db.ExecuteNonQuery("UPDATE Books SET total_copies = total_copies + 1, available_copies = available_copies + 1 WHERE book_id = " + bookId + ";");
            RefreshAll();
        }

        private void OnDeleteBookClick(object sender, EventArgs e)
        {
            if (_gridBooks.SelectedRows.Count == 0) return;
            long bookId = Convert.ToInt64(_gridBooks.SelectedRows[0].Cells["book_id"].Value);
            try
            {
                _db.ExecuteNonQuery("DELETE FROM Books WHERE book_id = " + bookId + ";");
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Foreign Key Constraint prevented deletion (book has borrowing/reservation history):\n\n" + ex.Message,
                    "Referential Integrity Protected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnRegisterMemberClick(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtMemName.Text) || string.IsNullOrWhiteSpace(_txtMemEmail.Text)) return;
            string tier = Convert.ToString(_cmbMemTier.SelectedItem);
            int maxB, days, rate;
            LibraryBootstrap.TierRules(tier, out maxB, out days, out rate);

            try
            {
                string simDate = LibraryBootstrap.GetSimDate(_db);
                string sql = string.Format(CultureInfo.InvariantCulture,
                    "INSERT INTO Members (name, email, phone, membership_tier, max_books_allowed, joined_date, status) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, 'Active');",
                    SqliteEngine.SqlQuote(_txtMemName.Text.Trim()),
                    SqliteEngine.SqlQuote(_txtMemEmail.Text.Trim()),
                    SqliteEngine.SqlQuote(_txtMemPhone.Text.Trim()),
                    SqliteEngine.SqlQuote(tier),
                    maxB,
                    SqliteEngine.SqlQuote(simDate));
                _db.ExecuteNonQuery(sql);
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Could Not Register Member", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnToggleMemberStatusClick(object sender, EventArgs e)
        {
            if (_gridMembers.SelectedRows.Count == 0) return;
            long memId = Convert.ToInt64(_gridMembers.SelectedRows[0].Cells["member_id"].Value);
            string cur = Convert.ToString(_gridMembers.SelectedRows[0].Cells["status"].Value);
            string next = (cur == "Active") ? "Suspended" : "Active";
            _db.ExecuteNonQuery("UPDATE Members SET status = " + SqliteEngine.SqlQuote(next) + " WHERE member_id = " + memId + ";");
            RefreshAll();
        }

        private void OnPlaceHoldClick(object sender, EventArgs e)
        {
            ComboItem mem = _cmbHoldMember.SelectedItem as ComboItem;
            ComboItem bk = _cmbHoldBook.SelectedItem as ComboItem;
            if (mem == null || bk == null) return;
            PlaceReservationInternal(mem.Id, bk.Id);
        }

        private void PlaceReservationInternal(long memberId, long bookId)
        {
            long dup = Convert.ToInt64(_db.ExecuteScalar(
                "SELECT COUNT(*) FROM Reservations WHERE member_id = " + memberId + " AND book_id = " + bookId + " AND status = 'Pending';"));
            if (dup > 0)
            {
                MessageBox.Show("This member already has a Pending reservation for this book.", "Duplicate Hold", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string simDate = LibraryBootstrap.GetSimDate(_db);
            _db.ExecuteNonQuery(string.Format(CultureInfo.InvariantCulture,
                "INSERT INTO Reservations (member_id, book_id, reserved_date, status) VALUES ({0}, {1}, {2}, 'Pending');",
                memberId, bookId, SqliteEngine.SqlQuote(simDate)));
            RefreshAll();
            MessageBox.Show("Reservation added to FIFO Hold Queue!", "Hold Placed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnFulfillHoldClick(object sender, EventArgs e)
        {
            if (_gridHolds.SelectedRows.Count == 0) return;
            long resId = Convert.ToInt64(_gridHolds.SelectedRows[0].Cells["reservation_id"].Value);
            string status = Convert.ToString(_gridHolds.SelectedRows[0].Cells["status"].Value);
            if (status != "Pending")
            {
                MessageBox.Show("Only Pending holds can be fulfilled.", "Hold Queue", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DataTable dt = _db.Query("SELECT member_id, book_id FROM Reservations WHERE reservation_id = " + resId + ";");
            if (dt.Rows.Count == 0) return;
            long memId = Convert.ToInt64(dt.Rows[0]["member_id"]);
            long bookId = Convert.ToInt64(dt.Rows[0]["book_id"]);

            try
            {
                LibraryBootstrap.IssueBook(_db, memId, bookId);
                _db.ExecuteNonQuery("UPDATE Reservations SET status = 'Fulfilled' WHERE reservation_id = " + resId + ";");
                RefreshAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Cannot Fulfill Hold Yet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnCancelHoldClick(object sender, EventArgs e)
        {
            if (_gridHolds.SelectedRows.Count == 0) return;
            long resId = Convert.ToInt64(_gridHolds.SelectedRows[0].Cells["reservation_id"].Value);
            _db.ExecuteNonQuery("UPDATE Reservations SET status = 'Cancelled' WHERE reservation_id = " + resId + ";");
            RefreshAll();
        }

        private void OnCollectFineClick(object sender, EventArgs e)
        {
            ComboItem item = _cmbUnpaidBorrow.SelectedItem as ComboItem;
            if (item == null)
            {
                MessageBox.Show("No unpaid closed fines to collect right now! Return an overdue book first.", "Fine Cashier", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            double amt = (double)_numPayAmount.Value;
            long borrowId = item.Id;
            long memId = Convert.ToInt64(_db.ExecuteScalar("SELECT member_id FROM Borrowings WHERE borrow_id = " + borrowId + ";"));
            string method = Convert.ToString(_cmbPayMethod.SelectedItem);
            string simDate = LibraryBootstrap.GetSimDate(_db);

            _db.ExecuteNonQuery(string.Format(CultureInfo.InvariantCulture,
                "INSERT INTO FinePayments (borrow_id, member_id, amount_paid, payment_date, payment_method) VALUES ({0}, {1}, {2:F2}, {3}, {4});",
                borrowId, memId, amt, SqliteEngine.SqlQuote(simDate), SqliteEngine.SqlQuote(method)));
            RefreshAll();
        }

        private void OnSimulateRushHour(object sender, EventArgs e)
        {
            string summary = LibraryBootstrap.SimulateRushHour(_db, _rng);
            RefreshAll();
            MessageBox.Show(summary, "🎲 Rush Hour Simulation Summary", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnExportMySql(object sender, EventArgs e)
        {
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "Export Live State to MySQL Script";
                sfd.Filter = "SQL Script (*.sql)|*.sql";
                sfd.FileName = "library_live_export.sql";
                sfd.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(sfd.FileName, LibraryBootstrap.ExportMySqlDump(_db), Encoding.UTF8);
                    MessageBox.Show("Exported MySQL data snapshot to:\n" + sfd.FileName, "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void OnResetDatabase(object sender, EventArgs e)
        {
            if (MessageBox.Show("Reset database back to initial demo state (2026-11-25)?", "Confirm Reset",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                LibraryBootstrap.ResetAndSeed(_db);
                RefreshAll();
            }
        }

        // =====================================================================
        // MASTER UI REFRESH
        // =====================================================================
        private void RefreshAll()
        {
            string simDate = LibraryBootstrap.GetSimDate(_db);
            _lblSimDate.Text = "📅 Sim Date: " + simDate;

            // 1. KPIs
            long availCopies = Convert.ToInt64(_db.ExecuteScalar("SELECT COALESCE(SUM(available_copies), 0) FROM Books;"));
            long totalCopies = Convert.ToInt64(_db.ExecuteScalar("SELECT COALESCE(SUM(total_copies), 0) FROM Books;"));
            long titleCount = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM Books;"));
            _kpiBooksVal.Text = availCopies + " / " + totalCopies;
            _kpiBooksSub.Text = titleCount + " distinct book titles";

            long activeLoans = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM vw_ActiveBorrowings;"));
            long totalBorrows = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM Borrowings;"));
            _kpiLoansVal.Text = activeLoans.ToString();
            _kpiLoansSub.Text = totalBorrows + " all-time borrows";

            long overdueCnt = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM vw_ActiveBorrowings WHERE days_overdue > 0;"));
            double liveOverdueRs = Convert.ToDouble(_db.ExecuteScalar("SELECT COALESCE(SUM(live_fine_rs), 0) FROM vw_ActiveBorrowings;"));
            _kpiOverdueVal.Text = overdueCnt.ToString();
            _kpiOverdueSub.Text = "₹" + liveOverdueRs.ToString("F2") + " accruing fine";

            long pendingHolds = Convert.ToInt64(_db.ExecuteScalar("SELECT COUNT(*) FROM Reservations WHERE status = 'Pending';"));
            _kpiHoldsVal.Text = pendingHolds.ToString();
            _kpiHoldsSub.Text = "FIFO waitlist active";

            double netDue = Convert.ToDouble(_db.ExecuteScalar("SELECT COALESCE(SUM(net_due_rs), 0) FROM vw_MemberLedger;"));
            double totalPaid = Convert.ToDouble(_db.ExecuteScalar("SELECT COALESCE(SUM(amount_paid), 0) FROM FinePayments;"));
            _kpiFinesVal.Text = "₹" + Math.Max(0, netDue).ToString("F2");
            _kpiFinesSub.Text = "₹" + totalPaid.ToString("F2") + " collected so far";

            // 2. Grids
            DataTable dtActive = _db.Query("SELECT * FROM vw_ActiveBorrowings ORDER BY days_overdue DESC, borrow_id ASC;");
            _gridDashActive.DataSource = dtActive;
            _gridCircActive.DataSource = dtActive.Copy();

            _gridDashLog.DataSource = _db.Query("SELECT log_id, event_type, description, event_time FROM ActivityLog ORDER BY log_id DESC LIMIT 50;");
            _gridMembers.DataSource = _db.Query("SELECT * FROM vw_MemberLedger ORDER BY member_id ASC;");

            _gridHolds.DataSource = _db.Query(
                "SELECT r.reservation_id, m.name AS member, m.membership_tier AS tier, bk.title AS book, " +
                "bk.available_copies AS stock, r.reserved_date, r.status " +
                "FROM Reservations r JOIN Members m ON r.member_id = m.member_id JOIN Books bk ON r.book_id = bk.book_id " +
                "ORDER BY CASE r.status WHEN 'Pending' THEN 0 ELSE 1 END, r.reservation_id ASC;");

            _gridPayments.DataSource = _db.Query(
                "SELECT fp.payment_id, m.name AS member, bk.title AS book, fp.amount_paid, fp.payment_method, fp.payment_date " +
                "FROM FinePayments fp JOIN Borrowings b ON fp.borrow_id = b.borrow_id " +
                "JOIN Members m ON fp.member_id = m.member_id JOIN Books bk ON b.book_id = bk.book_id " +
                "ORDER BY fp.payment_id DESC;");

            PopulateCombos();
            RefreshBooksGrid();
        }

        private void PopulateCombos()
        {
            // Categories
            DataTable cats = _db.Query("SELECT category_id, category_name, shelf_code FROM Categories ORDER BY category_id;");
            int prevFilterIdx = _cmbBookCatFilter.SelectedIndex;
            _cmbBookCatFilter.Items.Clear();
            _cmbBookCatFilter.Items.Add(new ComboItem { Id = 0, Label = "All Categories" });
            _cmbNewBookCat.Items.Clear();
            foreach (DataRow r in cats.Rows)
            {
                long id = Convert.ToInt64(r["category_id"]);
                string name = Convert.ToString(r["category_name"]);
                _cmbBookCatFilter.Items.Add(new ComboItem { Id = id, Label = name });
                _cmbNewBookCat.Items.Add(new ComboItem { Id = id, Label = name + " [" + r["shelf_code"] + "]" });
            }
            _cmbBookCatFilter.SelectedIndex = (prevFilterIdx >= 0 && prevFilterIdx < _cmbBookCatFilter.Items.Count) ? prevFilterIdx : 0;
            if (_cmbNewBookCat.Items.Count > 0 && _cmbNewBookCat.SelectedIndex < 0) _cmbNewBookCat.SelectedIndex = 0;

            // Members
            DataTable mems = _db.Query("SELECT member_id, name, tier, status, active_loans, max_limit FROM vw_MemberLedger ORDER BY member_id;");
            _cmbIssueMember.Items.Clear();
            _cmbHoldMember.Items.Clear();
            foreach (DataRow r in mems.Rows)
            {
                long id = Convert.ToInt64(r["member_id"]);
                string tier = Convert.ToString(r["tier"]);
                string status = Convert.ToString(r["status"]);
                string label = string.Format("#{0} {1} [{2} • {3}/{4} loans{5}]",
                    id, r["name"], tier, r["active_loans"], r["max_limit"], status == "Suspended" ? " • SUSPENDED" : "");
                _cmbIssueMember.Items.Add(new ComboItem { Id = id, Label = label, Extra = tier });
                _cmbHoldMember.Items.Add(new ComboItem { Id = id, Label = "#" + id + " " + r["name"] + " (" + tier + ")" });
            }
            if (_cmbIssueMember.Items.Count > 0) _cmbIssueMember.SelectedIndex = 0;
            if (_cmbHoldMember.Items.Count > 0) _cmbHoldMember.SelectedIndex = 0;

            // Books
            DataTable bks = _db.Query("SELECT book_id, title, available_copies, total_copies FROM Books ORDER BY book_id;");
            _cmbIssueBook.Items.Clear();
            _cmbHoldBook.Items.Clear();
            foreach (DataRow r in bks.Rows)
            {
                long id = Convert.ToInt64(r["book_id"]);
                long av = Convert.ToInt64(r["available_copies"]);
                string label = string.Format("#{0} {1} (Avail: {2}/{3})", id, r["title"], av, r["total_copies"]);
                _cmbIssueBook.Items.Add(new ComboItem { Id = id, Label = label });
                _cmbHoldBook.Items.Add(new ComboItem { Id = id, Label = label });
            }
            if (_cmbIssueBook.Items.Count > 0) _cmbIssueBook.SelectedIndex = 0;
            if (_cmbHoldBook.Items.Count > 0) _cmbHoldBook.SelectedIndex = 0;

            // Unpaid Closed Fines
            DataTable unpaid = _db.Query(
                "SELECT b.borrow_id, m.name, bk.title, " +
                "ROUND(b.fine_assessed - COALESCE((SELECT SUM(fp.amount_paid) FROM FinePayments fp WHERE fp.borrow_id = b.borrow_id), 0), 2) AS balance " +
                "FROM Borrowings b JOIN Members m ON b.member_id = m.member_id JOIN Books bk ON b.book_id = bk.book_id " +
                "WHERE b.fine_assessed > COALESCE((SELECT SUM(fp.amount_paid) FROM FinePayments fp WHERE fp.borrow_id = b.borrow_id), 0) " +
                "ORDER BY b.borrow_id ASC;");
            _cmbUnpaidBorrow.Items.Clear();
            foreach (DataRow r in unpaid.Rows)
            {
                long bid = Convert.ToInt64(r["borrow_id"]);
                double bal = Convert.ToDouble(r["balance"]);
                _cmbUnpaidBorrow.Items.Add(new ComboItem
                {
                    Id = bid,
                    Amount = bal,
                    Label = string.Format("Borrow #{0} • {1} • \"{2}\" • Unpaid: ₹{3:F2}", bid, r["name"], r["title"], bal)
                });
            }
            if (_cmbUnpaidBorrow.Items.Count > 0) _cmbUnpaidBorrow.SelectedIndex = 0;
        }

        private void RefreshBooksGrid()
        {
            if (_gridBooks == null) return;
            List<string> where = new List<string>();
            if (_txtBookSearch != null && !string.IsNullOrWhiteSpace(_txtBookSearch.Text))
            {
                string q = "%" + _txtBookSearch.Text.Trim().Replace("'", "''") + "%";
                where.Add(string.Format("(title LIKE '{0}' OR author LIKE '{0}' OR isbn LIKE '{0}')", q));
            }
            ComboItem cat = _cmbBookCatFilter != null ? _cmbBookCatFilter.SelectedItem as ComboItem : null;
            if (cat != null && cat.Id > 0)
            {
                where.Add("category = (SELECT category_name FROM Categories WHERE category_id = " + cat.Id + ")");
            }
            if (_chkAvailOnly != null && _chkAvailOnly.Checked)
            {
                where.Add("available > 0");
            }

            string sql = "SELECT * FROM vw_BookPopularity"
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                + " ORDER BY book_id ASC;";
            _gridBooks.DataSource = _db.Query(sql);
        }

        // =====================================================================
        // SHARED UI FACTORY HELPERS
        // =====================================================================
        private static void FormatActiveBorrowingsGrid(object sender, DataGridViewCellFormattingEventArgs e)
        {
            DataGridView g = sender as DataGridView;
            if (g == null || e.RowIndex < 0 || g.Columns["loan_status"] == null) return;
            string status = Convert.ToString(g.Rows[e.RowIndex].Cells["loan_status"].Value);
            if (status == "OVERDUE")
                g.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(254, 226, 226);
            else if (status == "DUE SOON")
                g.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(254, 249, 195);
        }

        private static DataGridView CreateStyledGrid()
        {
            DataGridView g = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(226, 232, 240),
                EnableHeadersVisualStyles = false
            };
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(51, 65, 85);
            g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9.2f);
            g.ColumnHeadersHeight = 36;
            g.DefaultCellStyle.Font = new Font("Segoe UI", 9.2f);
            g.DefaultCellStyle.ForeColor = Color.FromArgb(15, 23, 42);
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
            g.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 58, 138);
            g.RowTemplate.Height = 31;
            return g;
        }

        private static Panel WrapGridCard(string title, DataGridView grid)
        {
            Panel card = new Panel { Dock = DockStyle.Fill, BackColor = ColCard, Padding = new Padding(14, 10, 14, 12) };
            Label lbl = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Segoe UI Semibold", 10f),
                ForeColor = ColText
            };
            card.Controls.Add(grid);
            card.Controls.Add(lbl);
            return card;
        }

        private static Button CreateFlatButton(string text, Color bg, Color fg, int width, int height)
        {
            Button b = new Button
            {
                Text = text,
                Size = new Size(width, height),
                BackColor = bg,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9f),
                Cursor = Cursors.Hand,
                Margin = new Padding(4, 3, 4, 3)
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private static Label MakeFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(2, 8, 4, 0),
                Font = new Font("Segoe UI Semibold", 9f),
                ForeColor = ColMuted
            };
        }
    }

    // =========================================================================
    // 5. ENTRY POINT + AUTOMATED SELF-TEST SUITE (--selftest)
    // =========================================================================
    public static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--selftest")
                return RunSelfTest();

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "library_data.db");
            using (SqliteEngine db = new SqliteEngine(dbPath))
            {
                LibraryBootstrap.EnsureInitialized(db);
                Application.Run(new MainForm(db));
            }
            return 0;
        }

        private static int RunSelfTest()
        {
            string testDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_temp.db");
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest_result.txt");
            StringBuilder report = new StringBuilder();

            try
            {
                if (File.Exists(testDbPath)) File.Delete(testDbPath);
                using (SqliteEngine db = new SqliteEngine(testDbPath))
                {
                    LibraryBootstrap.ResetAndSeed(db);
                    report.AppendLine("[PASS] Schema, 5 triggers, 3 views, and sample data initialized.");

                    // Verify stock trigger decremented Clean Code (#105) to 0 available
                    long ccAvail = Convert.ToInt64(db.ExecuteScalar("SELECT available_copies FROM Books WHERE book_id = 105;"));
                    if (ccAvail != 0) throw new Exception("Expected Clean Code (#105) available_copies = 0, got " + ccAvail);
                    report.AppendLine("[PASS] Trigger trg_after_borrow_insert accurately decremented stock.");

                    // Verify out-of-stock trigger blocks borrowing #105
                    bool blockedOutOfStock = false;
                    try { LibraryBootstrap.IssueBook(db, 1, 105); }
                    catch (Exception ex) { blockedOutOfStock = ex.Message.Contains("0 available copies"); }
                    if (!blockedOutOfStock) throw new Exception("Expected out-of-stock trigger to block borrowing #105.");
                    report.AppendLine("[PASS] Trigger trg_before_borrow_insert blocked out-of-stock issue.");

                    // Verify suspended member (#7 Rohan) is blocked by trigger
                    bool blockedSuspended = false;
                    try { LibraryBootstrap.IssueBook(db, 7, 103); }
                    catch (Exception ex) { blockedSuspended = ex.Message.Contains("Suspended"); }
                    if (!blockedSuspended) throw new Exception("Expected suspended member trigger block.");
                    report.AppendLine("[PASS] Trigger trg_before_borrow_insert blocked suspended member.");

                    // Verify renewal blocked when pending reservation exists (#6 -> book 105)
                    bool blockedRenewHold = false;
                    try { LibraryBootstrap.RenewBook(db, 6); }
                    catch (Exception ex) { blockedRenewHold = ex.Message.Contains("Pending Reservation"); }
                    if (!blockedRenewHold) throw new Exception("Expected hold queue to block renewal on borrow #6.");
                    report.AppendLine("[PASS] Reservation queue blocked renewal on held book.");

                    // Advance clock +5 days -> verify simulated date & overdue calculation
                    LibraryBootstrap.AdvanceSimDays(db, 5);
                    string newSim = LibraryBootstrap.GetSimDate(db);
                    if (newSim != "2026-11-30") throw new Exception("Expected sim_date 2026-11-30, got " + newSim);

                    // Return overdue Premium loan #4 (due 2026-11-17, returned 2026-11-30 => 13 days * Rs.3 = Rs.39)
                    long retBook;
                    double fine = LibraryBootstrap.ReturnBook(db, 4, out retBook);
                    if (Math.Abs(fine - 39.0) > 0.01) throw new Exception("Expected Rs.39.00 Premium fine, got " + fine);
                    report.AppendLine("[PASS] Time-travel + tier-based fine calculation verified (Rs. 39.00).");

                    // Rush hour simulation + MySQL export check
                    string rush = LibraryBootstrap.SimulateRushHour(db, new Random(42));
                    if (string.IsNullOrEmpty(rush)) throw new Exception("Rush hour returned empty summary.");
                    string dump = LibraryBootstrap.ExportMySqlDump(db);
                    if (!dump.Contains("INSERT INTO Borrowings")) throw new Exception("MySQL export missing Borrowings dump.");
                    report.AppendLine("[PASS] Rush-hour simulator and MySQL .sql exporter verified.");

                    // Full UI Form construction & data-binding verification
                    using (MainForm form = new MainForm(db))
                    {
                        IntPtr handle = form.Handle; // Forces Win32 control & DataGridView creation
                        if (handle == IntPtr.Zero) throw new Exception("Failed to create MainForm window handle.");
                    }
                    report.AppendLine("[PASS] MainForm GUI widget tree and DataGridView bindings verified.");
                }
                if (File.Exists(testDbPath)) File.Delete(testDbPath);
                report.AppendLine("ALL SELF-TESTS PASSED.");
                File.WriteAllText(logPath, report.ToString(), Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                report.AppendLine("[FAIL] " + ex);
                File.WriteAllText(logPath, report.ToString(), Encoding.UTF8);
                return 1;
            }
        }
    }
}
