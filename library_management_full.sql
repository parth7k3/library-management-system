-- ============================================================
-- Library Management System (Enhanced Edition) - Master Script
-- Includes: 7 Tables, CHECK Constraints, 3 Triggers,
--           3 Stored Procedures, 3 Views, Sample Data & Reports
-- Target: MySQL 8.0+ / MySQL Workbench
-- ============================================================

DROP DATABASE IF EXISTS LibraryDB;
CREATE DATABASE LibraryDB;
USE LibraryDB;

-- ============================================================
-- 1. TABLES & CONSTRAINTS (DDL)
-- ============================================================

CREATE TABLE Categories (
    category_id INT AUTO_INCREMENT PRIMARY KEY,
    category_name VARCHAR(50) NOT NULL UNIQUE,
    shelf_code VARCHAR(10) NOT NULL
);

CREATE TABLE Members (
    member_id INT AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(60) NOT NULL,
    email VARCHAR(100) NOT NULL UNIQUE,
    phone VARCHAR(15) NOT NULL,
    membership_tier ENUM('Student', 'Premium', 'Faculty') NOT NULL DEFAULT 'Student',
    max_books_allowed INT NOT NULL DEFAULT 3,
    joined_date DATE NOT NULL,
    status ENUM('Active', 'Suspended') NOT NULL DEFAULT 'Active',
    CONSTRAINT chk_max_books CHECK (max_books_allowed BETWEEN 1 AND 15)
);

CREATE TABLE Books (
    book_id INT PRIMARY KEY,
    isbn VARCHAR(20) NOT NULL UNIQUE,
    title VARCHAR(120) NOT NULL,
    author VARCHAR(80) NOT NULL,
    category_id INT NOT NULL,
    total_copies INT NOT NULL DEFAULT 1,
    available_copies INT NOT NULL DEFAULT 1,
    FOREIGN KEY (category_id) REFERENCES Categories(category_id),
    CONSTRAINT chk_copies CHECK (total_copies >= 0 AND available_copies >= 0 AND available_copies <= total_copies)
);

CREATE TABLE Borrowings (
    borrow_id INT AUTO_INCREMENT PRIMARY KEY,
    member_id INT NOT NULL,
    book_id INT NOT NULL,
    issue_date DATE NOT NULL,
    due_date DATE NOT NULL,
    return_date DATE DEFAULT NULL,
    renew_count INT NOT NULL DEFAULT 0,
    fine_assessed DECIMAL(8, 2) NOT NULL DEFAULT 0.00,
    FOREIGN KEY (member_id) REFERENCES Members(member_id),
    FOREIGN KEY (book_id) REFERENCES Books(book_id),
    CONSTRAINT chk_renewals CHECK (renew_count BETWEEN 0 AND 2),
    CONSTRAINT chk_dates CHECK (due_date >= issue_date)
);

CREATE TABLE Reservations (
    reservation_id INT AUTO_INCREMENT PRIMARY KEY,
    member_id INT NOT NULL,
    book_id INT NOT NULL,
    reserved_date DATE NOT NULL,
    status ENUM('Pending', 'Fulfilled', 'Cancelled') NOT NULL DEFAULT 'Pending',
    FOREIGN KEY (member_id) REFERENCES Members(member_id),
    FOREIGN KEY (book_id) REFERENCES Books(book_id)
);

CREATE TABLE FinePayments (
    payment_id INT AUTO_INCREMENT PRIMARY KEY,
    borrow_id INT NOT NULL,
    member_id INT NOT NULL,
    amount_paid DECIMAL(8, 2) NOT NULL,
    payment_date DATE NOT NULL,
    payment_method ENUM('UPI', 'Cash', 'Card') NOT NULL DEFAULT 'UPI',
    FOREIGN KEY (borrow_id) REFERENCES Borrowings(borrow_id),
    FOREIGN KEY (member_id) REFERENCES Members(member_id),
    CONSTRAINT chk_payment_positive CHECK (amount_paid > 0)
);

CREATE TABLE ActivityLog (
    log_id INT AUTO_INCREMENT PRIMARY KEY,
    event_type VARCHAR(30) NOT NULL,
    description VARCHAR(255) NOT NULL,
    event_time DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================
-- 2. TRIGGERS (Stock Control, Validation & Audit Trail)
-- ============================================================

DELIMITER $$

CREATE TRIGGER trg_before_borrow_insert
BEFORE INSERT ON Borrowings
FOR EACH ROW
BEGIN
    DECLARE v_avail INT;
    DECLARE v_status VARCHAR(20);
    DECLARE v_max INT;
    DECLARE v_active_loans INT;

    SELECT status, max_books_allowed INTO v_status, v_max
    FROM Members WHERE member_id = NEW.member_id;

    IF v_status <> 'Active' THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Cannot issue book: Member account is Suspended.';
    END IF;

    IF NEW.return_date IS NULL THEN
        SELECT COUNT(*) INTO v_active_loans
        FROM Borrowings
        WHERE member_id = NEW.member_id AND return_date IS NULL;

        IF v_active_loans >= v_max THEN
            SIGNAL SQLSTATE '45000'
                SET MESSAGE_TEXT = 'Cannot issue book: Member has reached their tier borrowing limit.';
        END IF;

        SELECT available_copies INTO v_avail
        FROM Books WHERE book_id = NEW.book_id;

        IF v_avail <= 0 THEN
            SIGNAL SQLSTATE '45000'
                SET MESSAGE_TEXT = 'Cannot issue book: No available copies in stock (place a Reservation instead).';
        END IF;
    END IF;
END$$

CREATE TRIGGER trg_after_borrow_insert
AFTER INSERT ON Borrowings
FOR EACH ROW
BEGIN
    IF NEW.return_date IS NULL THEN
        UPDATE Books
        SET available_copies = available_copies - 1
        WHERE book_id = NEW.book_id;
    END IF;

    INSERT INTO ActivityLog (event_type, description)
    VALUES ('BOOK_ISSUED', CONCAT('Borrow #', NEW.borrow_id, ': Member #', NEW.member_id, ' borrowed Book #', NEW.book_id));
END$$

CREATE TRIGGER trg_after_borrow_return
AFTER UPDATE ON Borrowings
FOR EACH ROW
BEGIN
    IF OLD.return_date IS NULL AND NEW.return_date IS NOT NULL THEN
        UPDATE Books
        SET available_copies = available_copies + 1
        WHERE book_id = NEW.book_id;

        INSERT INTO ActivityLog (event_type, description)
        VALUES ('BOOK_RETURNED', CONCAT('Borrow #', NEW.borrow_id, ': Book #', NEW.book_id, ' returned on ', NEW.return_date, ' (Fine: Rs. ', NEW.fine_assessed, ')'));
    ELSEIF NEW.renew_count > OLD.renew_count THEN
        INSERT INTO ActivityLog (event_type, description)
        VALUES ('BOOK_RENEWED', CONCAT('Borrow #', NEW.borrow_id, ': Renewed to ', NEW.due_date, ' (Renewal #', NEW.renew_count, ')'));
    END IF;
END$$

-- ============================================================
-- 3. STORED PROCEDURES
-- ============================================================

CREATE PROCEDURE sp_IssueBook(
    IN p_member_id INT,
    IN p_book_id INT,
    IN p_issue_date DATE
)
BEGIN
    DECLARE v_tier VARCHAR(20);
    DECLARE v_days INT;

    SELECT membership_tier INTO v_tier
    FROM Members WHERE member_id = p_member_id;

    SET v_days = CASE v_tier
        WHEN 'Faculty' THEN 30
        WHEN 'Premium' THEN 15
        ELSE 10
    END;

    INSERT INTO Borrowings (member_id, book_id, issue_date, due_date)
    VALUES (p_member_id, p_book_id, p_issue_date, DATE_ADD(p_issue_date, INTERVAL v_days DAY));
END$$

CREATE PROCEDURE sp_ReturnBook(
    IN p_borrow_id INT,
    IN p_return_date DATE
)
BEGIN
    DECLARE v_due DATE;
    DECLARE v_tier VARCHAR(20);
    DECLARE v_rate INT;
    DECLARE v_late_days INT;

    SELECT b.due_date, m.membership_tier INTO v_due, v_tier
    FROM Borrowings b
    JOIN Members m ON b.member_id = m.member_id
    WHERE b.borrow_id = p_borrow_id AND b.return_date IS NULL;

    IF v_due IS NULL THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Borrowing record not found or already returned.';
    END IF;

    SET v_rate = CASE v_tier
        WHEN 'Faculty' THEN 2
        WHEN 'Premium' THEN 3
        ELSE 5
    END;

    SET v_late_days = GREATEST(DATEDIFF(p_return_date, v_due), 0);

    UPDATE Borrowings
    SET return_date = p_return_date,
        fine_assessed = v_late_days * v_rate
    WHERE borrow_id = p_borrow_id;
END$$

CREATE PROCEDURE sp_RenewBook(
    IN p_borrow_id INT
)
BEGIN
    DECLARE v_book_id INT;
    DECLARE v_renews INT;
    DECLARE v_holds INT;

    SELECT book_id, renew_count INTO v_book_id, v_renews
    FROM Borrowings
    WHERE borrow_id = p_borrow_id AND return_date IS NULL;

    IF v_book_id IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Loan not found or already returned.';
    END IF;

    IF v_renews >= 2 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Maximum of 2 renewals reached.';
    END IF;

    SELECT COUNT(*) INTO v_holds
    FROM Reservations
    WHERE book_id = v_book_id AND status = 'Pending';

    IF v_holds > 0 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Cannot renew: Another member has reserved this book.';
    END IF;

    UPDATE Borrowings
    SET due_date = DATE_ADD(due_date, INTERVAL 7 DAY),
        renew_count = renew_count + 1
    WHERE borrow_id = p_borrow_id;
END$$

DELIMITER ;

-- ============================================================
-- 4. VIEWS
-- ============================================================

CREATE OR REPLACE VIEW vw_ActiveBorrowings AS
SELECT
    b.borrow_id,
    m.member_id,
    m.name AS member_name,
    m.membership_tier,
    bk.book_id,
    bk.title AS book_title,
    b.issue_date,
    b.due_date,
    b.renew_count,
    DATEDIFF(b.due_date, CURDATE()) AS days_remaining,
    GREATEST(DATEDIFF(CURDATE(), b.due_date), 0) AS days_overdue,
    GREATEST(DATEDIFF(CURDATE(), b.due_date), 0) *
        CASE m.membership_tier
            WHEN 'Student' THEN 5
            WHEN 'Premium' THEN 3
            WHEN 'Faculty' THEN 2
        END AS live_fine_rs
FROM Borrowings b
JOIN Members m ON b.member_id = m.member_id
JOIN Books bk ON b.book_id = bk.book_id
WHERE b.return_date IS NULL;

CREATE OR REPLACE VIEW vw_MemberLedger AS
SELECT
    m.member_id,
    m.name,
    m.membership_tier,
    m.status,
    m.max_books_allowed,
    (SELECT COUNT(*) FROM Borrowings b WHERE b.member_id = m.member_id AND b.return_date IS NULL) AS active_loans,
    (SELECT COUNT(*) FROM Borrowings b WHERE b.member_id = m.member_id) AS total_borrows,
    COALESCE((SELECT SUM(b.fine_assessed) FROM Borrowings b WHERE b.member_id = m.member_id), 0.00) AS closed_fines_rs,
    COALESCE((SELECT SUM(av.live_fine_rs) FROM vw_ActiveBorrowings av WHERE av.member_id = m.member_id), 0.00) AS running_overdue_rs,
    COALESCE((SELECT SUM(fp.amount_paid) FROM FinePayments fp WHERE fp.member_id = m.member_id), 0.00) AS fines_paid_rs,
    COALESCE((SELECT SUM(b.fine_assessed) FROM Borrowings b WHERE b.member_id = m.member_id), 0.00)
      + COALESCE((SELECT SUM(av.live_fine_rs) FROM vw_ActiveBorrowings av WHERE av.member_id = m.member_id), 0.00)
      - COALESCE((SELECT SUM(fp.amount_paid) FROM FinePayments fp WHERE fp.member_id = m.member_id), 0.00) AS net_due_rs
FROM Members m;

CREATE OR REPLACE VIEW vw_BookPopularity AS
SELECT
    bk.book_id,
    bk.isbn,
    bk.title,
    bk.author,
    c.category_name,
    c.shelf_code,
    bk.available_copies,
    bk.total_copies,
    COUNT(DISTINCT b.borrow_id) AS times_borrowed,
    (SELECT COUNT(*) FROM Reservations r WHERE r.book_id = bk.book_id AND r.status = 'Pending') AS pending_reservations
FROM Books bk
JOIN Categories c ON bk.category_id = c.category_id
LEFT JOIN Borrowings b ON bk.book_id = b.book_id
GROUP BY bk.book_id, bk.isbn, bk.title, bk.author, c.category_name, c.shelf_code, bk.available_copies, bk.total_copies;

-- ============================================================
-- 5. SAMPLE DATA
-- ============================================================

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

-- ============================================================
-- 6. KEY REPORTS
-- ============================================================

SELECT * FROM vw_BookPopularity ORDER BY times_borrowed DESC;
SELECT * FROM vw_ActiveBorrowings ORDER BY days_overdue DESC;
SELECT * FROM vw_MemberLedger ORDER BY net_due_rs DESC;
SELECT * FROM ActivityLog ORDER BY log_id DESC;
