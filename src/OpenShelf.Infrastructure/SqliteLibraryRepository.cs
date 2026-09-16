using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenShelf.Core;

namespace OpenShelf.Infrastructure;

public sealed class SqliteLibraryRepository : ILibraryRepository
{
    private const int CurrentSchemaVersion = 3;
    private const string SettingsKey = "application";

    private readonly IAppDataPaths _paths;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteLibraryRepository(IAppDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return;
            }

            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _paths.EnsureCreated();

                    var connection = new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = _paths.DatabasePath,
                            Mode = SqliteOpenMode.ReadWriteCreate,
                            Cache = SqliteCacheMode.Private
                        }.ToString());

                    try
                    {
                        connection.Open();
                        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
                        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 5000;");
                        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
                        ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");
                        ApplyMigrations(connection, cancellationToken);
                        _connection = connection;
                    }
                    catch
                    {
                        connection.Dispose();
                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public Task<IReadOnlyList<LibraryBook>> QueryBooksAsync(
        LibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return ExecuteSerializedAsync<IReadOnlyList<LibraryBook>>(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                var sql = new StringBuilder(
                    """
                    SELECT b.id
                    FROM books b
                    LEFT JOIN favourites f ON f.book_id = b.id
                    LEFT JOIN reading_progress p ON p.book_id = b.id
                    WHERE 1 = 1
                    """);

                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    sql.Append(
                        """

                          AND (
                            COALESCE(NULLIF(b.title_override, ''), b.embedded_title)
                                LIKE $search ESCAPE '\'
                            OR COALESCE(NULLIF(b.author_override, ''), b.embedded_author)
                                LIKE $search ESCAPE '\'
                          )
                        """);
                    command.Parameters.AddWithValue(
                        "$search",
                        $"%{EscapeLikePattern(query.Search.Trim())}%");
                }

                if (query.CategoryId is { } categoryId)
                {
                    sql.Append(
                        """

                          AND EXISTS (
                            SELECT 1
                            FROM book_categories bc
                            WHERE bc.book_id = b.id AND bc.category_id = $category_id
                          )
                        """);
                    command.Parameters.AddWithValue("$category_id", categoryId.ToString("D"));
                }

                if (query.FavoritesOnly)
                {
                    sql.Append("\n  AND f.book_id IS NOT NULL");
                }

                if (query.Format is { } format)
                {
                    sql.Append("\n  AND b.format = $format");
                    command.Parameters.AddWithValue("$format", (int)format);
                }

                var direction = query.Descending ? "DESC" : "ASC";
                var sortExpression = query.Sort switch
                {
                    LibrarySort.Author =>
                        "LOWER(COALESCE(NULLIF(b.author_override, ''), b.embedded_author))",
                    LibrarySort.RecentlyAdded => "b.imported_at",
                    LibrarySort.RecentlyOpened => "COALESCE(b.last_opened_at, '')",
                    LibrarySort.Progress => "COALESCE(p.progress, 0)",
                    _ => "LOWER(COALESCE(NULLIF(b.title_override, ''), b.embedded_title))"
                };

                sql.Append($"\nORDER BY {sortExpression} {direction}, b.id ASC;");
                command.CommandText = sql.ToString();

                var ids = new List<Guid>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        ids.Add(Guid.Parse(reader.GetString(0)));
                    }
                }

                var books = new List<LibraryBook>(ids.Count);
                foreach (var id in ids)
                {
                    token.ThrowIfCancellationRequested();
                    var book = ReadBook(connection, id, token);
                    if (book is not null)
                    {
                        books.Add(book);
                    }
                }

                return books;
            },
            cancellationToken);
    }

    public Task<LibraryBook?> GetBookAsync(
        Guid bookId,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(
            (connection, token) => ReadBook(connection, bookId, token),
            cancellationToken);

    public Task<LibraryBook?> FindByHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        return ExecuteSerializedAsync(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT id FROM books WHERE content_hash = $hash COLLATE NOCASE LIMIT 1;";
                command.Parameters.AddWithValue("$hash", contentHash);
                var value = command.ExecuteScalar();
                return value is string id
                    ? ReadBook(connection, Guid.Parse(id), token)
                    : null;
            },
            cancellationToken);
    }

    public Task AddBookAsync(
        LibraryBook book,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentException.ThrowIfNullOrWhiteSpace(book.ManagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(book.ContentHash);

        return ExecuteWriteAsync(
            (connection, transaction, token) =>
            {
                using (var command = CreateCommand(
                           connection,
                           transaction,
                           """
                           INSERT INTO books (
                               id, managed_path, format, content_hash, metadata_json,
                               embedded_title, embedded_author, title_override, author_override,
                               cover_image, cover_media_type, imported_at, last_opened_at,
                               estimated_word_count
                           )
                           VALUES (
                               $id, $managed_path, $format, $content_hash, $metadata_json,
                               $embedded_title, $embedded_author, $title_override, $author_override,
                               $cover_image, $cover_media_type, $imported_at, $last_opened_at,
                               $estimated_word_count
                           );
                           """))
                {
                    command.Parameters.AddWithValue("$id", book.Id.ToString("D"));
                    command.Parameters.AddWithValue("$managed_path", book.ManagedPath);
                    command.Parameters.AddWithValue("$format", (int)book.Format);
                    command.Parameters.AddWithValue("$content_hash", book.ContentHash);
                    command.Parameters.AddWithValue(
                        "$metadata_json",
                        InfrastructureJson.SerializeMetadata(book.EmbeddedMetadata));
                    command.Parameters.AddWithValue("$embedded_title", book.EmbeddedMetadata.Title);
                    command.Parameters.AddWithValue("$embedded_author", book.EmbeddedMetadata.Author);
                    command.Parameters.AddWithValue(
                        "$title_override",
                        DbValue(book.TitleOverride));
                    command.Parameters.AddWithValue(
                        "$author_override",
                        DbValue(book.AuthorOverride));
                    command.Parameters.AddWithValue("$cover_image", DbValue(book.CoverImage));
                    command.Parameters.AddWithValue(
                        "$cover_media_type",
                        DbValue(book.CoverMediaType));
                    command.Parameters.AddWithValue(
                        "$imported_at",
                        FormatTimestamp(book.ImportedAt));
                    command.Parameters.AddWithValue(
                        "$last_opened_at",
                        DbValue(book.LastOpenedAt is { } opened
                            ? FormatTimestamp(opened)
                            : null));
                    command.Parameters.AddWithValue(
                        "$estimated_word_count",
                        Math.Max(0, book.EstimatedWordCount));
                    command.ExecuteNonQuery();
                }

                if (book.IsFavorite)
                {
                    SetFavourite(connection, transaction, book.Id, true);
                }

                if (book.ReadingPosition is not null)
                {
                    SaveReadingPosition(connection, transaction, book.ReadingPosition);
                }

                foreach (var category in book.Categories)
                {
                    token.ThrowIfCancellationRequested();
                    using (var categoryCommand = CreateCommand(
                               connection,
                               transaction,
                               """
                               INSERT OR IGNORE INTO categories (id, name, created_at)
                               VALUES ($id, $name, $created_at);
                               """))
                    {
                        categoryCommand.Parameters.AddWithValue("$id", category.Id.ToString("D"));
                        categoryCommand.Parameters.AddWithValue("$name", category.Name);
                        categoryCommand.Parameters.AddWithValue(
                            "$created_at",
                            FormatTimestamp(category.CreatedAt));
                        categoryCommand.ExecuteNonQuery();
                    }

                    InsertBookCategory(connection, transaction, book.Id, category.Id);
                }
            },
            cancellationToken);
    }

    public Task RemoveBookAsync(
        Guid bookId,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    "DELETE FROM books WHERE id = $id;");
                command.Parameters.AddWithValue("$id", bookId.ToString("D"));
                command.ExecuteNonQuery();
            },
            cancellationToken);

    public Task UpdateMetadataAsync(
        Guid bookId,
        string? titleOverride,
        string? authorOverride,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    """
                    UPDATE books
                    SET title_override = $title_override,
                        author_override = $author_override
                    WHERE id = $id;
                    """);
                command.Parameters.AddWithValue("$id", bookId.ToString("D"));
                command.Parameters.AddWithValue(
                    "$title_override",
                    DbValue(NormalizeOverride(titleOverride)));
                command.Parameters.AddWithValue(
                    "$author_override",
                    DbValue(NormalizeOverride(authorOverride)));
                command.ExecuteNonQuery();
            },
            cancellationToken);

    public Task SetFavoriteAsync(
        Guid bookId,
        bool isFavorite,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            (connection, transaction, _) =>
                SetFavourite(connection, transaction, bookId, isFavorite),
            cancellationToken);

    public Task SaveEstimatedWordCountAsync(
        Guid bookId,
        long estimatedWordCount,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    "UPDATE books SET estimated_word_count = $count WHERE id = $id;");
                command.Parameters.AddWithValue("$count", Math.Max(0, estimatedWordCount));
                command.Parameters.AddWithValue("$id", bookId.ToString("D"));
                command.ExecuteNonQuery();
            },
            cancellationToken);

    public Task SaveReadingPositionAsync(
        ReadingPosition position,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.Anchor is not null && position.Anchor.BookId != position.BookId)
        {
            throw new ArgumentException(
                "The reading-position anchor belongs to another book.",
                nameof(position));
        }

        return ExecuteWriteAsync(
            (connection, transaction, _) =>
                SaveReadingPosition(connection, transaction, position),
            cancellationToken);
    }

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync<IReadOnlyList<Category>>(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT id, name, created_at FROM categories ORDER BY name COLLATE NOCASE;";
                using var reader = command.ExecuteReader();
                var categories = new List<Category>();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    categories.Add(new Category(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        ParseTimestamp(reader.GetString(2))));
                }

                return categories;
            },
            cancellationToken);

    public Task<Category> CreateCategoryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeCategoryName(name);
        var category = new Category(Guid.NewGuid(), normalizedName, DateTimeOffset.UtcNow);

        return ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    """
                    INSERT INTO categories (id, name, created_at)
                    VALUES ($id, $name, $created_at);
                    """);
                command.Parameters.AddWithValue("$id", category.Id.ToString("D"));
                command.Parameters.AddWithValue("$name", category.Name);
                command.Parameters.AddWithValue(
                    "$created_at",
                    FormatTimestamp(category.CreatedAt));
                command.ExecuteNonQuery();
                return category;
            },
            cancellationToken);
    }

    public Task RenameCategoryAsync(
        Guid categoryId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeCategoryName(name);
        return ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    "UPDATE categories SET name = $name WHERE id = $id;");
                command.Parameters.AddWithValue("$name", normalizedName);
                command.Parameters.AddWithValue("$id", categoryId.ToString("D"));
                command.ExecuteNonQuery();
            },
            cancellationToken);
    }

    public Task DeleteCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    "DELETE FROM categories WHERE id = $id;");
                command.Parameters.AddWithValue("$id", categoryId.ToString("D"));
                command.ExecuteNonQuery();
            },
            cancellationToken);

    public Task SetBookCategoriesAsync(
        Guid bookId,
        IEnumerable<Guid> categoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        var distinctIds = categoryIds.Distinct().ToArray();

        return ExecuteWriteAsync(
            (connection, transaction, token) =>
            {
                using (var deleteCommand = CreateCommand(
                           connection,
                           transaction,
                           "DELETE FROM book_categories WHERE book_id = $book_id;"))
                {
                    deleteCommand.Parameters.AddWithValue("$book_id", bookId.ToString("D"));
                    deleteCommand.ExecuteNonQuery();
                }

                foreach (var categoryId in distinctIds)
                {
                    token.ThrowIfCancellationRequested();
                    InsertBookCategory(connection, transaction, bookId, categoryId);
                }
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<Annotation>> GetAnnotationsAsync(
        Guid bookId,
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync<IReadOnlyList<Annotation>>(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT id, kind, anchor_json, created_at, modified_at, text, label, color
                    FROM annotations
                    WHERE book_id = $book_id
                    ORDER BY created_at ASC, id ASC;
                    """;
                command.Parameters.AddWithValue("$book_id", bookId.ToString("D"));

                using var reader = command.ExecuteReader();
                var annotations = new List<Annotation>();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var anchor = InfrastructureJson.DeserializeAnchor(reader.GetString(2))
                        ?? throw new InvalidDataException("An annotation has no anchor.");

                    annotations.Add(new Annotation(
                        Guid.Parse(reader.GetString(0)),
                        bookId,
                        (AnnotationKind)reader.GetInt32(1),
                        anchor,
                        ParseTimestamp(reader.GetString(3)),
                        ParseTimestamp(reader.GetString(4)),
                        GetNullableString(reader, 5),
                        GetNullableString(reader, 6),
                        reader.IsDBNull(7)
                            ? null
                            : (HighlightColor?)reader.GetInt32(7)));
                }

                return annotations;
            },
            cancellationToken);

    public Task SaveAnnotationAsync(
        Annotation annotation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.Anchor.BookId != annotation.BookId)
        {
            throw new ArgumentException(
                "The annotation anchor belongs to another book.",
                nameof(annotation));
        }

        return ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    """
                    INSERT INTO annotations (
                        id, book_id, kind, anchor_json, created_at, modified_at,
                        text, label, color
                    )
                    VALUES (
                        $id, $book_id, $kind, $anchor_json, $created_at, $modified_at,
                        $text, $label, $color
                    )
                    ON CONFLICT(id) DO UPDATE SET
                        book_id = excluded.book_id,
                        kind = excluded.kind,
                        anchor_json = excluded.anchor_json,
                        modified_at = excluded.modified_at,
                        text = excluded.text,
                        label = excluded.label,
                        color = excluded.color;
                    """);
                command.Parameters.AddWithValue("$id", annotation.Id.ToString("D"));
                command.Parameters.AddWithValue("$book_id", annotation.BookId.ToString("D"));
                command.Parameters.AddWithValue("$kind", (int)annotation.Kind);
                command.Parameters.AddWithValue(
                    "$anchor_json",
                    InfrastructureJson.SerializeAnchor(annotation.Anchor));
                command.Parameters.AddWithValue(
                    "$created_at",
                    FormatTimestamp(annotation.CreatedAt));
                command.Parameters.AddWithValue(
                    "$modified_at",
                    FormatTimestamp(annotation.ModifiedAt));
                command.Parameters.AddWithValue("$text", DbValue(annotation.Text));
                command.Parameters.AddWithValue("$label", DbValue(annotation.Label));
                command.Parameters.AddWithValue(
                    "$color",
                    annotation.Color is { } color ? (int)color : DBNull.Value);
                command.ExecuteNonQuery();
            },
            cancellationToken);
    }

    public Task DeleteAnnotationAsync(
        Guid annotationId,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    "DELETE FROM annotations WHERE id = $id;");
                command.Parameters.AddWithValue("$id", annotationId.ToString("D"));
                command.ExecuteNonQuery();
            },
            cancellationToken);

    public Task<ApplicationSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteSerializedAsync(
            (connection, _) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value_json FROM settings WHERE key = $key;";
                command.Parameters.AddWithValue("$key", SettingsKey);
                return command.ExecuteScalar() is string json
                    ? InfrastructureJson.DeserializeSettings(json)
                    : ApplicationSettings.Default;
            },
            cancellationToken);

    public Task SaveSettingsAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ExecuteWriteAsync(
            (connection, transaction, _) =>
            {
                using var command = CreateCommand(
                    connection,
                    transaction,
                    """
                    INSERT INTO settings (key, value_json, updated_at)
                    VALUES ($key, $value_json, $updated_at)
                    ON CONFLICT(key) DO UPDATE SET
                        value_json = excluded.value_json,
                        updated_at = excluded.updated_at;
                    """);
                command.Parameters.AddWithValue("$key", SettingsKey);
                command.Parameters.AddWithValue(
                    "$value_json",
                    InfrastructureJson.SerializeSettings(settings));
                command.Parameters.AddWithValue(
                    "$updated_at",
                    FormatTimestamp(DateTimeOffset.UtcNow));
                command.ExecuteNonQuery();
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _databaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var connection = _connection;
            _connection = null;
            if (connection is not null)
            {
                await Task.Run(connection.Dispose).ConfigureAwait(false);
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<T> ExecuteSerializedAsync<T>(
        Func<SqliteConnection, CancellationToken, T> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = GetInitializedConnection();
            return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return operation(connection, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private Task ExecuteWriteAsync(
        Action<SqliteConnection, SqliteTransaction, CancellationToken> operation,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync<object?>(
            (connection, token) =>
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    operation(connection, transaction, token);
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                    return null;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            },
            cancellationToken);

    private Task<T> ExecuteWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, T> operation,
        CancellationToken cancellationToken) =>
        ExecuteSerializedAsync(
            (connection, token) =>
            {
                using var transaction = connection.BeginTransaction();
                try
                {
                    var value = operation(connection, transaction, token);
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                    return value;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            },
            cancellationToken);

    private SqliteConnection GetInitializedConnection() =>
        _connection
        ?? throw new InvalidOperationException(
            "InitializeAsync must be called before using the repository.");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ApplyMigrations(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """);

        var appliedVersions = new HashSet<int>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT version FROM schema_migrations;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                appliedVersions.Add(reader.GetInt32(0));
            }
        }

        for (var version = 1; version <= CurrentSchemaVersion; version++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (appliedVersions.Contains(version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                switch (version)
                {
                    case 1:
                        ApplyVersionOne(connection, transaction);
                        break;
                    case 2:
                        ApplyVersionTwo(connection, transaction);
                        break;
                    case 3:
                        ApplyVersionThree(connection, transaction);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"No migration is registered for schema version {version}.");
                }

                using var recordMigration = CreateCommand(
                    connection,
                    transaction,
                    """
                    INSERT INTO schema_migrations (version, applied_at)
                    VALUES ($version, $applied_at);
                    """);
                recordMigration.Parameters.AddWithValue("$version", version);
                recordMigration.Parameters.AddWithValue(
                    "$applied_at",
                    FormatTimestamp(DateTimeOffset.UtcNow));
                recordMigration.ExecuteNonQuery();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private static void ApplyVersionOne(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            CREATE TABLE books (
                id TEXT PRIMARY KEY,
                managed_path TEXT NOT NULL UNIQUE,
                format INTEGER NOT NULL,
                content_hash TEXT NOT NULL COLLATE NOCASE UNIQUE,
                metadata_json TEXT NOT NULL,
                embedded_title TEXT NOT NULL,
                embedded_author TEXT NOT NULL,
                title_override TEXT NULL,
                author_override TEXT NULL,
                cover_image BLOB NULL,
                cover_media_type TEXT NULL,
                imported_at TEXT NOT NULL,
                last_opened_at TEXT NULL
            );

            CREATE TABLE categories (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                created_at TEXT NOT NULL
            );

            CREATE TABLE book_categories (
                book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
                PRIMARY KEY (book_id, category_id)
            );

            CREATE TABLE favourites (
                book_id TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
                created_at TEXT NOT NULL
            );

            CREATE TABLE reading_progress (
                book_id TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
                anchor_json TEXT NULL,
                progress REAL NOT NULL,
                chapter_label TEXT NULL,
                page_number INTEGER NULL,
                page_count INTEGER NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE annotations (
                id TEXT PRIMARY KEY,
                book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL,
                anchor_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                text TEXT NULL,
                label TEXT NULL,
                color INTEGER NULL
            );

            CREATE TABLE settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX ix_books_imported_at ON books(imported_at);
            CREATE INDEX ix_books_last_opened_at ON books(last_opened_at);
            CREATE INDEX ix_book_categories_category_id
                ON book_categories(category_id, book_id);
            CREATE INDEX ix_annotations_book_id
                ON annotations(book_id, created_at);
            """);
        command.ExecuteNonQuery();
    }

    private static void ApplyVersionTwo(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            ALTER TABLE reading_progress ADD COLUMN speech_anchor_json TEXT NULL;
            ALTER TABLE reading_progress ADD COLUMN speech_sentence_index INTEGER NULL;
            ALTER TABLE reading_progress ADD COLUMN speech_character_offset INTEGER NULL;
            """);
        command.ExecuteNonQuery();
    }

    private static void ApplyVersionThree(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using (var tableCheck = CreateCommand(
                   connection,
                   transaction,
                   "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'books';"))
        {
            if (tableCheck.ExecuteScalar() is null)
            {
                return;
            }
        }

        using var command = CreateCommand(
            connection,
            transaction,
            "ALTER TABLE books ADD COLUMN estimated_word_count INTEGER NOT NULL DEFAULT 0;");
        command.ExecuteNonQuery();
    }

    private static LibraryBook? ReadBook(
        SqliteConnection connection,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        BookRow? row = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT managed_path, format, content_hash, metadata_json,
                       title_override, author_override, cover_image, cover_media_type,
                       imported_at, last_opened_at, estimated_word_count
                FROM books
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", bookId.ToString("D"));
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                row = new BookRow(
                    reader.GetString(0),
                    (BookFormat)reader.GetInt32(1),
                    reader.GetString(2),
                    InfrastructureJson.DeserializeMetadata(reader.GetString(3)),
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    reader.IsDBNull(6) ? null : (byte[])reader[6],
                    GetNullableString(reader, 7),
                    ParseTimestamp(reader.GetString(8)),
                    reader.IsDBNull(9)
                        ? null
                        : ParseTimestamp(reader.GetString(9)),
                    reader.GetInt64(10));
            }
        }

        if (row is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var isFavourite = false;
        using (var favouriteCommand = connection.CreateCommand())
        {
            favouriteCommand.CommandText =
                "SELECT 1 FROM favourites WHERE book_id = $book_id;";
            favouriteCommand.Parameters.AddWithValue("$book_id", bookId.ToString("D"));
            isFavourite = favouriteCommand.ExecuteScalar() is not null;
        }

        ReadingPosition? position = null;
        using (var progressCommand = connection.CreateCommand())
        {
            progressCommand.CommandText =
                """
                SELECT anchor_json, progress, chapter_label, page_number, page_count,
                       updated_at, speech_anchor_json, speech_sentence_index,
                       speech_character_offset
                FROM reading_progress
                WHERE book_id = $book_id;
                """;
            progressCommand.Parameters.AddWithValue("$book_id", bookId.ToString("D"));
            using var reader = progressCommand.ExecuteReader();
            if (reader.Read())
            {
                var lastSpokenAnchor = reader.IsDBNull(6)
                    ? null
                    : InfrastructureJson.DeserializeAnchor(reader.GetString(6));
                var lastSpokenPosition = lastSpokenAnchor is null
                    || reader.IsDBNull(7)
                    || reader.IsDBNull(8)
                        ? null
                        : new SpeechPosition(
                            lastSpokenAnchor,
                            reader.GetInt32(7),
                            reader.GetInt32(8));
                position = new ReadingPosition(
                    bookId,
                    reader.IsDBNull(0)
                        ? null
                        : InfrastructureJson.DeserializeAnchor(reader.GetString(0)),
                    reader.GetDouble(1),
                    GetNullableString(reader, 2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    ParseTimestamp(reader.GetString(5)),
                    lastSpokenPosition);
            }
        }

        var categories = ImmutableArray.CreateBuilder<Category>();
        using (var categoryCommand = connection.CreateCommand())
        {
            categoryCommand.CommandText =
                """
                SELECT c.id, c.name, c.created_at
                FROM categories c
                INNER JOIN book_categories bc ON bc.category_id = c.id
                WHERE bc.book_id = $book_id
                ORDER BY c.name COLLATE NOCASE;
                """;
            categoryCommand.Parameters.AddWithValue("$book_id", bookId.ToString("D"));
            using var reader = categoryCommand.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                categories.Add(new Category(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    ParseTimestamp(reader.GetString(2))));
            }
        }

        return new LibraryBook(
            bookId,
            row.ManagedPath,
            row.Format,
            row.ContentHash,
            row.Metadata,
            row.TitleOverride,
            row.AuthorOverride,
            row.CoverImage,
            row.CoverMediaType,
            isFavourite,
            position,
            row.ImportedAt,
            row.LastOpenedAt,
            categories.ToImmutable(),
            row.EstimatedWordCount);
    }

    private static void SetFavourite(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid bookId,
        bool isFavourite)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            isFavourite
                ? """
                  INSERT INTO favourites (book_id, created_at)
                  VALUES ($book_id, $created_at)
                  ON CONFLICT(book_id) DO NOTHING;
                  """
                : "DELETE FROM favourites WHERE book_id = $book_id;");
        command.Parameters.AddWithValue("$book_id", bookId.ToString("D"));
        if (isFavourite)
        {
            command.Parameters.AddWithValue(
                "$created_at",
                FormatTimestamp(DateTimeOffset.UtcNow));
        }

        command.ExecuteNonQuery();
    }

    private static void SaveReadingPosition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReadingPosition position)
    {
        using (var command = CreateCommand(
                   connection,
                   transaction,
                   """
                   INSERT INTO reading_progress (
                       book_id, anchor_json, progress, chapter_label,
                       page_number, page_count, updated_at, speech_anchor_json,
                       speech_sentence_index, speech_character_offset
                   )
                   VALUES (
                       $book_id, $anchor_json, $progress, $chapter_label,
                       $page_number, $page_count, $updated_at, $speech_anchor_json,
                       $speech_sentence_index, $speech_character_offset
                   )
                   ON CONFLICT(book_id) DO UPDATE SET
                       anchor_json = excluded.anchor_json,
                       progress = excluded.progress,
                       chapter_label = excluded.chapter_label,
                       page_number = excluded.page_number,
                       page_count = excluded.page_count,
                       updated_at = excluded.updated_at,
                       speech_anchor_json = excluded.speech_anchor_json,
                       speech_sentence_index = excluded.speech_sentence_index,
                       speech_character_offset = excluded.speech_character_offset;
                   """))
        {
            command.Parameters.AddWithValue("$book_id", position.BookId.ToString("D"));
            command.Parameters.AddWithValue(
                "$anchor_json",
                DbValue(position.Anchor is null
                    ? null
                    : InfrastructureJson.SerializeAnchor(position.Anchor)));
            command.Parameters.AddWithValue("$progress", position.ClampedProgress);
            command.Parameters.AddWithValue(
                "$chapter_label",
                DbValue(position.ChapterLabel));
            command.Parameters.AddWithValue(
                "$page_number",
                position.PageNumber is { } pageNumber ? pageNumber : DBNull.Value);
            command.Parameters.AddWithValue(
                "$page_count",
                position.PageCount is { } pageCount ? pageCount : DBNull.Value);
            command.Parameters.AddWithValue(
                "$updated_at",
                FormatTimestamp(position.UpdatedAt));
            command.Parameters.AddWithValue(
                "$speech_anchor_json",
                DbValue(position.LastSpokenPosition is null
                    ? null
                    : InfrastructureJson.SerializeAnchor(
                        position.LastSpokenPosition.Anchor)));
            command.Parameters.AddWithValue(
                "$speech_sentence_index",
                position.LastSpokenPosition is { } speechPosition
                    ? speechPosition.SentenceIndex
                    : DBNull.Value);
            command.Parameters.AddWithValue(
                "$speech_character_offset",
                position.LastSpokenPosition is { } lastSpokenPosition
                    ? lastSpokenPosition.CharacterOffset
                    : DBNull.Value);
            command.ExecuteNonQuery();
        }

        using var openedCommand = CreateCommand(
            connection,
            transaction,
            "UPDATE books SET last_opened_at = $last_opened_at WHERE id = $book_id;");
        openedCommand.Parameters.AddWithValue(
            "$last_opened_at",
            FormatTimestamp(position.UpdatedAt));
        openedCommand.Parameters.AddWithValue("$book_id", position.BookId.ToString("D"));
        openedCommand.ExecuteNonQuery();
    }

    private static void InsertBookCategory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid bookId,
        Guid categoryId)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO book_categories (book_id, category_id)
            VALUES ($book_id, $category_id);
            """);
        command.Parameters.AddWithValue("$book_id", bookId.ToString("D"));
        command.Parameters.AddWithValue("$category_id", categoryId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string? NormalizeOverride(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCategoryName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                "Category names cannot exceed 128 characters.");
        }

        return normalized;
    }

    private sealed record BookRow(
        string ManagedPath,
        BookFormat Format,
        string ContentHash,
        BookMetadata Metadata,
        string? TitleOverride,
        string? AuthorOverride,
        byte[]? CoverImage,
        string? CoverMediaType,
        DateTimeOffset ImportedAt,
        DateTimeOffset? LastOpenedAt,
        long EstimatedWordCount);
}
