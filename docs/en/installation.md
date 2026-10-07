# Installation

🇬🇧 English · [🇮🇹 Italiano](../it/installation.md)

mcp-dotNET is a .NET MCP server. "Installing" means building it and registering it with your AI client. There is no runtime to set up beyond the .NET SDK.

## Requirements

- Windows 10/11, Linux or macOS.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` ≥ 10.0).
- An MCP client: Claude Desktop, Claude Code, the ChatGPT desktop app, Codex, or any other.

## Option A — install script (recommended)

The script builds the server in Release and writes/merges the configuration for Claude Code, Claude Desktop and Codex. A timestamped backup is made before any file is changed; existing entries are preserved.

```powershell
# Windows
git clone https://github.com/jumpifequal/mcp-dotnet C:\Tools\mcp-dotnet
cd C:\Tools\mcp-dotnet
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

```bash
# Linux / macOS (needs python3 for JSON merging)
git clone https://github.com/jumpifequal/mcp-dotnet ~/mcp-dotnet
cd ~/mcp-dotnet
./scripts/install.sh
```

Useful options (PowerShell):

| Option | Effect |
|---|---|
| `-Target claude-code \| claude-desktop \| codex \| all` | which client(s) to configure (default `all`) |
| `-ReadOnly` | register with `--read-only` (no patch/rename/save tools) |
| `-Transport http -Port 5005` | register the HTTP transport instead of stdio (Claude Code) |
| `-NoBuild` | skip the build, use an existing Release binary |
| `-NoRegister` | build only, touch no configuration |

`setup.bat` runs the same thing with a double-click.

## Option B — manual configuration

Build once:

```bash
dotnet build -c Release
```

The server DLL is at `src/MCPPOC/bin/Release/net10.0/mcp-dotnet.dll`. Launch it through the shared `dotnet` host.

### Connect your AI client

**Claude Desktop / any `mcpServers` JSON** — add to `claude_desktop_config.json` (Windows: `%APPDATA%\Claude\`, macOS: `~/Library/Application Support/Claude/`):

```json
{
  "mcpServers": {
    "dotnet": {
      "command": "dotnet",
      "args": ["C:\\Tools\\mcp-dotnet\\src\\MCPPOC\\bin\\Release\\net10.0\\mcp-dotnet.dll", "--read-only"]
    }
  }
}
```

Drop `--read-only` to enable patching. Fully restart Claude Desktop afterwards.

**Claude Code**

```bash
claude mcp add dotnet -- dotnet /path/to/src/MCPPOC/bin/Release/net10.0/mcp-dotnet.dll
```

**Codex** — add to `~/.codex/config.toml`:

```toml
[mcp_servers.dotnet]
command = "dotnet"
args = ["/path/to/src/MCPPOC/bin/Release/net10.0/mcp-dotnet.dll"]
```

### OpenAI clients

The ChatGPT desktop app and Codex (CLI and IDE extension) speak MCP over stdio; use the Codex configuration above, or the app's "Developer / MCP servers" settings with the same `command`/`args`. ChatGPT on the web cannot use a local server.

## Install as a .NET tool

```bash
dotnet pack src/MCPPOC/MCPPOC.csproj -c Release -o artifacts
dotnet tool install -g mcp-dotnet --add-source ./artifacts
```

Then the command is simply `mcp-dotnet`, and client configs can use `"command": "mcp-dotnet"` with no DLL path.

## HTTP transport

For Claude Code or a shared server, run the HTTP transport instead of stdio:

```bash
# generate a token once
dotnet src/MCPPOC/bin/Release/net10.0/mcp-dotnet.dll --gen-token
# start the server (loopback, bearer auth)
set MCP_DOTNET_HTTP_TOKEN=<token>        # export on Linux/macOS
dotnet .../mcp-dotnet.dll --transport http --port 5005
```

Register it with Claude Code:

```bash
claude mcp add dotnet --transport http http://127.0.0.1:5005/mcp --header "Authorization: Bearer <token>"
```

Or let the installer and console do it: `install.ps1 -Transport http -Target claude-code`, then `console.bat` to start and watch the server. Details and TLS in [Tweaking → Transports](tweaking.md#transports-stdio-and-http).

## Check it works

Start a conversation and ask the client to *load a .NET assembly and list its namespaces*. From a terminal you can smoke-test the stdio server:

```bash
dotnet .../mcp-dotnet.dll --help
```

## Update

```bash
git pull
dotnet build -c Release
```

Client configuration does not need to change (it points at the same DLL).

## Uninstall

Remove the server entry from your client configuration (or `claude mcp remove dotnet`), delete the clone, and — if you installed the tool — `dotnet tool uninstall -g mcp-dotnet`.
