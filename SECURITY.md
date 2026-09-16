# Security policy

OpenShelf Reader treats every imported book as untrusted input.

- The application reads only files or folders explicitly selected by the user.
- Imported files are copied to managed application storage; originals are not
  modified or removed.
- Network resources, scripts, forms, XML external entities, embedded
  executables, and paths escaping a book archive are rejected.
- Resource sizes, archive expansion, XML depth, entry counts, and decoded
  image dimensions are bounded.
- Passwords used to open PDFs are held only for the current open operation and
  are not persisted.
- Adobe, Kindle, LCP, and other vendor DRM is not bypassed.

Do not attach copyrighted books to a public issue. A diagnostics report should
contain application versions and error details, never book contents.
