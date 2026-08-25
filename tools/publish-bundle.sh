#!/bin/sh
# Single-file distributable: self-contained Superserver + WASM client + the
# study/warm-up datasets in one self-extracting executable per platform.
set -e
cd "$(dirname "$0")/.."

rids="win-x64 osx-arm64 linux-x64"
[ $# -gt 0 ] && rids="$*"

for rid in $rids; do
    echo "=== publishing $rid ==="
    dotnet publish src/Superserver/Superserver.fsproj -c Release -r "$rid" \
        -p:Bundle=true -o "publish/tmp-$rid"
    case "$rid" in
        win-*)
            cp "publish/tmp-$rid/Superserver.exe" "publish/Superprojekt-$rid.exe"
            ;;
        *)
            # tar.gz so the executable bit survives transfer
            cp "publish/tmp-$rid/Superserver" "publish/Superprojekt-$rid"
            chmod +x "publish/Superprojekt-$rid"
            tar -czf "publish/Superprojekt-$rid.tar.gz" -C publish "Superprojekt-$rid"
            ;;
    esac
done

echo "=== artifacts ==="
ls -lh publish/Superprojekt-*
