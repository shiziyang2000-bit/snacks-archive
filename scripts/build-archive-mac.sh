#!/usr/bin/env bash
# Build this source in isolated staging. Never installs, launches or publishes.
set -euo pipefail
REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-}" in --check|"") ;; *) echo "Usage: $0 [--check]" >&2; exit 2 ;; esac
fail() { echo "Build prerequisite: $*" >&2; exit 1; }
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || fail 'Apple silicon macOS is required'
[[ "$(sw_vers -productVersion | cut -d. -f1)" -ge 26 ]] || fail 'macOS 26 or newer is required'
ORIGINAL_APP="${SNACKS_ORIGINAL_APP:-}"
[[ -n "$ORIGINAL_APP" && -d "$ORIGINAL_APP/Contents/Resources/backend" ]] || fail 'set SNACKS_ORIGINAL_APP to the extracted official v2.20.1 arm64 .app'
VERSION=$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$ORIGINAL_APP/Contents/Info.plist")
[[ "$VERSION" == 2.20.1 ]] || fail "expected upstream resource version 2.20.1, got $VERSION"
for tool in ffmpeg ffprobe; do
  [[ -x "$ORIGINAL_APP/Contents/Resources/ffmpeg/$tool" ]] || fail "upstream app is missing $tool"
done
DOTNET_BIN="${SNACKS_DOTNET:-$(command -v dotnet || true)}"
NODE_BIN="${SNACKS_NODE:-$(command -v node || true)}"
NPM_BIN="${SNACKS_NPM:-$(command -v npm || true)}"
[[ -x "$DOTNET_BIN" ]] || fail 'install .NET 10 SDK or set SNACKS_DOTNET'
[[ -x "$NODE_BIN" && -x "$NPM_BIN" ]] || fail 'install Node.js 22+ and npm, or set SNACKS_NODE / SNACKS_NPM'
[[ "$("$DOTNET_BIN" --version)" == 10.* ]] || fail '.NET 10 SDK is required'
"$NODE_BIN" -e 'if(Number(process.versions.node.split(".")[0])<22)process.exit(1)' || fail 'Node.js 22+ is required'
for tool in python3 otool install_name_tool codesign lipo; do command -v "$tool" >/dev/null || fail "missing $tool"; done
MP4BOX_SOURCE="${SNACKS_MP4BOX:-}"
if [[ -z "$MP4BOX_SOURCE" ]] && command -v brew >/dev/null; then
  GPAC_PREFIX="$(brew --prefix gpac 2>/dev/null || true)"
  [[ -z "$GPAC_PREFIX" ]] || MP4BOX_SOURCE="$GPAC_PREFIX/bin/MP4Box"
fi
[[ -n "$MP4BOX_SOURCE" && -x "$MP4BOX_SOURCE" ]] || fail 'install GPAC, or set SNACKS_MP4BOX to MP4Box'
lipo -archs "$MP4BOX_SOURCE" | grep -qw arm64 || fail 'MP4Box must contain arm64'
if [[ "${1:-}" == --check ]]; then
  echo 'Prerequisites found. No compilation, download, signing or packaging performed.'
  exit 0
fi

BUILD_WORK="${SNACKS_BUILD_WORK:-$REPO_DIR/.build-archive}"
mkdir -p "$BUILD_WORK"
BUILD_WORK="$(cd "$BUILD_WORK" && pwd)"
RUN_DIR="$(mktemp -d "$BUILD_WORK/run-XXXXXXXX")"
OUTPUT_DIR="${SNACKS_ARCHIVE_OUTPUT:-$RUN_DIR/dist}"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
[[ -z "$(ls -A "$OUTPUT_DIR")" ]] || fail 'output directory must be empty'
unset ELECTRON_RUN_AS_NODE
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
echo '[1/5] Copying the supplied upstream media-resource snapshot'
ditto "$ORIGINAL_APP/Contents/Resources/ffmpeg" "$RUN_DIR/ffmpeg"
"$RUN_DIR/ffmpeg/ffmpeg" -version > "$RUN_DIR/ffmpeg-version.txt"
echo '[2/5] Publishing this source with self-contained .NET 10'
"$DOTNET_BIN" publish "$REPO_DIR/Snacks/Snacks.csproj" -c Release -r osx-arm64 --self-contained \
  --artifacts-path "$RUN_DIR/dotnet-artifacts" -p:DebugType=None -p:DebugSymbols=false -o "$RUN_DIR/backend"
python3 - "$ORIGINAL_APP/Contents/Resources/backend" "$RUN_DIR/backend" <<'PY'
from pathlib import Path
import shutil, sys
source, target = map(Path, sys.argv[1:])
for path in source.glob('*.dylib'):
    if not (target / path.name).exists():
        shutil.copy2(path, target / path.name, follow_symlinks=False)
for name in ('tessdata', 'wordlists'):
    if (source / name).is_dir():
        shutil.copytree(source / name, target / name, dirs_exist_ok=True)
for name in ('x64', 'x86'):
    if (source / name).is_dir():
        (target / name).mkdir(exist_ok=True)
        for path in (source / name).glob('*.dylib'):
            shutil.copy2(path, target / name / path.name, follow_symlinks=False)
PY
echo '[3/5] Bundling MP4Box and its recursive arm64 libraries'
mkdir -p "$RUN_DIR/licenses"
cp "$REPO_DIR/LICENSE" "$RUN_DIR/licenses/Snacks-MIT.txt"
python3 "$REPO_DIR/electron-app/scripts/bundle-gpac-mac.py" "$RUN_DIR/gpac" \
  --mp4box "$MP4BOX_SOURCE" --licenses "$RUN_DIR/licenses/gpac" --manifest "$RUN_DIR/gpac-manifest.json"
if [[ -d "$ORIGINAL_APP/Contents/Resources/Licenses" ]]; then
  ditto "$ORIGINAL_APP/Contents/Resources/Licenses" "$RUN_DIR/licenses/upstream-bundled"
fi
echo '[4/5] Validating the wrapper with locked Electron tooling'
if [[ ! -f "$REPO_DIR/electron-app/node_modules/electron-builder/cli.js" ]]; then
  "$NPM_BIN" --prefix "$REPO_DIR/electron-app" ci --no-audit --no-fund
fi
"$NPM_BIN" --prefix "$REPO_DIR/electron-app" run check
cp "$REPO_DIR/electron-app/node_modules/electron/dist/LICENSE" "$RUN_DIR/licenses/Electron-LICENSE.txt"
cp "$REPO_DIR/electron-app/node_modules/electron/dist/LICENSES.chromium.html" "$RUN_DIR/licenses/LICENSES.chromium.html"
cp "$REPO_DIR/electron-app/node_modules/tree-kill/LICENSE" "$RUN_DIR/licenses/tree-kill-LICENSE.txt"
if [[ -n "${SNACKS_EXTRA_LICENSES:-}" ]]; then
  [[ -d "$SNACKS_EXTRA_LICENSES" ]] || fail 'SNACKS_EXTRA_LICENSES must be a directory'
  ditto "$SNACKS_EXTRA_LICENSES" "$RUN_DIR/licenses/additional"
fi
python3 - "$REPO_DIR/electron-app/package.json" "$RUN_DIR" "$OUTPUT_DIR" <<'PY'
import json, sys
from pathlib import Path
package, work, output = map(Path, sys.argv[1:])
config = json.loads(package.read_text())['build']
config['directories'] = {'output': str(output)}
config['extraResources'] = [{'from': str(work / name), 'to': target, 'filter': ['**/*']}
    for name, target in [('backend','backend'),('ffmpeg','ffmpeg'),('gpac','gpac'),('licenses','Licenses')]]
config['mac']['identity'] = '-'
config['publish'] = None
(work / 'electron-builder.json').write_text(json.dumps(config, ensure_ascii=False, indent=2) + '\n')
PY
echo '[5/5] Creating an ad-hoc signed, unpublished DMG'
env -u APPLE_ID -u APPLE_APP_SPECIFIC_PASSWORD -u APPLE_TEAM_ID \
  -u CSC_NAME -u CSC_LINK -u CSC_KEY_PASSWORD -u SNACKS_NOTARIZE CSC_IDENTITY_AUTO_DISCOVERY=false \
  "$NODE_BIN" "$REPO_DIR/electron-app/node_modules/electron-builder/cli.js" \
  --projectDir "$REPO_DIR/electron-app" --config "$RUN_DIR/electron-builder.json" --mac --arm64 --publish never
python3 - "$OUTPUT_DIR" <<'PY'
from pathlib import Path
import hashlib, sys
output = Path(sys.argv[1]); images = sorted(output.glob('*.dmg'))
if not images:
    raise SystemExit('No DMG was produced')
(output / 'SHA256SUMS.txt').write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest() + '  ' + p.name + '\n' for p in images))
for image in images:
    print(image)
PY
echo "Build evidence: $RUN_DIR"
echo 'No app was launched or installed. Ad-hoc signature only; no Apple notarization.'
