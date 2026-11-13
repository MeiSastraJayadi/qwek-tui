#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/Qwek.csproj"
OUT_DIR="${1:-$ROOT/dist}"
RIDS=(win-x64 win-arm64 linux-x64 linux-arm64)
DEFAULT_BACKEND_URL="${QWEK_DEFAULT_BACKEND_URL:-}"

if [[ -z "${VERSION:-}" ]]; then
    VERSION="$(git -C "$ROOT" describe --tags --always 2>/dev/null || true)"
fi
VERSION="${VERSION#v}"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+ ]]; then
    VERSION="0.0.0"
fi

mkdir -p "$OUT_DIR"
rm -f "$OUT_DIR"/qwek-*.zip "$OUT_DIR"/qwek-*.tar.gz "$OUT_DIR"/qwek-checksums.txt
rm -rf "$OUT_DIR/.work"

publish() {
    local rid="$1" work="$2"
    local backend_arg=""

    if [[ -n "$DEFAULT_BACKEND_URL" ]]; then
        backend_arg="-p:DefaultBackendUrl=$DEFAULT_BACKEND_URL"
    fi

    dotnet publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=None \
        -p:DebugSymbols=false \
        -p:AssemblyName=qwek \
        ${backend_arg:+"$backend_arg"} \
        -p:Version="$VERSION" \
        -o "$work"
}

create_zip() {
    local dir="$1" dest="$2" file="$3"

    if command -v zip >/dev/null 2>&1; then
        (cd "$dir" && zip -q "$dest" "$file")
        return
    fi

    if command -v powershell.exe >/dev/null 2>&1 && command -v cygpath >/dev/null 2>&1; then
        QWEK_ZIP_SOURCE="$(cygpath -w "$dir/$file")" \
            QWEK_ZIP_DEST="$(cygpath -w "$dest")" \
            powershell.exe -NoProfile -Command \
            'Compress-Archive -LiteralPath $env:QWEK_ZIP_SOURCE -DestinationPath $env:QWEK_ZIP_DEST -Force'
        return
    fi

    if tar -a -cf "$dest" -C "$dir" "$file" 2>/dev/null; then
        return
    fi

    echo "error: no zip tool found (install zip or run from PowerShell)" >&2
    exit 1
}

for rid in "${RIDS[@]}"; do
    work="$OUT_DIR/.work/$rid"
    echo "==> publishing $rid"
    publish "$rid" "$work"

    if [[ "$rid" == win-* ]]; then
        create_zip "$work" "$OUT_DIR/qwek-$rid.zip" "qwek.exe"
    else
        (cd "$work" && tar --mode=755 -czf "../../qwek-$rid.tar.gz" "qwek")
    fi
done

if command -v sha256sum >/dev/null 2>&1; then
    (cd "$OUT_DIR" && sha256sum qwek-*.zip qwek-*.tar.gz > qwek-checksums.txt)
elif command -v shasum >/dev/null 2>&1; then
    (cd "$OUT_DIR" && shasum -a 256 qwek-*.zip qwek-*.tar.gz > qwek-checksums.txt)
elif command -v openssl >/dev/null 2>&1; then
    (
        cd "$OUT_DIR"
        for file in qwek-*.zip qwek-*.tar.gz; do
            printf '%s  %s\n' "$(openssl dgst -sha256 "$file" | awk '{print $NF}')" "$file"
        done > qwek-checksums.txt
    )
else
    echo "error: no SHA-256 tool found" >&2
    exit 1
fi

rm -rf "$OUT_DIR/.work"

echo
echo "Built Qwek TUI $VERSION packages in $OUT_DIR:"
ls -lh "$OUT_DIR"/qwek-*.zip "$OUT_DIR"/qwek-*.tar.gz "$OUT_DIR/qwek-checksums.txt" |
    awk '{ printf "  %s (%s)\n", $9, $5 }'
