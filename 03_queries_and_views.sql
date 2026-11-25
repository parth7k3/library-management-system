-- ============================================================
-- Library Management System (Enhanced Edition)
-- File 3: Views, Stored Procedures & Analytical Queries
-- ============================================================

USE LibraryDB;

-- ============================================================
-- PART A: ANALYTICAL VIEWS
-- ============================================================

-- 1. Active Borrowings View with Tier-Based Live Fine Calculation
--    Rates: Student = Rs. 5/day | Premium = Rs. 3/day | Faculty = Rs. 2/day
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

-- 2. Member Financial & Circulation Ledger View
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

-- 3. Book Popularity & Reservation Queue View
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
-- PART B: STORED PROCEDURES (Business Logic)
-- ============================================================

DELIMITER $$

-- Procedure 1: Issue a book with automatic tier-based loan duration
DROP PROCEDURE IF EXISTS sp_IssueBook$$
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

-- Procedure 2: Return a book, assess tier-based fine, and check reservations
DROP PROCEDURE IF EXISTS sp_ReturnBook$$
CREATE PROCEDURE sp_ReturnBook(
    IN p_borrow_id INT,
    IN p_return_date DATE
)
BEGIN
    DECLARE v_due DATE;
    DECLARE v_member_id INT;
    DECLARE v_book_id INT;
    DECLARE v_tier VARCHAR(20);
    DECLARE v_rate INT;
    DECLARE v_late_days INT;
    DECLARE v_fine DECIMAL(8, 2);

    SELECT b.due_date, b.member_id, b.book_id, m.membership_tier
    INTO v_due, v_member_id, v_book_id, v_tier
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
    SET v_fine = v_late_days * v_rate;

    UPDATE Borrowings
    SET return_date = p_return_date,
        fine_assessed = v_fine
    WHERE borrow_id = p_borrow_id;
END$$

-- Procedure 3: Renew an active loan (+7 days) if no hold exists
DROP PROCEDURE IF EXISTS sp_RenewBook$$
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
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Cannot renew: Loan not found or already returned.';
    END IF;

    IF v_renews >= 2 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Cannot renew: Maximum of 2 renewals reached.';
    END IF;

    SELECT COUNT(*) INTO v_holds
    FROM Reservations
    WHERE book_id = v_book_id AND status = 'Pending';

    IF v_holds > 0 THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Cannot renew: Another member has a pending reservation for this book.';
    END IF;

    UPDATE Borrowings
    SET due_date = DATE_ADD(due_date, INTERVAL 7 DAY),
        renew_count = renew_count + 1
    WHERE borrow_id = p_borrow_id;
END$$

DELIMITER ;

-- ============================================================
-- PART C: CORE & ADVANCED VIVA QUERIES
-- ============================================================

-- Q1: Full catalog with shelf location and stock status
SELECT * FROM vw_BookPopularity ORDER BY times_borrowed DESC;

-- Q2: Currently borrowed books with live overdue days & tier-based fines
SELECT * FROM vw_ActiveBorrowings ORDER BY days_overdue DESC;

-- Q3: Member financial ledger (active loans, closed fines, live fines, paid, net due)
SELECT * FROM vw_MemberLedger ORDER BY net_due_rs DESC;

-- Q4: Books that have NEVER been borrowed (Subquery)
SELECT book_id, isbn, title, author, available_copies
FROM Books
WHERE book_id NOT IN (SELECT DISTINCT book_id FROM Borrowings);

-- Q5: Category-wise circulation analytics (JOIN + GROUP BY)
SELECT
    c.category_name,
    COUNT(DISTINCT bk.book_id) AS titles_in_category,
    COUNT(b.borrow_id) AS total_borrows
FROM Categories c
LEFT JOIN Books bk ON c.category_id = bk.category_id
LEFT JOIN Borrowings b ON bk.book_id = b.book_id
GROUP BY c.category_id, c.category_name
ORDER BY total_borrows DESC;

-- Q6: Pending reservation waitlist ordered by FIFO priority
SELECT
    r.reservation_id,
    bk.title AS book_title,
    m.name AS waiting_member,
    m.membership_tier,
    r.reserved_date
FROM Reservations r
JOIN Books bk ON r.book_id = bk.book_id
JOIN Members m ON r.member_id = m.member_id
WHERE r.status = 'Pending'
ORDER BY r.book_id, r.reserved_date ASC;

-- Q7: Automated Trigger Audit Trail
SELECT * FROM ActivityLog ORDER BY log_id DESC;
