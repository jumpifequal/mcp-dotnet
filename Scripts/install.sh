#!/usr/bin/env bash
# Build mcp-dotNET and register it (stdio) with Claude Code, Claude Desktop and/or Codex.
# For the HTTP transport use install.ps1 -Transport http on Windows, or configure the client
# manually (see docs/en/installation.md). Merges into existing config files (a timestamped .bak
# is made first). Requires: dotnet, and python3 for JSON merging.
#
# Usage: Scripts/install.sh [--target claude-code|claude-desktop|codex|all] [--read-only] [--name NAME] [--no-build]
set -euo pipefail

target=all; readonly_flag=0; name=dotnet; nobuild=0
while [ $# -gt 0 ]; do
  case "$1" in
    --target) target="$2"; shift 2;;
    --read-only) readonly_flag=1; shift;;
    --name) name="$2"; shift 2;;
    --no-build) nobuild=1; shift;;
    -h|--help) grep '^#' "$0" | sed 's/^# \?//'; exit 0;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="$repo/src/MCPPOC/MCPPOC.csproj"
extra=(); [ "$readonly_flag" = 1 ] && extra=(--read-only)

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }

if [ "$nobuild" = 0 ]; then
  step "Building mcp-dotNET (Release)..."
  dotnet build "$project" -c Release >/dev/null
fi
dll="$repo/bin/MCPPOC/Release/net10.0/mcp-dotnet.dll"
[ -f "$dll" ] || { echo "Server binary not found at $dll" >&2; exit 1; }
echo "    binary: $dll"

command=dotnet
args_json=$(python3 - "$dll" "${extra[@]}" <<'PY'
import json, sys
print(json.dumps(sys.argv[1:]))
PY
)

backup() { [ -f "$1" ] && cp "$1" "$1.bak-$(date +%Y%m%d-%H%M%S)" && echo "    backup: $1.bak-*"; return 0; }

merge_json() { # $1 = config path
  local path="$1"; mkdir -p "$(dirname "$path")"
  backup "$path"
  python3 - "$path" "$name" "$command" "$args_json" <<'PY'
import json, os, sys
path, name, command, args_json = sys.argv[1:5]
data = {}
if os.path.exists(path):
    with open(path, encoding='utf-8') as f:
        data = json.load(f) or {}
data.setdefault('mcpServers', {})[name] = {'command': command, 'args': json.loads(args_json)}
with open(path, 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=2)
PY
  echo "    wrote: $path"
}

configure_claude_code() {
  step "Claude Code"
  if command -v claude >/dev/null 2>&1; then
    # shellcheck disable=SC2046
    claude mcp add "$name" -- "$command" $(python3 -c "import json,sys;print(' '.join(json.loads(sys.argv[1])))" "$args_json") \
      || merge_json "$HOME/.claude.json"
  else
    echo "    'claude' CLI not found; writing project .mcp.json"
    merge_json "$repo/.mcp.json"
  fi
}

configure_claude_desktop() {
  step "Claude Desktop"
  case "$(uname -s)" in
    Darwin) path="$HOME/Library/Application Support/Claude/claude_desktop_config.json";;
    *)      path="${APPDATA:-$HOME/.config}/Claude/claude_desktop_config.json";;
  esac
  merge_json "$path"
  echo "    Restart Claude Desktop to pick up the change."
}

configure_codex() {
  step "Codex"
  local path="$HOME/.codex/config.toml"; mkdir -p "$(dirname "$path")"
  if [ -f "$path" ] && grep -q "^\[mcp_servers\.$name\]" "$path"; then
    echo "    [mcp_servers.$name] already present; leaving untouched."
    return
  fi
  backup "$path"
  local toml_args; toml_args=$(python3 -c "import json,sys;print(', '.join('\"%s\"'%a for a in json.loads(sys.argv[1])))" "$args_json")
  { [ -f "$path" ] && printf '\n'; printf '[mcp_servers.%s]\ncommand = "%s"\nargs = [%s]\n' "$name" "$command" "$toml_args"; } >> "$path"
  echo "    wrote: $path"
}

case "$target" in
  claude-code) configure_claude_code;;
  claude-desktop) configure_claude_desktop;;
  codex) configure_codex;;
  all) configure_claude_code; configure_claude_desktop; configure_codex;;
  *) echo "unknown target: $target" >&2; exit 1;;
esac

printf '\033[32mDone. Server "%s" -> %s %s\033[0m\n' "$name" "$command" "$dll"
