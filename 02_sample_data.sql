-- ============================================================
-- Library Management System (Enhanced Edition)
-- File 2: Rich Sample Data (DML - INSERT)
-- ============================================================

USE LibraryDB;

-- 1. Categories
INSERT INTO Categories (category_id, category_name, shelf_code) VALUES
(1, 'Programming', 'CS-A1'),
(2, 'Fiction',     'FI-B2'),
(3, 'Science',     'SC-C1'),
(4, 'Databases',   'CS-A2'),
(5, 'History',     'HI-D1'),
(6, 'AI & ML',     'CS-A3');

-- 2. Members (Across Student, Premium, and Faculty tiers)
INSERT INTO Members (member_id, name, email, phone, membership_tier, max_books_allowed, joined_date, status) VALUES
(1, 'Aarav Sharma',  'aarav@gmail.com',  '9876543210', 'Student', 3, '2026-08-10', 'Active'),
(2, 'Riya Patel',    'riya@gmail.com',   '9876543211', 'Premium', 5, '2026-08-15', 'Active'),
(3, 'Karan Verma',   'karan@gmail.com',  '9876543212', 'Student', 3, '2026-09-01', 'Active'),
(4, 'Sneha Iyer',    'sneha@gmail.com',  '9876543213', 'Student', 3, '2026-09-12', 'Active'),
(5, 'Dr. Vikram Rao','vikram@univ.edu',  '9876543214', 'Faculty', 8, '2026-07-01', 'Active'),
(6, 'Meera Nair',    'meera@gmail.com',  '9876543215', 'Premium', 5, '2026-10-05', 'Active'),
(7, 'Rohan Das',     'rohan@gmail.com',  '9876543216', 'Student', 3, '2026-10-20', 'Suspended');

-- 3. Books
-- Note: available_copies starts equal to total_copies; active borrowings below will auto-decrement available_copies via trigger!
INSERT INTO Books (book_id, isbn, title, author, category_id, total_copies, available_copies) VALUES
(101, '978-0321563842', 'C++ Basics',                       'Bjarne Stroustrup',    1, 4, 4),
(102, '978-0747532699', 'Harry Potter & Sorcerer''s Stone', 'J.K. Rowling',         2, 2, 2),
(103, '978-1118230718', 'Fundamentals of Physics',          'David Halliday',       3, 4, 4),
(104, '978-0078022159', 'Database System Concepts',         'Abraham Silberschatz', 4, 3, 3),
(105, '978-0132350884', 'Clean Code',                       'Robert C. Martin',     1, 1, 1),
(106, '978-0553380163', 'A Brief History of Time',          'Stephen Hawking',      3, 2, 2),
(107, '978-0060959581', 'India After Gandhi',               'Ramachandra Guha',     5, 3, 3),
(108, '978-0262035613', 'Deep Learning',                    'Ian Goodfellow',       6, 2, 2);

-- 4. Borrowings
-- Historical returned loans (available_copies stays unchanged because return_date IS NOT NULL)
INSERT INTO Borrowings (borrow_id, member_id, book_id, issue_date, due_date, return_date, renew_count, fine_assessed) VALUES
(1, 1, 101, '2026-11-01', '2026-11-11', '2026-11-09', 0,  0.00), -- Returned early
(2, 4, 104, '2026-11-01', '2026-11-11', '2026-11-17', 0, 30.00), -- Student 6 days late @ Rs.5 = Rs.30
(3, 1, 105, '2026-11-02', '2026-11-12', '2026-11-15', 0, 15.00); -- Student 3 days late @ Rs.5 = Rs.15

-- Active unreturned loans (trg_after_borrow_insert automatically decrements available_copies!)
INSERT INTO Borrowings (borrow_id, member_id, book_id, issue_date, due_date, return_date, renew_count, fine_assessed) VALUES
(4, 2, 102, '2026-11-02', '2026-11-17', NULL, 0, 0.00), -- Overdue Premium loan
(5, 3, 101, '2026-11-05', '2026-11-15', NULL, 1, 0.00), -- Overdue Student loan
(6, 6, 105, '2026-11-16', '2026-12-01', NULL, 0, 0.00), -- Takes the ONLY copy of Clean Code (105 now has 0 available!)
(7, 5, 108, '2026-11-18', '2026-12-18', NULL, 0, 0.00), -- Faculty 30-day loan
(8, 4, 104, '2026-11-20', '2026-11-30', NULL, 0, 0.00); -- Active within due date

-- 5. Reservations (Clean Code #105 has 0 copies left, so Aarav & Karan queued for it!)
INSERT INTO Reservations (reservation_id, member_id, book_id, reserved_date, status) VALUES
(1, 1, 105, '2026-11-19', 'Pending'),
(2, 3, 105, '2026-11-21', 'Pending'),
(3, 2, 104, '2026-11-10', 'Fulfilled');

-- 6. FinePayments (Sneha paid Rs. 20 of her Rs. 30 fine via UPI; Aarav paid full Rs. 15 in Cash)
INSERT INTO FinePayments (payment_id, borrow_id, member_id, amount_paid, payment_date, payment_method) VALUES
(1, 2, 4, 20.00, '2026-11-18', 'UPI'),
(2, 3, 1, 15.00, '2026-11-15', 'Cash');
