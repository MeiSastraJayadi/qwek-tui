#!/usr/bin/env bash
set -euo pipefail

REPO="MeiSastraJayadi/qwek-tui"
APP="qwek"
VERSION=""
PREFIX=""
FROM_SOURCE=0
UNINSTALL=0

usage() {
    cat <<'EOF'
Install Qwek TUI on Linux.

Usage:
  ./install.sh [options]

Options:
  --version <tag>    Install a specific release (for example v1.2.0)
  --prefix <dir>     Install prefix (default: /usr/local when writable,
                     otherwise ~/.local)
  --from-source      Build and install from source with the .NET 10 SDK
  --uninstall        Remove the installed qwek binary
  -h, --help         Show this help

Environment:
  QWEK_RELEASE_BASE  Override the release download base URL

Examples:
  curl -fsSL https://raw.githubusercontent.com/MeiSastraJayadi/qwek-tui/master/install.sh | bash
  ./install.sh --prefix "$HOME/.local"
EOF
}

info() {
    printf '==> %s\n' "$*"
}

err() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

while (($#)); do
    case "$1" in
        --version)
            [[ $# -ge 2 ]] || err "missing value for --version"
            VERSION="$2"
            shift 2
            ;;
        --version=*)
            VERSION="${1#*=}"
            shift
            ;;
        --prefix)
            [[ $# -ge 2 ]] || err "missing value for --prefix"
            PREFIX="$2"
            shift 2
            ;;
        --prefix=*)
            PREFIX="${1#*=}"
            shift
            ;;
        --from-source)
            FROM_SOURCE=1
            shift
            ;;
        --uninstall)
            UNINSTALL=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            err "unknown option: $1 (try --help)"
            ;;
    esac
done

[[ "$(uname -s)" == "Linux" ]] ||
    err "this installer is for Linux; on Windows run install.ps1 in PowerShell"

case "$(uname -m)" in
    x86_64|amd64) RID_ARCH="x64" ;;
    aarch64|arm64) RID_ARCH="arm64" ;;
    *) err "unsupported CPU architecture: $(uname -m)" ;;
esac

if [[ -z "$PREFIX" ]]; then
    if [[ -w /usr/local/bin ]]; then
        PREFIX="/usr/local"
    else
        PREFIX="$HOME/.local"
    fi
fi

BIN_DIR="$PREFIX/bin"
BINARY="$BIN_DIR/$APP"

if ((UNINSTALL)); then
    if [[ -e "$BINARY" ]]; then
        rm -f "$BINARY"
        info "removed $BINARY"
    else
        info "$APP is not installed at $BINARY"
    fi
    exit 0
fi

have() {
    command -v "$1" >/dev/null 2>&1
}

download() {
    local url="$1" dest="$2"

    if have curl; then
        curl -fL --retry 3 --retry-delay 2 --connect-timeout 15 -o "$dest" "$url"
    elif have wget; then
        wget --tries=3 --timeout=15 -O "$dest" "$url"
    else
        err "curl or wget is required to download Qwek"
    fi
}

sha256_file() {
    if have sha256sum; then
        sha256sum "$1" | awk '{print $1}'
    elif have shasum; then
        shasum -a 256 "$1" | awk '{print $1}'
    elif have openssl; then
        openssl dgst -sha256 "$1" | awk '{print $NF}'
    else
        return 1
    fi
}

download_first() {
    local dest="$1"
    shift
    local url

    for url in "$@"; do
        info "downloading $url"
        if download "$url" "$dest"; then
            return 0
        fi
        rm -f "$dest"
    done

    return 1
}

release_urls() {
    local asset="$1"

    if [[ -n "${QWEK_RELEASE_BASE:-}" ]]; then
        printf '%s/%s\n' "${QWEK_RELEASE_BASE%/}" "$asset"
    elif [[ -n "$VERSION" ]]; then
        printf 'https://github.com/%s/releases/download/%s/%s\n' "$REPO" "$VERSION" "$asset"
        if [[ "$VERSION" != v* ]]; then
            printf 'https://github.com/%s/releases/download/v%s/%s\n' "$REPO" "$VERSION" "$asset"
        fi
    else
        printf 'https://github.com/%s/releases/latest/download/%s\n' "$REPO" "$asset"
    fi
}

source_urls() {
    if [[ -n "$VERSION" ]]; then
        printf 'https://github.com/%s/archive/refs/tags/%s.tar.gz\n' "$REPO" "$VERSION"
        if [[ "$VERSION" != v* ]]; then
            printf 'https://github.com/%s/archive/refs/tags/v%s.tar.gz\n' "$REPO" "$VERSION"
        fi
    else
        printf 'https://github.com/%s/archive/refs/heads/master.tar.gz\n' "$REPO"
    fi
}

verify_checksum() {
    local archive="$1" asset="$2" checksums="$3"
    local expected actual

    if [[ ! -s "$checksums" ]]; then
        info "checksum file unavailable, skipping verification"
        return 0
    fi

    expected="$(awk -v name="$asset" '$2 == name || $2 == "*" name { print $1; exit }' "$checksums")"
    if [[ -z "$expected" ]]; then
        info "no checksum listed for $asset, skipping verification"
        return 0
    fi

    if ! actual="$(sha256_file "$archive")"; then
        info "no SHA-256 tool available, skipping verification"
        return 0
    fi

    if [[ "${actual,,}" != "${expected,,}" ]]; then
        err "checksum verification failed for $asset"
    fi

    info "checksum verified"
}

install_binary() {
    local source="$1"

    mkdir -p "$BIN_DIR"
    cp "$source" "$BINARY"
    chmod 755 "$BINARY"
}

build_from_source() {
    have dotnet ||
        err "the .NET 10 SDK is required for --from-source: https://dotnet.microsoft.com/download"

    local source_archive="$TMP_DIR/source.tar.gz"
    local source_dir="$TMP_DIR/source"
    local urls=()

    mapfile -t urls < <(source_urls)

    if ! download_first "$source_archive" "${urls[@]}"; then
        err "could not download the source archive"
    fi

    mkdir -p "$source_dir"
    tar -xzf "$source_archive" -C "$source_dir" --strip-components=1

    info "building from source (this may take a few minutes)"
    dotnet publish "$source_dir/Qwek.csproj" \
        -c Release \
        -r "linux-$RID_ARCH" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -p:DebugType=None \
        -p:DebugSymbols=false \
        -p:AssemblyName=qwek \
        -o "$TMP_DIR/publish"

    install_binary "$TMP_DIR/publish/qwek"
}

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

if ((FROM_SOURCE)); then
    build_from_source
else
    ASSET="qwek-linux-$RID_ARCH.tar.gz"
    urls=()

    mapfile -t urls < <(release_urls "$ASSET")

    if download_first "$TMP_DIR/$ASSET" "${urls[@]}"; then
        sum_urls=()
        mapfile -t sum_urls < <(release_urls "qwek-checksums.txt")
        download_first "$TMP_DIR/qwek-checksums.txt" "${sum_urls[@]}" || true

        verify_checksum "$TMP_DIR/$ASSET" "$ASSET" "$TMP_DIR/qwek-checksums.txt"
        tar -xzf "$TMP_DIR/$ASSET" -C "$TMP_DIR"
        install_binary "$TMP_DIR/qwek"
    elif have dotnet; then
        info "no release build available to download"
        info "falling back to building from source with the .NET SDK"
        build_from_source
    else
        err "no release build available and the .NET 10 SDK was not found; install the SDK or publish a release first"
    fi
fi

info "installed $APP to $BINARY"

case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *)
        printf '\n%s is not on your PATH. Add it with:\n\n' "$BIN_DIR"
        printf '  echo '\''export PATH="%s:$PATH"'\'' >> ~/.bashrc\n' "$BIN_DIR"
        printf '  source ~/.bashrc\n\n'
        ;;
esac

info "run '$APP --help' to get started"
