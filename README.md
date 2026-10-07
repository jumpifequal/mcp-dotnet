# mcp-dotNET: .NET reverse engineering over MCP

> **Fork of [neoz/mcp-dotnet (MCPPOC)](https://github.com/neoz/mcp-dotnet).** This fork rebuilds the original proof-of-concept on the MCP C# SDK 2.x and .NET 10 and adds decompilation, cross-reference, security-scan and IL-patching tools. See [NOTICE.md](NOTICE.md) for credits and [CHANGELOG.md](CHANGELOG.md) for what changed.

An [MCP](https://modelcontextprotocol.io) server that lets an AI assistant analyse .NET assemblies. It loads assemblies, browses metadata, follows cross-references, reads IL, **decompiles to C# with the ILSpy engine**, flags security-relevant APIs, and **patches IL**, then saves the result.

Built on [dnlib](https://github.com/0xd4d/dnlib) (metadata, IL, writing), [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) (the ILSpy engine) and the official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 2.x.

> Static analysis only: the server never executes the code it inspects.

## What's new in 2.0

- **MCP C# SDK 2.2** (protocol `2025-11-25`): tool annotations (read-only/destructive/idempotent), structured output with JSON schemas, `isError` results with actionable messages, prompts, resources, progress notifications, and stdio **or Streamable HTTP** transport.
- **.NET 10**, ILSpy decompiler 11.1, dnlib 4.5. Ships as a `dotnet tool` / NuGet MCP server package.
- **Multi-assembly workspace**: load several assemblies and address them by id. Files are read into memory, so the originals are not locked and can be overwritten when saving.
- **Nested types everywhere.** 1.x only saw top-level types.
- **Decompilation reflects your changes**: patched IL and renamed members show up immediately.
- New tools: ILSpy IL disassembly, real control-flow graphs (basic blocks plus Mermaid), callers/callees, type usages, derived types, an API risk scanner, an obfuscator detector, resource extraction including `.resources` decoding, raw byte reads, whole-project export, and member renaming for deobfuscation.
- Rewritten IL patcher: correct locals/args, field operands, long branches, `ldc.i4.s`, static vs instance calls, scope-correct references to BCL methods, branch and exception-handler retargeting, MaxStack recomputation. Every instruction in `dnlib.dll` round-trips through the parser (161,116 of 161,116).
- `--read-only` mode, which removes every tool that modifies or writes files.
- Test suite: 58 tests, including patches that are saved, loaded into the runtime and executed, and end-to-end MCP protocol tests.

## Install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/neoz/mcp-dotnet
cd mcp-dotnet
dotnet build -c Release
```

Or install as a global tool from a locally built package:

```bash
dotnet pack src/MCPPOC/MCPPOC.csproj -c Release -o artifacts
dotnet tool install -g mcp-dotnet --add-source ./artifacts
```

## Configure your MCP client

### Quick install (recommended)

The install script builds the server and writes/merges the config for Claude Code, Claude Desktop and Codex (a timestamped backup is made before any file is changed):

```powershell
# Windows
./scripts/install.ps1                      # all three clients
./scripts/install.ps1 -Target codex -ReadOnly
```

```bash
# Linux / macOS (needs python3)
./scripts/install.sh                       # all three clients
./scripts/install.sh --target claude-code --read-only
```

### Manual configuration

**Claude Code**

```bash
claude mcp add dotnet -- dotnet run --project /path/to/mcp-dotnet/src/MCPPOC -c Release
```

(or `claude mcp add dotnet -- mcp-dotnet` when installed as a tool)

**Claude Desktop / VS Code / any client using `mcpServers` JSON**

```json
{
  "mcpServers": {
    "dotnet": {
      "command": "mcp-dotnet",
      "args": ["--read-only"]
    }
  }
}
```

Without the global tool, use `"command": "dotnet", "args": ["/path/to/src/MCPPOC/bin/Release/net10.0/mcp-dotnet.dll"]`.

### Command line

| Option | Meaning |
|---|---|
| *(none)* | stdio transport (default) |
| `--http` | Streamable HTTP transport |
| `--urls <urls>` | Listen address for `--http` (default `http://127.0.0.1:5005`) |
| `--path <path>` | HTTP endpoint path (default `/mcp`) |
| `--read-only` | Disable patch/rename/save tools (also: `MCP_DOTNET_READONLY=1`) |
| `--log-level <level>` | Log level for stderr (default `Warning`) |

HTTP mode binds to localhost by default and has no authentication. Don't expose it on a network: the tools read and write files with the server's permissions.

## Tools

Members are identified by **metadata token** (`0x06000012`), which every tool returns and accepts. Names also work when unambiguous: `Namespace.Type`, `Outer+Inner` or `Outer/Inner`, `Namespace.Type::Method`, `Namespace.Type::Method(int32,string)`. List tools are paginated (`offset`, `limit`, `nextOffset`).

| Area | Tools |
|---|---|
| Workspace | `load_assembly`, `list_assemblies`, `set_active_assembly`, `unload_assembly`, `get_assembly_info`, `list_assembly_references` |
| Browse | `list_namespaces`, `list_types`, `get_type`, `find_methods`, `find_members`, `get_method`, `get_entry_points`, `list_pinvokes`, `get_static_field_values` |
| Code | `decompile`, `decompile_assembly_attributes`, `disassemble`, `get_method_il`, `get_control_flow_graph`, `read_bytes`, `export_project` |
| Cross-references | `find_strings`, `find_callers`, `find_callees`, `find_field_references`, `find_type_usages`, `find_derived_types` |
| Security | `scan_suspicious_apis` (dynamic code loading, injection P/Invokes, process, network, crypto, registry, unsafe deserialization, anti-analysis, capture…), obfuscator hints in `get_assembly_info` |
| Resources | `list_resources`, `get_resource` (decodes `.resources`, detects embedded PE/ZIP/GZip/high-entropy blobs, saves to disk) |
| Patching¹ | `patch_method_il`, `replace_method_body`, `nop_instructions`, `rename_member`, `save_assembly` |

¹ Not available with `--read-only`.

### Prompts

- `triage_assembly`: a structured first look at an unknown assembly.
- `malware_triage`: capabilities, payload staging, IOCs, MITRE ATT&CK mapping.
- `find_secrets`: hard-coded credentials, keys and connection strings.

### Resources

- `dotnet://assemblies`: the loaded assemblies.
- `dotnet://{assembly}/info`: assembly overview.
- `dotnet://{assembly}/source/{member}`: decompiled C# for a type name or member token.

## IL patching syntax

`patch_method_il` replaces `removeCount` instructions starting at an IL offset. The default overwrites as many instructions as you insert; `0` inserts without removing. It accepts ilasm/ILSpy syntax and the format `get_method_il` prints, so listings can be pasted back:

```text
ldarg a                                         // parameter by name, index or A_n
brtrue IL_0000                                  // target = instruction of the original body
ldstr "denied\n"
call void [System.Console]System.Console::WriteLine(string)
br #0                                           // target = 1st instruction of this snippet
ldsfld int32 MyApp.Config::Retries
callvirt instance int32 System.String::get_Length()
switch (IL_0010, IL_0020)
```

Branches and exception handlers that pointed at removed instructions are redirected to the replacement. Short/long branch forms are re-optimized and MaxStack is recomputed. `save_assembly` keeps metadata tokens stable and refuses to overwrite files unless `overwrite=true`.

## Development

```bash
dotnet test mcp-dotnet.slnx
```

```
src/MCPPOC/            server (mcp-dotnet)
  Workspace/           multi-assembly, thread-safe workspace
  Analysis/            IL parser, patcher, ILSpy service, API scanner, CFG, resources
  Tools/               MCP tools, grouped by area
  Server/              DI setup, prompts, resources
tests/MCPPOC.Tests/    unit, end-to-end patch and MCP protocol tests
tests/TestTarget/      deterministic assembly the tests analyse and patch
tests/fixtures/        third-party binaries used as test inputs
```

## Breaking changes from 1.x

Tool names are now snake_case and grouped. `ListTypesRegex`/`SearchTypes` → `list_types`; `Find_Methods_By_Name` → `find_methods`; `GetMethodILBodyByRID` → `get_method_il`; `Decompile_Method_By_RID` → `decompile`; `ExtractControlFlowGraph` → `get_control_flow_graph`; `FindMethodReferences` → `find_callers`; `FindStringLiterals`/`FindStringReferences` → `find_strings`; `UpdateMethodInstructions` → `patch_method_il`; `ReadMemoryData` → `read_bytes`; `ListFields`/`ListProperties`/`ListEvents` → `find_members`. Methods can still be passed by RID, for example `"12"`.

## License and responsible use

MIT. Analyse only software you are authorised to analyse, and respect licences and applicable law.
