# Architecture

OpenShelf separates untrusted format parsing, application state, and native UI
rendering into four projects.

## Projects

- `OpenShelf.Core` defines immutable document, annotation, library, import,
  resource, and speech contracts.
- `OpenShelf.Formats` probes signatures and converts each supported format into
  a `BookDocument`. XHTML is parsed as data and normalized immediately; no
  browser renderer is involved.
- `OpenShelf.Infrastructure` owns atomic managed imports, SHA-256 duplicate
  detection, SQLite persistence, resource resolution/cache policy, and the
  eSpeak NG process boundary.
- `OpenShelf.App` composes the services and supplies the Avalonia library and
  reader experience.

## Trust boundary

Every adapter receives a bounded `BookImportRequest`, honors cancellation, and
returns structured failures. Archive names are decoded and canonicalized before
use. Resource lookup rejects absolute paths, traversal, remote schemes, and
payloads above configured limits. XML readers disable DTDs and external
entities. Imported source files are opened read-only, copied to a temporary
managed path, and atomically promoted only after parsing and persistence
succeed.

## Stable locations

Reflowable annotations store section, block, and character offsets together
with surrounding quote context. Fixed-page annotations store page/range and
rectangles. Reader layout changes therefore do not redefine the annotation's
logical target.

## Progress

Reflowable progress is a normalized content position divided by the document's
normalized length. Fixed-page progress combines page index and within-page
position. UI surfaces clamp the value to 0–100 and label the edge states
`Unread` and `Finished`.
