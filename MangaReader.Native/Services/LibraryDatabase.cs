using MangaReader.Native.Models;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace MangaReader.Native.Services;

public sealed class LibraryDatabase
{
    private const int MaxBackupFiles = 40;
    private const string UpsertBookSql =
        """
        INSERT INTO books(id, title, author, character_name, foreign_name, tags, produced_at, imported_at, summary,
                          folder_path, page_count, total_bytes, cover_page_index, last_read_page_index, is_missing, is_privacy_cover, updated_at)
        VALUES ($id, $title, $author, $characterName, $foreignName, $tags, $producedAt, $importedAt, $summary,
                $folderPath, $pageCount, $totalBytes, $coverPageIndex, $lastReadPageIndex, $isMissing, $isPrivacyCover, $updatedAt)
        ON CONFLICT(id) DO UPDATE SET
            title = excluded.title,
            author = excluded.author,
            character_name = excluded.character_name,
            foreign_name = excluded.foreign_name,
            tags = CASE WHEN books.tags = '' THEN excluded.tags ELSE books.tags END,
            produced_at = excluded.produced_at,
            imported_at = CASE WHEN books.imported_at = '' THEN excluded.imported_at ELSE books.imported_at END,
            summary = excluded.summary,
            folder_path = excluded.folder_path,
            page_count = excluded.page_count,
            total_bytes = excluded.total_bytes,
            cover_page_index = excluded.cover_page_index,
            last_read_page_index = excluded.last_read_page_index,
            is_missing = excluded.is_missing,
            is_privacy_cover = excluded.is_privacy_cover,
            updated_at = excluded.updated_at;
        """;
    private static readonly TimeSpan MetadataBackupInterval = TimeSpan.FromMinutes(10);
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly string _backupPath;
    private readonly ConcurrentDictionary<string, string> _settingsCache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastMetadataBackupAt = DateTimeOffset.MinValue;

    public LibraryDatabase(AppStorage storage)
    {
        _databasePath = storage.DatabasePath;
        _backupPath = storage.BackupPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = storage.DatabasePath
        }.ToString();
    }

    public void Initialize()
    {
        BackupDatabase("before-migration", force: false);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS books (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                author TEXT NOT NULL DEFAULT '',
                character_name TEXT NOT NULL DEFAULT '',
                foreign_name TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '',
                produced_at TEXT NOT NULL DEFAULT '',
                imported_at TEXT NOT NULL DEFAULT '',
                summary TEXT NOT NULL DEFAULT '',
                folder_path TEXT NOT NULL UNIQUE,
                page_count INTEGER NOT NULL DEFAULT 0,
                total_bytes INTEGER NOT NULL DEFAULT 0,
                cover_page_index INTEGER NOT NULL DEFAULT 0,
                book_style INTEGER NOT NULL DEFAULT -1,
                last_read_page_index INTEGER NOT NULL DEFAULT 0,
                read_count INTEGER NOT NULL DEFAULT 0,
                reading_status TEXT NOT NULL DEFAULT 'unread',
                is_favorite INTEGER NOT NULL DEFAULT 0,
                rating REAL NOT NULL DEFAULT 0,
                is_missing INTEGER NOT NULL DEFAULT 0,
                is_hidden INTEGER NOT NULL DEFAULT 0,
                is_privacy_cover INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS library_roots (
                path TEXT PRIMARY KEY,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS shortcuts (
                action_id TEXT PRIMARY KEY,
                keybinding TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS managed_tags (
                name TEXT PRIMARY KEY,
                category TEXT NOT NULL DEFAULT '自定义',
                is_exclusive INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS managed_authors (
                name TEXT PRIMARY KEY,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS suppressed_tags (
                name TEXT PRIMARY KEY,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS book_bookmarks (
                book_id TEXT NOT NULL,
                page_index INTEGER NOT NULL,
                label TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                PRIMARY KEY (book_id, page_index),
                FOREIGN KEY (book_id) REFERENCES books(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reverse_organize_pending_redirects (
                book_id TEXT PRIMARY KEY,
                title TEXT NOT NULL DEFAULT '',
                author TEXT NOT NULL DEFAULT '',
                source_path TEXT NOT NULL,
                target_path TEXT NOT NULL,
                manifest_path TEXT NOT NULL DEFAULT '',
                target_root TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_books_author ON books(author);
            CREATE INDEX IF NOT EXISTS idx_books_reading_status ON books(reading_status);
            CREATE INDEX IF NOT EXISTS idx_books_is_favorite ON books(is_favorite);
            CREATE INDEX IF NOT EXISTS idx_books_is_hidden ON books(is_hidden);
            CREATE INDEX IF NOT EXISTS idx_books_folder_path ON books(folder_path);
            CREATE INDEX IF NOT EXISTS idx_book_bookmarks_book_id ON book_bookmarks(book_id);
            CREATE INDEX IF NOT EXISTS idx_books_last_opened_at ON books(last_opened_at);
            CREATE INDEX IF NOT EXISTS idx_reverse_organize_pending_target_root ON reverse_organize_pending_redirects(target_root);

            CREATE TABLE IF NOT EXISTS misc_images (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL UNIQUE,
                file_name TEXT NOT NULL DEFAULT '',
                category TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '',
                comment TEXT NOT NULL DEFAULT '',
                rating REAL NOT NULL DEFAULT 0,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                file_size INTEGER NOT NULL DEFAULT 0,
                imported_at TEXT NOT NULL DEFAULT '',
                last_opened_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_misc_images_category ON misc_images(category);
            CREATE INDEX IF NOT EXISTS idx_misc_images_is_favorite ON misc_images(is_favorite);
            CREATE INDEX IF NOT EXISTS idx_misc_images_last_opened_at ON misc_images(last_opened_at);

            CREATE TABLE IF NOT EXISTS misc_image_tags (
                name TEXT PRIMARY KEY,
                category TEXT NOT NULL DEFAULT '',
                color TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_misc_image_tags_category ON misc_image_tags(category);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "books", "character_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "books", "foreign_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "books", "produced_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "books", "imported_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "books", "summary", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "books", "total_bytes", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "books", "book_style", "INTEGER NOT NULL DEFAULT -1");
        EnsureColumn(connection, "books", "read_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "books", "reading_status", "TEXT NOT NULL DEFAULT 'unread'");
        EnsureColumn(connection, "books", "is_favorite", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "books", "rating", "REAL NOT NULL DEFAULT 0");
        EnsureColumn(connection, "books", "is_hidden", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "books", "is_privacy_cover", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "books", "last_opened_at", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "managed_tags", "category", "TEXT NOT NULL DEFAULT '自定义'");
        EnsureColumn(connection, "managed_tags", "is_exclusive", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "managed_tags", "color", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "book_bookmarks", "label", "TEXT NOT NULL DEFAULT ''");
    }

    public void SaveLibraryRoot(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO library_roots(path, updated_at)
            VALUES ($path, $updatedAt)
            ON CONFLICT(path) DO UPDATE SET updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public List<string> LoadLibraryRoots()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM library_roots ORDER BY updated_at DESC;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public Dictionary<string, MangaBook> LoadBooksByPath()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, author, tags, folder_path, page_count, cover_page_index,
                   last_read_page_index, is_missing
                 , character_name, produced_at, imported_at, summary, book_style, is_hidden, read_count, reading_status, is_favorite, foreign_name, total_bytes, is_privacy_cover, rating, last_opened_at
            FROM books;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, MangaBook>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var book = new MangaBook
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Author = reader.GetString(2),
                Tags = reader.GetString(3),
                FolderPath = reader.GetString(4),
                PageCount = reader.GetInt32(5),
                CoverPageIndex = reader.GetInt32(6),
                LastReadPageIndex = reader.GetInt32(7),
                IsMissing = reader.GetInt32(8) == 1,
                CharacterName = reader.GetString(9),
                ProducedAt = reader.GetString(10),
                ImportedAt = reader.GetString(11),
                Summary = reader.GetString(12),
                BookStyle = reader.GetInt32(13),
                IsHidden = reader.GetInt32(14) == 1,
                ReadCount = reader.GetInt32(15),
                ReadingStatus = reader.GetString(16),
                IsFavorite = reader.GetInt32(17) == 1,
                ForeignName = reader.GetString(18),
                TotalBytes = reader.GetInt64(19),
                IsPrivacyCover = reader.GetInt32(20) == 1,
                Rating = reader.GetDouble(21),
                LastOpenedAt = reader.IsDBNull(22) ? "" : reader.GetString(22)
            };
            result[book.FolderPath] = book;
        }
        return result;
    }

    public void UpsertBook(MangaBook book)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertBookSql;
        AddBookParameters(command, book);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void UpsertBooksBatch(IReadOnlyCollection<MangaBook> books)
    {
        if (books.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertBookSql;
            foreach (var book in books)
            {
                command.Parameters.Clear();
                AddBookParameters(command, book);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void SaveProgress(MangaBook book)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE books
            SET last_read_page_index = $lastReadPageIndex,
                reading_status = $readingStatus,
                last_opened_at = $lastOpenedAt,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", book.Id);
        command.Parameters.AddWithValue("$lastReadPageIndex", book.LastReadPageIndex);
        command.Parameters.AddWithValue("$readingStatus", book.ReadingStatus);
        command.Parameters.AddWithValue("$lastOpenedAt", book.LastOpenedAt);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void SaveMetadata(MangaBook book)
    {
        BackupDatabase("before-metadata-save", force: ShouldCreateMetadataBackup());
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE books
            SET title = $title,
                author = $author,
                character_name = $characterName,
                foreign_name = $foreignName,
                tags = $tags,
                produced_at = $producedAt,
                imported_at = $importedAt,
                summary = $summary,
                cover_page_index = $coverPageIndex,
                book_style = $bookStyle,
                read_count = $readCount,
                reading_status = $readingStatus,
                is_favorite = $isFavorite,
                rating = $rating,
                is_privacy_cover = $isPrivacyCover,
                last_opened_at = $lastOpenedAt,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", book.Id);
        command.Parameters.AddWithValue("$title", book.Title);
        command.Parameters.AddWithValue("$author", book.Author);
        command.Parameters.AddWithValue("$characterName", book.CharacterName);
        command.Parameters.AddWithValue("$foreignName", book.ForeignName);
        command.Parameters.AddWithValue("$tags", book.Tags);
        command.Parameters.AddWithValue("$producedAt", book.ProducedAt);
        command.Parameters.AddWithValue("$importedAt", book.ImportedAt);
        command.Parameters.AddWithValue("$summary", book.Summary);
        command.Parameters.AddWithValue("$coverPageIndex", book.CoverPageIndex);
        command.Parameters.AddWithValue("$bookStyle", book.BookStyle);
        command.Parameters.AddWithValue("$readCount", book.ReadCount);
        command.Parameters.AddWithValue("$readingStatus", book.ReadingStatus);
        command.Parameters.AddWithValue("$isFavorite", book.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$rating", Math.Clamp(book.Rating, 0, 5));
        command.Parameters.AddWithValue("$isPrivacyCover", book.IsPrivacyCover ? 1 : 0);
        command.Parameters.AddWithValue("$lastOpenedAt", book.LastOpenedAt);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void SaveBookTagsBatch(IReadOnlyCollection<(string BookId, string Tags)> updates, string reason)
    {
        if (updates.Count == 0)
        {
            return;
        }

        BackupDatabase(reason, force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE books
                SET tags = $tags,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            var updatedAt = DateTimeOffset.Now.ToString("O");
            foreach (var (bookId, tags) in updates)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", bookId);
                command.Parameters.AddWithValue("$tags", tags);
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            _lastMetadataBackupAt = DateTimeOffset.Now;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void SaveBookAuthorsBatch(IReadOnlyCollection<(string BookId, string Author)> updates, string reason)
    {
        if (updates.Count == 0)
        {
            return;
        }

        BackupDatabase(reason, force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var updatedAt = DateTimeOffset.Now.ToString("O");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE books
                SET author = $author,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            foreach (var (bookId, author) in updates)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", bookId);
                command.Parameters.AddWithValue("$author", author);
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            _lastMetadataBackupAt = DateTimeOffset.Now;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void SaveBookTitlesBatch(IReadOnlyCollection<(string BookId, string Title)> updates, string reason)
    {
        if (updates.Count == 0)
        {
            return;
        }

        BackupDatabase(reason, force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var updatedAt = DateTimeOffset.Now.ToString("O");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE books
                SET title = $title,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            foreach (var (bookId, title) in updates)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", bookId);
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            _lastMetadataBackupAt = DateTimeOffset.Now;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void SaveBookStylesBatch(IReadOnlyCollection<(string BookId, int BookStyle)> updates, string reason)
    {
        if (updates.Count == 0)
        {
            return;
        }

        BackupDatabase(reason, force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var updatedAt = DateTimeOffset.Now.ToString("O");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE books
                SET book_style = $bookStyle,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;
            foreach (var (bookId, bookStyle) in updates)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", bookId);
                command.Parameters.AddWithValue("$bookStyle", bookStyle);
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            _lastMetadataBackupAt = DateTimeOffset.Now;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void SaveReadCount(MangaBook book)
    {
        BackupDatabase("before-read-count-save", force: ShouldCreateMetadataBackup());
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE books
            SET read_count = $readCount,
                reading_status = $readingStatus,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", book.Id);
        command.Parameters.AddWithValue("$readCount", book.ReadCount);
        command.Parameters.AddWithValue("$readingStatus", book.ReadingStatus);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void SetHidden(MangaBook book, bool isHidden)
    {
        BackupDatabase("before-hidden-toggle", force: ShouldCreateMetadataBackup());
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE books
            SET is_hidden = $isHidden,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", book.Id);
        command.Parameters.AddWithValue("$isHidden", isHidden ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void SetPrivacyCover(MangaBook book, bool isPrivacyCover)
    {
        BackupDatabase("before-privacy-cover-toggle", force: ShouldCreateMetadataBackup());
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE books
            SET is_privacy_cover = $isPrivacyCover,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", book.Id);
        command.Parameters.AddWithValue("$isPrivacyCover", isPrivacyCover ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void DeleteBook(MangaBook book)
    {
        BackupDatabase("before-delete-book", force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM books WHERE id = $id;";
            command.Parameters.AddWithValue("$id", book.Id);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void RelocateBook(string oldId, MangaBook book)
    {
        BackupDatabase("before-relocate-book", force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            // Delete old record
            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM books WHERE id = $oldId;";
                deleteCmd.Parameters.AddWithValue("$oldId", oldId);
                deleteCmd.ExecuteNonQuery();
            }

            // Insert with new id
            using (var insertCmd = connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = UpsertBookSql;
                AddBookParameters(insertCmd, book);
                insertCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void UpdateFolderPath(MangaBook book)
    {
        BackupDatabase("before-relocate-book", force: true);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE books
            SET folder_path = $folderPath,
                page_count = $pageCount,
                total_bytes = $totalBytes,
                cover_page_index = $coverPageIndex,
                last_read_page_index = $lastReadPageIndex,
                is_missing = $isMissing,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", book.Id);
        command.Parameters.AddWithValue("$folderPath", book.FolderPath);
        command.Parameters.AddWithValue("$pageCount", book.PageCount);
        command.Parameters.AddWithValue("$totalBytes", book.TotalBytes);
        command.Parameters.AddWithValue("$coverPageIndex", book.CoverPageIndex);
        command.Parameters.AddWithValue("$lastReadPageIndex", book.LastReadPageIndex);
        command.Parameters.AddWithValue("$isMissing", book.IsMissing ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpdateFolderPathBatch(IReadOnlyCollection<MangaBook> books, string reason)
    {
        if (books.Count == 0)
        {
            return;
        }

        BackupDatabase(reason, force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE books
                SET folder_path = $folderPath,
                    page_count = $pageCount,
                    total_bytes = $totalBytes,
                    cover_page_index = $coverPageIndex,
                    last_read_page_index = $lastReadPageIndex,
                    is_missing = $isMissing,
                    updated_at = $updatedAt
                WHERE id = $id;
                """;

            var updatedAt = DateTimeOffset.Now.ToString("O");
            foreach (var book in books)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", book.Id);
                command.Parameters.AddWithValue("$folderPath", book.FolderPath);
                command.Parameters.AddWithValue("$pageCount", book.PageCount);
                command.Parameters.AddWithValue("$totalBytes", book.TotalBytes);
                command.Parameters.AddWithValue("$coverPageIndex", book.CoverPageIndex);
                command.Parameters.AddWithValue("$lastReadPageIndex", book.LastReadPageIndex);
                command.Parameters.AddWithValue("$isMissing", book.IsMissing ? 1 : 0);
                command.Parameters.AddWithValue("$updatedAt", updatedAt);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            _lastMetadataBackupAt = DateTimeOffset.Now;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public List<ReverseOrganizePendingRedirectRecord> LoadPendingReverseOrganizeRedirects()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT book_id, title, author, source_path, target_path, manifest_path, target_root, created_at, updated_at
            FROM reverse_organize_pending_redirects
            ORDER BY updated_at DESC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<ReverseOrganizePendingRedirectRecord>();
        while (reader.Read())
        {
            result.Add(new ReverseOrganizePendingRedirectRecord
            {
                BookId = reader.GetString(0),
                Title = reader.GetString(1),
                Author = reader.GetString(2),
                SourcePath = reader.GetString(3),
                TargetPath = reader.GetString(4),
                ManifestPath = reader.GetString(5),
                TargetRoot = reader.GetString(6),
                CreatedAt = reader.GetString(7),
                UpdatedAt = reader.GetString(8)
            });
        }

        return result;
    }

    public void SavePendingReverseOrganizeRedirects(IReadOnlyCollection<ReverseOrganizePendingRedirectRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO reverse_organize_pending_redirects
                    (book_id, title, author, source_path, target_path, manifest_path, target_root, created_at, updated_at)
                VALUES
                    ($bookId, $title, $author, $sourcePath, $targetPath, $manifestPath, $targetRoot, $createdAt, $updatedAt)
                ON CONFLICT(book_id) DO UPDATE SET
                    title = excluded.title,
                    author = excluded.author,
                    source_path = excluded.source_path,
                    target_path = excluded.target_path,
                    manifest_path = excluded.manifest_path,
                    target_root = excluded.target_root,
                    updated_at = excluded.updated_at;
                """;

            foreach (var record in records)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$bookId", record.BookId);
                command.Parameters.AddWithValue("$title", record.Title);
                command.Parameters.AddWithValue("$author", record.Author);
                command.Parameters.AddWithValue("$sourcePath", record.SourcePath);
                command.Parameters.AddWithValue("$targetPath", record.TargetPath);
                command.Parameters.AddWithValue("$manifestPath", record.ManifestPath);
                command.Parameters.AddWithValue("$targetRoot", record.TargetRoot);
                command.Parameters.AddWithValue("$createdAt", string.IsNullOrWhiteSpace(record.CreatedAt) ? DateTimeOffset.Now.ToString("O") : record.CreatedAt);
                command.Parameters.AddWithValue("$updatedAt", string.IsNullOrWhiteSpace(record.UpdatedAt) ? DateTimeOffset.Now.ToString("O") : record.UpdatedAt);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void RemovePendingReverseOrganizeRedirects(IReadOnlyCollection<string> bookIds)
    {
        if (bookIds.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM reverse_organize_pending_redirects WHERE book_id = $bookId;";
            foreach (var bookId in bookIds)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$bookId", bookId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public Dictionary<string, string> LoadShortcuts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT action_id, keybinding FROM shortcuts;";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    public Dictionary<int, string> LoadBookmarks(string bookId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT page_index, label FROM book_bookmarks WHERE book_id = $bookId ORDER BY page_index ASC;";
        command.Parameters.AddWithValue("$bookId", bookId);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<int, string>();
        while (reader.Read())
        {
            result[reader.GetInt32(0)] = reader.GetString(1);
        }
        return result;
    }

    public void AddBookmark(string bookId, int pageIndex, string? label = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO book_bookmarks(book_id, page_index, label, created_at)
            VALUES ($bookId, $pageIndex, $label, $createdAt)
            ON CONFLICT(book_id, page_index) DO UPDATE SET label = $label;
            """;
        command.Parameters.AddWithValue("$bookId", bookId);
        command.Parameters.AddWithValue("$pageIndex", pageIndex);
        command.Parameters.AddWithValue("$label", label ?? "");
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RemoveBookmark(string bookId, int pageIndex)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM book_bookmarks WHERE book_id = $bookId AND page_index = $pageIndex;";
        command.Parameters.AddWithValue("$bookId", bookId);
        command.Parameters.AddWithValue("$pageIndex", pageIndex);
        command.ExecuteNonQuery();
    }

    public void RemoveAllBookmarks(string bookId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM book_bookmarks WHERE book_id = $bookId;";
        command.Parameters.AddWithValue("$bookId", bookId);
        command.ExecuteNonQuery();
    }

    public void ClearAllBookmarks()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM book_bookmarks;";
        command.ExecuteNonQuery();
    }

    public string LoadSetting(string key, string defaultValue = "")
    {
        return _settingsCache.GetOrAdd(key, _ =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string ?? defaultValue;
        });
    }

    public List<ManagedTagRecord> LoadManagedTags()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, category, is_exclusive, updated_at, color FROM managed_tags ORDER BY updated_at DESC, name ASC;";
        using var reader = command.ExecuteReader();
        var result = new List<ManagedTagRecord>();
        while (reader.Read())
        {
            var color = reader.IsDBNull(4) ? "" : reader.GetString(4);
            result.Add(new ManagedTagRecord(reader.GetString(0), reader.GetString(1), reader.GetInt32(2) == 1, reader.GetString(3), color));
        }
        return result;
    }

    public List<string> LoadSuppressedTags()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM suppressed_tags;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public List<string> LoadManagedAuthors()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM managed_authors ORDER BY name ASC;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    public void SaveManagedAuthor(string author)
    {
        BackupDatabase("before-managed-author-save", force: ShouldCreateMetadataBackup());
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO managed_authors(name, updated_at)
            VALUES ($name, $updatedAt)
            ON CONFLICT(name) DO UPDATE SET
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$name", author);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void RenameManagedAuthor(string oldName, string newName)
    {
        BackupDatabase("before-managed-author-rename", force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM managed_authors WHERE name = $oldName;";
            deleteCommand.Parameters.AddWithValue("$oldName", oldName);
            deleteCommand.ExecuteNonQuery();
        }

        using (var upsertCommand = connection.CreateCommand())
        {
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText =
                """
                INSERT INTO managed_authors(name, updated_at)
                VALUES ($newName, $updatedAt)
                ON CONFLICT(name) DO UPDATE SET
                    updated_at = excluded.updated_at;
                """;
            upsertCommand.Parameters.AddWithValue("$newName", newName);
            upsertCommand.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            upsertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void DeleteManagedAuthor(string author)
    {
        BackupDatabase("before-managed-author-delete", force: true);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM managed_authors WHERE name = $name;";
        command.Parameters.AddWithValue("$name", author);
        command.ExecuteNonQuery();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void SaveManagedTag(string tag, string category = "自定义", bool isExclusive = false, string color = "")
    {
        BackupDatabase("before-managed-tag-save", force: ShouldCreateMetadataBackup());
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO managed_tags(name, category, is_exclusive, updated_at, color)
                VALUES ($name, $category, $isExclusive, $updatedAt, $color)
                ON CONFLICT(name) DO UPDATE SET
                    category = excluded.category,
                    is_exclusive = excluded.is_exclusive,
                    updated_at = excluded.updated_at,
                    color = excluded.color;
                """;
            command.Parameters.AddWithValue("$name", tag);
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$isExclusive", isExclusive ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$color", color);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM suppressed_tags WHERE name = $name;";
            command.Parameters.AddWithValue("$name", tag);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void RenameManagedTag(string oldName, string newName, string category = "自定义", bool isExclusive = false, string color = "")
    {
        BackupDatabase("before-managed-tag-rename", force: true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM managed_tags WHERE name = $oldName;";
            deleteCommand.Parameters.AddWithValue("$oldName", oldName);
            deleteCommand.ExecuteNonQuery();
        }

        using (var upsertCommand = connection.CreateCommand())
        {
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText =
                """
                INSERT INTO managed_tags(name, category, is_exclusive, updated_at, color)
                VALUES ($newName, $category, $isExclusive, $updatedAt, $color)
                ON CONFLICT(name) DO UPDATE SET
                    category = excluded.category,
                    is_exclusive = excluded.is_exclusive,
                    updated_at = excluded.updated_at,
                    color = excluded.color;
                """;
            upsertCommand.Parameters.AddWithValue("$newName", newName);
            upsertCommand.Parameters.AddWithValue("$category", category);
            upsertCommand.Parameters.AddWithValue("$isExclusive", isExclusive ? 1 : 0);
            upsertCommand.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            upsertCommand.Parameters.AddWithValue("$color", color);
            upsertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        _lastMetadataBackupAt = DateTimeOffset.Now;
    }

    public void DeleteManagedTag(string tag)
    {
        BackupDatabase("before-managed-tag-delete", force: true);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM managed_tags WHERE name = $name;";
        command.Parameters.AddWithValue("$name", tag);
        command.ExecuteNonQuery();
    }

    public void RenameTagCategory(string oldCategory, string newCategory)
    {
        BackupDatabase("before-tag-category-rename", force: true);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE managed_tags SET category = $newCat, updated_at = $now WHERE category = $oldCat;";
        command.Parameters.AddWithValue("$oldCat", oldCategory);
        command.Parameters.AddWithValue("$newCat", newCategory);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void SuppressTag(string tag)
    {
        BackupDatabase("before-tag-suppress", force: true);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO suppressed_tags(name, updated_at)
            VALUES ($name, $updatedAt)
            ON CONFLICT(name) DO UPDATE SET
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$name", tag);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void SaveShortcut(string actionId, string keybinding)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO shortcuts(action_id, keybinding, updated_at)
            VALUES ($actionId, $keybinding, $updatedAt)
            ON CONFLICT(action_id) DO UPDATE SET
                keybinding = excluded.keybinding,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$actionId", actionId);
        command.Parameters.AddWithValue("$keybinding", keybinding);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void SaveSetting(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_settings(key, value, updated_at)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
        _settingsCache[key] = value;
    }

    public string CreateManualBackup()
    {
        return BackupDatabase("manual-backup", force: true);
    }

    public string CreateTagManagementBackup()
    {
        return BackupDatabase("before-tag-management", force: true);
    }

    private bool ShouldCreateMetadataBackup()
    {
        return DateTimeOffset.Now - _lastMetadataBackupAt >= MetadataBackupInterval;
    }

    private string BackupDatabase(string reason, bool force)
    {
        if (!File.Exists(_databasePath))
        {
            return "";
        }

        if (!force && !ShouldCreateMetadataBackup())
        {
            return "";
        }

        Directory.CreateDirectory(_backupPath);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff");
        var safeReason = string.Join("-", reason.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var backupDatabasePath = Path.Combine(_backupPath, $"app_{timestamp}_{safeReason}.db");

        File.Copy(_databasePath, backupDatabasePath, overwrite: false);
        TryCopyCompanionFile("-wal", backupDatabasePath);
        TryCopyCompanionFile("-shm", backupDatabasePath);
        PruneBackups();
        return backupDatabasePath;
    }

    private void TryCopyCompanionFile(string suffix, string backupDatabasePath)
    {
        var source = _databasePath + suffix;
        if (!File.Exists(source))
        {
            return;
        }

        try
        {
            File.Copy(source, backupDatabasePath + suffix, overwrite: false);
        }
        catch (IOException ex)
        {
            AppLogger.Warn("database-backup", $"跳过被锁定的 SQLite 伴生文件：{Path.GetFileName(source)}。{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLogger.Warn("database-backup", $"跳过无权限访问的 SQLite 伴生文件：{Path.GetFileName(source)}。{ex.Message}");
        }
    }

    private void PruneBackups()
    {
        var backups = Directory.EnumerateFiles(_backupPath, "app_*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Skip(MaxBackupFiles)
            .ToList();

        foreach (var file in backups)
        {
            TryDelete(file.FullName);
            TryDelete(file.FullName + "-wal");
            TryDelete(file.FullName + "-shm");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }


public sealed record ManagedTagRecord(string Name, string Category, bool IsExclusive, string UpdatedAt, string Color = "");
public sealed record BookmarkRecord(string BookId, int PageIndex, string CreatedAt);

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static void AddBookParameters(SqliteCommand command, MangaBook book)
    {
        command.Parameters.AddWithValue("$id", book.Id);
        command.Parameters.AddWithValue("$title", book.Title);
        command.Parameters.AddWithValue("$author", book.Author);
        command.Parameters.AddWithValue("$characterName", book.CharacterName);
        command.Parameters.AddWithValue("$foreignName", book.ForeignName);
        command.Parameters.AddWithValue("$tags", book.Tags);
        command.Parameters.AddWithValue("$producedAt", book.ProducedAt);
        command.Parameters.AddWithValue("$importedAt", string.IsNullOrWhiteSpace(book.ImportedAt) ? DateTimeOffset.Now.ToString("O") : book.ImportedAt);
        command.Parameters.AddWithValue("$summary", book.Summary);
        command.Parameters.AddWithValue("$folderPath", book.FolderPath);
        command.Parameters.AddWithValue("$pageCount", book.PageCount);
        command.Parameters.AddWithValue("$totalBytes", book.TotalBytes);
        command.Parameters.AddWithValue("$coverPageIndex", book.CoverPageIndex);
        command.Parameters.AddWithValue("$lastReadPageIndex", book.LastReadPageIndex);
        command.Parameters.AddWithValue("$isMissing", book.IsMissing ? 1 : 0);
        command.Parameters.AddWithValue("$isPrivacyCover", book.IsPrivacyCover ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
    }

    // ===== 杂图（misc_images）CRUD =====
    // 独立于 books 表，0-10 评分、独立 tag 体系，不参与反向规整 / 批量整理。

    public void UpsertMiscImage(MiscImage image)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO misc_images(id, file_path, file_name, category, tags, comment, rating,
                                     is_favorite, file_size, imported_at, last_opened_at, updated_at)
            VALUES ($id, $filePath, $fileName, $category, $tags, $comment, $rating,
                    $isFavorite, $fileSize, $importedAt, $lastOpenedAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                file_path = excluded.file_path,
                file_name = excluded.file_name,
                category = CASE WHEN misc_images.category = '' THEN excluded.category ELSE misc_images.category END,
                tags = excluded.tags,
                comment = excluded.comment,
                rating = excluded.rating,
                is_favorite = excluded.is_favorite,
                file_size = excluded.file_size,
                last_opened_at = excluded.last_opened_at,
                updated_at = excluded.updated_at;
            """;
        AddMiscImageParameters(command, image);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void UpsertMiscImagesBatch(IReadOnlyCollection<MiscImage> images)
    {
        if (images.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO misc_images(id, file_path, file_name, category, tags, comment, rating,
                                     is_favorite, file_size, imported_at, last_opened_at, updated_at)
            VALUES ($id, $filePath, $fileName, $category, $tags, $comment, $rating,
                    $isFavorite, $fileSize, $importedAt, $lastOpenedAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                file_path = excluded.file_path,
                file_name = excluded.file_name,
                file_size = excluded.file_size,
                updated_at = excluded.updated_at;
            """;
        foreach (var image in images)
        {
            command.Parameters.Clear();
            AddMiscImageParameters(command, image);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<MiscImage> LoadAllMiscImages()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, file_path, file_name, category, tags, comment, rating,
                   is_favorite, file_size, imported_at, last_opened_at
            FROM misc_images
            ORDER BY imported_at DESC, file_name COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<MiscImage>();
        while (reader.Read())
        {
            var image = new MiscImage
            {
                Id = reader.GetString(0),
                FilePath = reader.GetString(1),
                FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Category = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Tags = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Comment = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Rating = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                IsFavorite = !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                FileSize = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                ImportedAt = reader.IsDBNull(9) ? "" : reader.GetString(9),
                LastOpenedAt = reader.IsDBNull(10) ? "" : reader.GetString(10)
            };
            result.Add(image);
        }
        return result;
    }

    public void DeleteMiscImage(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM misc_images WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateMiscImageFavorite(string id, bool isFavorite)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE misc_images
            SET is_favorite = $isFavorite, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$isFavorite", isFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpdateMiscImageRating(string id, double rating)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE misc_images
            SET rating = $rating, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$rating", rating);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpdateMiscImageComment(string id, string comment)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE misc_images
            SET comment = $comment, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$comment", comment ?? "");
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpdateMiscImageTags(string id, string tags)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE misc_images
            SET tags = $tags, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$tags", tags ?? "");
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpdateMiscImageCategory(string id, string category)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE misc_images
            SET category = $category, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$category", category ?? "");
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void UpdateMiscImageLastOpenedAt(string id, string lastOpenedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE misc_images
            SET last_opened_at = $lastOpenedAt, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$lastOpenedAt", lastOpenedAt ?? "");
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    // 反向重命名：数据库 file_path/file_name/updated_at 三字段同步更新（File.Move 由调用方执行）。
    public void RenameMiscImageFile(string id, string newFilePath, string newFileName)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE misc_images
            SET file_path = $filePath, file_name = $fileName, updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$filePath", newFilePath);
        command.Parameters.AddWithValue("$fileName", newFileName);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void AddMiscImageParameters(SqliteCommand command, MiscImage image)
    {
        command.Parameters.AddWithValue("$id", image.Id);
        command.Parameters.AddWithValue("$filePath", image.FilePath);
        command.Parameters.AddWithValue("$fileName", image.FileName);
        command.Parameters.AddWithValue("$category", image.Category ?? "");
        command.Parameters.AddWithValue("$tags", image.Tags ?? "");
        command.Parameters.AddWithValue("$comment", image.Comment ?? "");
        command.Parameters.AddWithValue("$rating", image.Rating);
        command.Parameters.AddWithValue("$isFavorite", image.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$fileSize", image.FileSize);
        command.Parameters.AddWithValue("$importedAt", string.IsNullOrWhiteSpace(image.ImportedAt) ? DateTimeOffset.Now.ToString("O") : image.ImportedAt);
        command.Parameters.AddWithValue("$lastOpenedAt", image.LastOpenedAt ?? "");
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
    }

    // ===== 杂图 tag（misc_image_tags）CRUD =====
    // 独立于 managed_tags 表，颜色/分类独立存储，与漫画 tag 体系完全隔离。

    public List<ManagedTagRecord> LoadMiscTags()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name, category, color, updated_at
            FROM misc_image_tags
            ORDER BY category COLLATE NOCASE, name COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<ManagedTagRecord>();
        while (reader.Read())
        {
            result.Add(new ManagedTagRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                false,
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(2) ? "" : reader.GetString(2)
            ));
        }
        return result;
    }

    public void UpsertMiscTag(string name, string category, string color)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO misc_image_tags(name, category, color, updated_at)
            VALUES ($name, $category, $color, $updatedAt)
            ON CONFLICT(name) DO UPDATE SET
                category = excluded.category,
                color = excluded.color,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$category", category ?? "");
        command.Parameters.AddWithValue("$color", color ?? "");
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeleteMiscTag(string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM misc_image_tags WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    // 重命名杂图 tag：更新 misc_image_tags.name 并同步替换 misc_images.tags 字段中所有引用。
    // tags 字段以逗号分隔，需逐条解析后重写，避免误伤同名子串。
    public void RenameMiscTag(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            // 1. 检查新名称是否已存在
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT COUNT(*) FROM misc_image_tags WHERE name = $newName;";
                checkCmd.Parameters.AddWithValue("$newName", newName);
                var existing = Convert.ToInt32(checkCmd.ExecuteScalar());
                if (existing > 0)
                {
                    throw new InvalidOperationException($"目标名称已存在：{newName}");
                }
            }

            // 2. 重命名 misc_image_tags
            using (var renameCmd = connection.CreateCommand())
            {
                renameCmd.Transaction = transaction;
                renameCmd.CommandText =
                    """
                    UPDATE misc_image_tags
                    SET name = $newName, updated_at = $updatedAt
                    WHERE name = $oldName;
                    """;
                renameCmd.Parameters.AddWithValue("$oldName", oldName);
                renameCmd.Parameters.AddWithValue("$newName", newName);
                renameCmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
                renameCmd.ExecuteNonQuery();
            }

            // 3. 同步替换 misc_images.tags 中引用此 tag 的记录
            using (var queryCmd = connection.CreateCommand())
            {
                queryCmd.Transaction = transaction;
                queryCmd.CommandText = "SELECT id, tags FROM misc_images WHERE tags LIKE $pattern;";
                queryCmd.Parameters.AddWithValue("$pattern", $"%{oldName}%");
                using var reader = queryCmd.ExecuteReader();
                var pending = new List<(string Id, string NewTags)>();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var raw = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var newTags = RewriteTagList(raw, oldName, newName);
                    if (!string.Equals(raw, newTags, StringComparison.Ordinal))
                    {
                        pending.Add((id, newTags));
                    }
                }

                foreach (var (id, newTags) in pending)
                {
                    using var updateCmd = connection.CreateCommand();
                    updateCmd.Transaction = transaction;
                    updateCmd.CommandText = "UPDATE misc_images SET tags = $tags, updated_at = $updatedAt WHERE id = $id;";
                    updateCmd.Parameters.AddWithValue("$id", id);
                    updateCmd.Parameters.AddWithValue("$tags", newTags);
                    updateCmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
                    updateCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { /* ignore */ }
            throw;
        }
    }

    // 统计某个 tag 在 misc_images.tags 中被多少张图引用。
    // 通过 LIKE 粗筛后在内存中精确比对，避免误命中同名子串。
    public int CountMiscTagUsage(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return 0;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tags FROM misc_images WHERE tags LIKE $pattern;";
        command.Parameters.AddWithValue("$pattern", $"%{tagName}%");
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var raw = reader.IsDBNull(0) ? "" : reader.GetString(0);
            foreach (var t in raw.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(t, tagName, StringComparison.Ordinal))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private static string RewriteTagList(string raw, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var separators = new[] { ',', '，', ';', '；' };
        var parts = raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rewritten = parts.Select(p => string.Equals(p, oldName, StringComparison.Ordinal) ? newName : p);
        return string.Join(", ", rewritten);
    }
}
