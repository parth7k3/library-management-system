-- ============================================================
-- Library Management System (Enhanced Edition)
-- File 4: Live Presentation & Viva Walkthrough
-- Execute step-by-step in MySQL Workbench (Ctrl + Enter)
-- ============================================================

USE LibraryDB;

-- 1. Register a new Premium member
INSERT INTO Members (name, email, phone, membership_tier, max_books_allowed, joined_date, status)
VALUES ('Ananya Gupta', 'ananya@gmail.com', '9876543299', 'Premium', 5, CURDATE(), 'Active');

-- 2. Issue a book using Stored Procedure (auto-calculates 15-day due date & triggers stock -1)
CALL sp_IssueBook(LAST_INSERT_ID(), 103, CURDATE());
SELECT book_id, title, available_copies, total_copies FROM Books WHERE book_id = 103;

-- 3. Renew an active borrowing (+7 days) when no hold exists
CALL sp_RenewBook(8);
SELECT * FROM Borrowings WHERE borrow_id = 8;

-- 4. Try renewing a book that has pending Reservations (Borrow #6 = Clean Code #105)
-- Uncomment to demonstrate business-rule error handling in your viva!
-- CALL sp_RenewBook(6);

-- 5. Return an overdue book using Stored Procedure (auto-calculates tier fine & restores stock +1)
CALL sp_ReturnBook(4, CURDATE());
SELECT * FROM Borrowings WHERE borrow_id = 4;

-- 6. Collect fine payment via UPI
INSERT INTO FinePayments (borrow_id, member_id, amount_paid, payment_date, payment_method)
VALUES (4, 2, 24.00, CURDATE(), 'UPI');

-- 7. Inspect Member Financial Ledger & Trigger Activity Log
SELECT * FROM vw_MemberLedger;
SELECT * FROM ActivityLog ORDER BY log_id DESC LIMIT 10;
