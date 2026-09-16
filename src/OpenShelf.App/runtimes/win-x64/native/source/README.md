# libmobi runtime

OpenShelf uses libmobi 0.12 for DRM-free MOBI and AZW3 decompression. The
Windows release bundles `libmobi.dll` under
`runtimes/win-x64/native/`. The library is built from the unmodified upstream
C sources with the bundled miniz implementation and without DRM/encryption
support.

- Upstream: <https://github.com/bfabiszewski/libmobi>
- Source tag: `v0.12`
- Source commit: `85dcfe803fc2a21020ddcf15c3eb66b93d388add`
- License: LGPL-3.0-or-later

The DLL is dynamically loaded at runtime. A copy of the upstream license is
shipped beside it. Rebuild instructions are in `build/Build-LibMobi.ps1`.
