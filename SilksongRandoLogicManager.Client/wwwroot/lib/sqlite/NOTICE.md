# Official SQLite WASM 3.54.1

Unmodified files extracted from https://sqlite.org/2026/sqlite-wasm-3540100.zip.

Archive SHA3-256 (matches CACHE1_C_CONTRACT):
`87e24f6a4588692310b7942dc2fa7c7275bb9ba2e01d553e4115aa4a904a0050`

Archive SHA-256:
`85ebce90b6d189442b29e11d193337b5e63203aae9b6590dbe89a601b9891da2`

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| sqlite3.mjs | 438757 | 7f66120f5bb79d0646af88f5c27510ed26d731e801d96e4024d24878dec5a86a |
| sqlite3.wasm | 879430 | 10fd727b37924755edc12c2f3ead0d54155d8f2d9f2abf6a1ef9b2b2597e0a51 |
| README.upstream.txt (archive README.txt) | 1890 | c2e654b60b753dcbbf4d52f30138afd8b5eef42aef42bccdd32fc94a1776674b |

SQLite source ID:
`2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6ca76`.
Built upstream with Emscripten 5.0.1; the preserved module header identifies both
SQLite public-domain code and Emscripten's MIT / Illinois-NCSA glue licenses.
`EMSCRIPTEN-LICENSE.txt` preserves the Emscripten 5.0.1 license and its musl notice.
Sources:

- https://raw.githubusercontent.com/emscripten-core/emscripten/5.0.1/LICENSE
- https://raw.githubusercontent.com/emscripten-core/emscripten/5.0.1/system/lib/libc/musl/COPYRIGHT
- https://sqlite.org/copyright.html

SQLite's blessing, retained in the unmodified module:

> The author disclaims copyright to this source code. In place of a legal notice,
> here is a blessing:
> May you do good and not evil.
> May you find forgiveness for yourself and forgive others.
> May you share freely, never taking more than you give.

The application uses the official **opfs-sahpool** VFS in a dedicated worker.
The generic OPFS/SAB header instructions in the unmodified upstream README do not
impose COOP/COEP on this selected SAH-pool VFS. No demos, tests, Worker1 wrapper,
async OPFS proxy or third-party coordination helper are vendored. Application
coordination is independently implemented, not part of this vendor bundle.
