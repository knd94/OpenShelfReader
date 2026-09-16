using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using OpenShelf.Core;
using OpenShelf.Infrastructure;
using Xunit;

namespace OpenShelf.Tests.Infrastructure;

public sealed class SqliteLibraryRepositoryTests
{
    [Fact]
    public async Task RoundTripsLibraryRelationsAnnotationsAndSettings()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var paths = new PlatformAppDataPaths(temporaryDirectory.Path);
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);

        var bookId = Guid.NewGuid();
        var managedPath = System.IO.Path.Combine(paths.LibraryDirectory, "book.epub");
        var importedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var book = new LibraryBook(
            bookId,
            managedPath,
            BookFormat.Epub,
            "ABCDEF1234",
            new BookMetadata("Embedded title", "Embedded author", Language: "en"),
            TitleOverride: null,
            AuthorOverride: null,
            CoverImage: [1, 2, 3],
            CoverMediaType: "image/png",
            IsFavorite: false,
            ReadingPosition: null,
            importedAt,
            LastOpenedAt: null,
            ImmutableArray<Category>.Empty);
        await repository.AddBookAsync(book, TestContext.Current.CancellationToken);

        var category = await repository.CreateCategoryAsync(
            "Research",
            TestContext.Current.CancellationToken);
        await repository.SetBookCategoriesAsync(
            bookId,
            [category.Id],
            TestContext.Current.CancellationToken);
        await repository.SetFavoriteAsync(
            bookId,
            true,
            TestContext.Current.CancellationToken);
        await repository.UpdateMetadataAsync(
            bookId,
            "Edited title",
            "Edited author",
            TestContext.Current.CancellationToken);

        var anchor = new ReflowableAnchor(
            bookId,
            "ABCDEF1234",
            "chapter",
            "paragraph",
            4,
            18,
            "quote");
        var position = new ReadingPosition(
            bookId,
            anchor,
            0.42,
            "Chapter 3",
            12,
            100,
            DateTimeOffset.UtcNow,
            new SpeechPosition(
                anchor with
                {
                    StartOffset = 38,
                    EndOffset = 38,
                    ExactQuote = null
                },
                SentenceIndex: 7,
                CharacterOffset: 38));
        await repository.SaveReadingPositionAsync(
            position,
            TestContext.Current.CancellationToken);

        var annotation = new Annotation(
            Guid.NewGuid(),
            bookId,
            AnnotationKind.Note,
            anchor,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            Text: "Remember this",
            Label: "Evidence",
            Color: HighlightColor.Blue);
        await repository.SaveAnnotationAsync(
            annotation,
            TestContext.Current.CancellationToken);

        var settings = ApplicationSettings.Default with
        {
            LibraryTheme = LibraryTheme.Light,
            SpeechRate = 205,
            Reader = ReaderPreferences.Default with
            {
                Theme = ReaderTheme.Sepia,
                FontSize = 24
            }
        };
        await repository.SaveSettingsAsync(
            settings,
            TestContext.Current.CancellationToken);

        var loaded = await repository.GetBookAsync(
            bookId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal("Edited title", loaded.DisplayTitle);
        Assert.Equal("Edited author", loaded.DisplayAuthor);
        Assert.True(loaded.IsFavorite);
        Assert.Equal(position with { Progress = position.ClampedProgress }, loaded.ReadingPosition);
        Assert.Equal(category, Assert.Single(loaded.Categories));
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.CoverImage);

        var annotations = await repository.GetAnnotationsAsync(
            bookId,
            TestContext.Current.CancellationToken);
        Assert.Equal(annotation, Assert.Single(annotations));
        Assert.Equal(
            settings,
            await repository.GetSettingsAsync(TestContext.Current.CancellationToken));

        var queryResult = await repository.QueryBooksAsync(
            new LibraryQuery(
                Search: "edited",
                CategoryId: category.Id,
                FavoritesOnly: true,
                Format: BookFormat.Epub),
            TestContext.Current.CancellationToken);
        Assert.Equal(bookId, Assert.Single(queryResult).Id);
        Assert.Equal(
            bookId,
            (await repository.FindByHashAsync(
                "abcdef1234",
                TestContext.Current.CancellationToken))?.Id);
        Assert.True(File.Exists(paths.DatabasePath));
    }

    [Fact]
    public async Task VersionTwoMigrationPreservesLegacyProgressWithNoSpeechPosition()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var paths = new PlatformAppDataPaths(temporaryDirectory.Path);
        paths.EnsureCreated();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath
        }.ToString();

        await using (var legacyConnection = new SqliteConnection(connectionString))
        {
            await legacyConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = legacyConnection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                INSERT INTO schema_migrations (version, applied_at)
                VALUES (1, '2026-01-01T00:00:00.0000000+00:00');

                CREATE TABLE reading_progress (
                    book_id TEXT PRIMARY KEY,
                    anchor_json TEXT NULL,
                    progress REAL NOT NULL,
                    chapter_label TEXT NULL,
                    page_number INTEGER NULL,
                    page_count INTEGER NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO reading_progress (
                    book_id, anchor_json, progress, chapter_label,
                    page_number, page_count, updated_at
                )
                VALUES (
                    'legacy-book', NULL, 0.5, 'Legacy chapter',
                    NULL, NULL, '2026-01-01T00:00:00.0000000+00:00'
                );
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var repository = new SqliteLibraryRepository(paths))
        {
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
        }

        await using var verificationConnection = new SqliteConnection(connectionString);
        await verificationConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var verification = verificationConnection.CreateCommand();
        verification.CommandText =
            """
            SELECT speech_anchor_json, speech_sentence_index, speech_character_offset
            FROM reading_progress
            WHERE book_id = 'legacy-book';
            """;
        await using (var reader = await verification.ExecuteReaderAsync(
                         TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
        }

        verification.CommandText =
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 2;";
        Assert.Equal(
            1L,
            (long)(await verification.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task DeletingCategoryAndAnnotationCascadesLibraryState()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var paths = new PlatformAppDataPaths(temporaryDirectory.Path);
        await using var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);

        var book = CreateBook(paths.LibraryDirectory);
        await repository.AddBookAsync(book, TestContext.Current.CancellationToken);
        var category = await repository.CreateCategoryAsync(
            "Temporary",
            TestContext.Current.CancellationToken);
        await repository.SetBookCategoriesAsync(
            book.Id,
            [category.Id],
            TestContext.Current.CancellationToken);
        var annotation = new Annotation(
            Guid.NewGuid(),
            book.Id,
            AnnotationKind.Bookmark,
            new ReflowableAnchor(
                book.Id,
                book.ContentHash,
                "s",
                "b",
                0,
                0),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await repository.SaveAnnotationAsync(
            annotation,
            TestContext.Current.CancellationToken);

        await repository.DeleteCategoryAsync(
            category.Id,
            TestContext.Current.CancellationToken);
        await repository.DeleteAnnotationAsync(
            annotation.Id,
            TestContext.Current.CancellationToken);

        Assert.Empty((await repository.GetBookAsync(
            book.Id,
            TestContext.Current.CancellationToken))!.Categories);
        Assert.Empty(await repository.GetAnnotationsAsync(
            book.Id,
            TestContext.Current.CancellationToken));
    }

    private static LibraryBook CreateBook(string libraryDirectory)
    {
        var id = Guid.NewGuid();
        return new LibraryBook(
            id,
            System.IO.Path.Combine(libraryDirectory, $"{id:N}.txt"),
            BookFormat.Txt,
            Guid.NewGuid().ToString("N"),
            new BookMetadata("Title", "Author"),
            null,
            null,
            null,
            null,
            false,
            null,
            DateTimeOffset.UtcNow,
            null,
            ImmutableArray<Category>.Empty);
    }
}
