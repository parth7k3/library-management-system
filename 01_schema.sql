-- ============================================================
-- Library Management System (Enhanced Edition)
-- File 1: Database, 7 Tables, Constraints & Triggers (DDL)
-- Target: MySQL 8.0+ / MySQL Workbench
-- ============================================================

DROP DATABASE IF EXISTS LibraryDB;
CREATE DATABASE LibraryDB;
USE LibraryDB;

-- 1. Categories Table (with physical shelf code)
CREATE TABLE Categories (
    category_id INT AUTO_INCREMENT PRIMARY KEY,
    category_name VARCHAR(50) NOT NULL UNIQUE,
    shelf_code VARCHAR(10) NOT NULL
);

-- 2. Members Table (with Membership Tiers & Borrowing Limits)
-- Tiers:
--   Student : max 3 books, 10-day loan, Rs. 5/day fine
--   Premium : max 5 books, 15-day loan, Rs. 3/day fine
--   Faculty : max 8 books, 30-day loan, Rs. 2/day fine
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

-- 3. Books Table (with ISBN, total vs available stock & CHECK constraints)
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

-- 4. Borrowings Table (tracks loans, renewals & assessed late fines)
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

-- 5. Reservations Table (Hold queue for high-demand / out-of-stock books)
CREATE TABLE Reservations (
    reservation_id INT AUTO_INCREMENT PRIMARY KEY,
    member_id INT NOT NULL,
    book_id INT NOT NULL,
    reserved_date DATE NOT NULL,
    status ENUM('Pending', 'Fulfilled', 'Cancelled') NOT NULL DEFAULT 'Pending',
    FOREIGN KEY (member_id) REFERENCES Members(member_id),
    FOREIGN KEY (book_id) REFERENCES Books(book_id)
);

-- 6. FinePayments Table (Tracks Cash / UPI / Card fine collections)
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

-- 7. ActivityLog Table (Populated automatically by Triggers)
CREATE TABLE ActivityLog (
    log_id INT AUTO_INCREMENT PRIMARY KEY,
    event_type VARCHAR(30) NOT NULL,
    description VARCHAR(255) NOT NULL,
    event_time DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================
-- TRIGGERS (Automated Stock Control, Validation & Audit Trail)
-- ============================================================

DELIMITER $$

-- Validate stock, member status, and tier borrowing limit BEFORE issuing
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

    SELECT COUNT(*) INTO v_active_loans
    FROM Borrowings
    WHERE member_id = NEW.member_id AND return_date IS NULL;

    IF v_active_loans >= v_max THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Cannot issue book: Member has reached their tier borrowing limit.';
    END IF;

    -- Only decrement live stock if inserting an unreturned (active) loan
    IF NEW.return_date IS NULL THEN
        SELECT available_copies INTO v_avail
        FROM Books WHERE book_id = NEW.book_id;

        IF v_avail <= 0 THEN
            SIGNAL SQLSTATE '45000'
                SET MESSAGE_TEXT = 'Cannot issue book: No available copies in stock (place a Reservation instead).';
        END IF;
    END IF;
END$$

-- Auto-decrement available_copies and log issue AFTER inserting active loan
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

-- Auto-increment available_copies when a book is returned
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

DELIMITER ;
