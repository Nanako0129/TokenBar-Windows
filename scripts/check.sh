#!/usr/bin/env bash
# macOS-side inner loop: everything here runs without a Windows machine.
# The dotnet tests load the release dylib (copied by src/Directory.Build.targets),
# so the cargo release build must come first.
#
# Optional: WIN_CHECK=1 also type-checks the Windows target; install it with
# `rustup target add x86_64-pc-windows-msvc`.
set -euo pipefail
cd "$(dirname "$0")/.."

cargo check --workspace --locked
cargo test --workspace --locked
cargo build --release --locked

if [[ "${WIN_CHECK:-0}" == "1" ]]; then
  cargo check --workspace --locked --target x86_64-pc-windows-msvc
fi

dotnet restore src/TokenBar.slnx --locked-mode
dotnet build src/TokenBar.slnx --no-restore
dotnet test src/TokenBar.slnx --no-build
dotnet run --project src/TokenBar.Smoke --no-build
