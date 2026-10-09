# sqlite-writer-bench

Public harness: https://github.com/esbencarlsen/sqlite-writer-bench

Compare concurrent **read/write** performance of:

| Engine | How it is wired |
|---|---|
| **SQLite** | `Microsoft.Data.Sqlite`, `journal_mode=DELETE` |
| **SQLite WAL** | `Microsoft.Data.Sqlite`, `journal_mode=WAL` |
| **Turso MVCC** | `Turso.Data.Sqlite.Provider`, `journal_mode='mvcc'` + `BEGIN CONCURRENT` |
| **sqlite-multiwriter** | Stock SQLite + [sqliteai/sqlite-multiwriter](https://github.com/sqliteai/sqlite-multiwriter) VFS (`vfs=multiwriter&mw_rebase=1`) |

Inspired by [Marco Bambini’s Substack post](https://marcobambini.substack.com/p/we-solved-sqlites-single-writer-limitation).

The repo produces:

1. JSON metrics under `results/`
2. A Substack-pasteable draft at [`article/substack-draft.md`](article/substack-draft.md)

## Prerequisites

- .NET 11 SDK
- Linux or macOS (x86_64 or arm64) for the multiwriter native extension
- `curl`, `python3`, `tar` (for `scripts/fetch-multiwriter.sh`)

## Setup

```bash
./scripts/fetch-multiwriter.sh
```

This downloads the latest `multiwriter.so` / `.dylib` into `native/`. Override with `MULTIWRITER_EXT=/path/to/multiwriter.so` if needed.

## Run benchmarks

```bash
# Single profile (durable disk + FULL by default)
dotnet run --project src/SqliteWriterBench -c Release -- run

# Full article matrix: durable FULL + tmpfs FULL + disk OFF
dotnet run --project src/SqliteWriterBench -c Release -- run --matrix \
  --duration 8 \
  --writers 1,4,8,12,16,20,24
```

Defaults: 8s per cell, writer counts `1,4,16`, 4 readers, 100 inserts/tx, `synchronous=FULL`.

Database files for the durable profile go under on-disk `tmp/` (not `/tmp`). `/tmp` is tmpfs here; with `synchronous=FULL` it can show ~30× higher write tx/s than durable storage and is not crash-safe.

Useful flags:

```bash
dotnet run --project src/SqliteWriterBench -c Release -- run \
  --duration 8 \
  --writers 1,4,16 \
  --readers 4 \
  --rows 100 \
  --sync FULL \
  --engine WAL \
  --workdir ./tmp
```

Outputs:

- `results/latest.json`
- `results/bench-<timestamp>.json`
- regenerates `article/substack-draft.md`

## Render article only

```bash
dotnet run --project src/SqliteWriterBench -- render
```

Rebuilds the Substack draft from `results/latest.json` without re-running benches.

## Paste into Substack

1. Open [`article/substack-draft.md`](article/substack-draft.md)
2. New Substack post → paste the markdown
3. Tweak title/subtitle if you want; tables paste cleanly as markdown

## Workload notes

- Each writer thread uses its **own connection** and inserts **its own rows** (low true conflicts).
- Readers concurrently run `COUNT(*)` and grouped range scans against the same file.
- Turso is a separate engine (not a SQLite VFS). sqlite-multiwriter is a VFS loaded into stock SQLite.
- Hot-row conflict matrices and multi-process (`mw_mp=1`) mode are intentionally out of scope for v1.

## License

This project is licensed under the [MIT License](LICENSE). Upstream engines keep their own licenses (SQLite public domain, Turso, Apache-2.0 multiwriter).
