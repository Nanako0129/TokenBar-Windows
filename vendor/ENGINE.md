# Shared Rust engine consumer

TokenBar for Windows consumes the public
[`Nanako0129/tokscale-core`](https://github.com/Nanako0129/tokscale-core)
repository through an immutable Git submodule. Shared parser, scanner, cache,
pricing, and aggregation changes land in the engine repository before either
app consumer advances its reviewed pin.

| Field | Value |
|---|---|
| Path | `vendor/tokscale-core` |
| Repository | `https://github.com/Nanako0129/tokscale-core.git` |
| Reviewed pin | `b31e39425859393504a2d56cb5af7c93e6461c7d` |
| Native consumer baseline | `704426e8df9acfb8e82fe4bf3b7ed3e5adbc2fea` |
| Windows pre-migration baseline | `68e2541c5e9adb14a47433f8b25e26b0be84d1fc` |
| Upstream and local-patch ledger | Immutable [`UPSTREAM.md`](https://github.com/Nanako0129/tokscale-core/blob/b31e39425859393504a2d56cb5af7c93e6461c7d/UPSTREAM.md) |

> **Warning:** Do not edit shared source on a consumer branch. Engine changes
> must pass review in `tokscale-core`; this repository then advances only the
> reviewed gitlink and runs the Windows consumer gates.

## Ownership

| Owner | Surface |
|---|---|
| `tokscale-core` | Shared Rust source, tests, standalone lock and CI, upstream baseline, and local-patch ledger |
| TokenBar for Windows | Gitlink, root `Cargo.lock`, `crates/tb_core_ffi`, C header, C# bridge, application, and packaging wiring |

## Checkout

Clone recursively:

```bash
git clone --recurse-submodules https://github.com/Nanako0129/TokenBar-Windows.git
```

Initialize an existing checkout before building:

```bash
git submodule update --init --recursive
```

The submodule must be clean, and its checked-out `HEAD` must equal the
superproject gitlink.

## Historical sync boundary

The final manual copy came from Native commit
[`729dc3adf21cc31e16ef0b8b742f0244197d7058`](https://github.com/Nanako0129/TokenBar/commit/729dc3adf21cc31e16ef0b8b742f0244197d7058)
and reached Windows baseline `68e2541c5e9adb14a47433f8b25e26b0be84d1fc`.
At that checkpoint, all 63 shared files were byte-identical after excluding
the former Windows-only `SYNC.md`; Windows carried no shared-tree local patch.

The public engine preserves that source history and the complete authoritative
ledger. This document replaces the former `vendor/tokscale-core/SYNC.md` and
the duplicated vendor ledger; the manual-copy procedure is retired.
