# OpenShelf Reader

OpenShelf Reader is a local-first, native desktop eBook library and reader for
Windows, macOS, and Linux. It is built with C#/.NET 10 and Avalonia controls;
book markup is converted to native document objects and is never rendered in a
browser or WebView.

## Highlights

- Imports EPUB, PDF, MOBI, AZW3, FB2, TXT, RTF, and DOCX files.
- Imports individual files or recursively scans an explicitly selected folder.
- Copies imported books into managed application storage without changing the
  originals, and detects exact duplicates with SHA-256.
- Presents a responsive dark library with real or generated covers, favourites,
  categories, progress, search, filtering, and sorting.
- Includes native scrolling and paged reading, table-of-contents navigation,
  selectable text, clipboard copying, highlights, notes, and bookmarks.
- Restores per-book reading progress and displays it on the cover card, in the
  reader footer, and in the window title.
- Supports embedded EPUB images, safe book-relative resource lookup, bounded
  image caching, damaged-image placeholders, fullscreen (`F11`), and offline
  eSpeak NG text-to-speech. Double-clicking a sentence starts speech there,
  and the spoken position is restored separately for every book. Spoken-text
  highlighting and automatic sentence/page following can each be switched off;
  Stop ends speech immediately without closing the book.
- Provides native System, Reading, Text to speech, and Hotkeys settings pages.
  Preferences remain local and include no telemetry or paid feature tier.
- Stores the library, settings, progress, and annotations locally in SQLite.

OpenShelf does not bypass DRM. Adobe/Kindle DRM and KFX are rejected with a
clear explanation. Password-protected PDFs prompt for a password and never
store it.

## Run from source

Install the .NET 10 SDK, then run:

```powershell
dotnet restore OpenShelfReader.slnx
dotnet run --project src/OpenShelf.App/OpenShelf.App.csproj
```

The application only reads books or folders explicitly selected through native
pickers. Its database and managed book copies are stored in the operating
system's per-user application-data location.

## Build and package

Create a self-contained Windows x64 build:

```powershell
.\build\Publish.ps1 -Runtime win-x64
```

The Windows output is one self-contained executable at
`artifacts\publish\win-x64\OpenShelfReader.exe`. Its .NET runtime, native
libraries, eSpeak engine, voices, and other runtime data are bundled into that
single file and extracted to a private runtime directory when needed. A
distributable archive is written below `artifacts\packages` together with its
SHA-256 checksum. Windows uses `.zip`; macOS and Linux use `.tar.gz` so
executable permissions are preserved.

Supported publish runtime identifiers are `win-x64`, `osx-x64`, `osx-arm64`,
and `linux-x64`.

## Test

```powershell
dotnet test OpenShelfReader.slnx --configuration Release
```

Tests use generated fixtures and isolated temporary application-data
directories; they do not read an existing personal library.

## Security and privacy

See [SECURITY.md](SECURITY.md) for parser boundaries and supported-book
reporting guidance. OpenShelf blocks scripts, forms, remote book resources,
external XML entities, archive traversal, embedded executables, and
unreasonably large expanded resources.

## License

OpenShelf Reader is licensed under the GNU General Public License v3.0.
Third-party components retain their respective licenses; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
