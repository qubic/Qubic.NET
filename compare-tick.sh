#!/usr/bin/env bash
#
# Compare the same tick as seen by two different peers. Dumps tick data,
# tx wire bytes (qubic-cli compatible too), and parsed JSON for each peer
# under data/{tick}_src_{src_ip}/ and data/{tick}_dst_{dst_ip}/ so they can
# be diffed side by side. data/ is .gitignore'd.
#
# Usage: ./compare-tick.sh <tick> <src-ip[:port]> <dst-ip[:port]>
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <tick> <src-ip[:port]> <dst-ip[:port]>" >&2
  echo "example: $0 56118364 10.10.10.1 10.10.10.2" >&2
  exit 1
fi

tick="$1"
src_peer="$2"
dst_peer="$3"

if [[ ! "$tick" =~ ^[0-9]+$ ]]; then
  echo "error: tick must be a positive integer, got '$tick'" >&2
  exit 1
fi

# Resolve to the script's directory so `tools/...` resolves correctly
# regardless of where the script is invoked from.
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir"
mkdir -p data

run_one() {
  local peer="$1"
  local role="$2"
  # Sanitize the peer for use in a path (replace ':' with '_' for ip:port).
  local safe="${peer//:/_}"
  local dump_dir="data/${tick}_${role}_${safe}"

  echo
  echo "═══════════════════════════════════════════════════════════════"
  echo "  ${role^^}  peer=$peer  tick=$tick  →  $dump_dir/"
  echo "═══════════════════════════════════════════════════════════════"

  dotnet run --project tools/Qubic.ChainAnalytics.Cli -- \
    "$peer" "$tick" \
    --dump-dir "$dump_dir" \
    --verify-signature \
    --tx-range none
}

run_one "$src_peer" src
run_one "$dst_peer" dst

src_dir="data/${tick}_src_${src_peer//:/_}"
dst_dir="data/${tick}_dst_${dst_peer//:/_}"

echo
echo "Done. Suggested diffs:"
echo "  diff <(xxd $src_dir/tick-*-tickdata.bin) <(xxd $dst_dir/tick-*-tickdata.bin)"
echo "  diff $src_dir/tick-*.json $dst_dir/tick-*.json"
