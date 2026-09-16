# Third-party notices

OpenShelf Reader is licensed under GPL-3.0-or-later. It uses the following
open-source projects. Their licenses and copyright notices remain applicable.

| Component | Purpose | License |
| --- | --- | --- |
| Avalonia | Native cross-platform user interface | MIT |
| Microsoft.Data.Sqlite / SQLitePCLRaw / SQLite | Local library database | MIT / Apache-2.0 / Public Domain |
| VersOne.Epub | EPUB package and metadata parsing | The Unlicense |
| AngleSharp | Parsing book-supplied XHTML | MIT |
| PDFtoImage / PDFium / SkiaSharp | PDF page rendering | MIT / BSD-style / MIT |
| Svg.Skia | Sandboxed SVG rasterization for book artwork | MIT |
| PdfPig | PDF metadata and selectable text geometry | Apache-2.0 |
| libmobi | MOBI and AZW3 parsing | LGPL-3.0-or-later |
| RtfPipe | RTF parsing | MIT |
| DocumentFormat.OpenXml | DOCX parsing | MIT |
| eSpeak NG | Offline text-to-speech | GPL-3.0-or-later |
| xUnit.net | Automated tests | Apache-2.0 |

Binary distributions must retain the license files shipped by PDFium,
libmobi, eSpeak NG, SQLite, and their transitive native dependencies. When
GPL/LGPL native binaries are bundled, the corresponding source and build
instructions must be offered alongside the release.
