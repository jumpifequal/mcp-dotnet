# Changelog

All notable user-facing changes to this project are documented here.

This project is a fork of [neoz/mcp-dotnet (MCPPOC)](https://github.com/neoz/mcp-dotnet).

**2.0.0 - Fork rebuilt on MCP C# SDK 2.x with new analysis, decompilation and patching tools**

- add: MCP SDK 2.x support (protocol `2025-11-25`) with tool annotations, structured JSON results, prompts, resources and progress reporting.
- add: Streamable HTTP transport via `--transport http` with bearer-token auth (`MCP_DOTNET_HTTP_TOKEN`, `--gen-token`), loopback bind by default, a Host-header allow-list and optional TLS; `--no-auth` is accepted only on a loopback bind.
- add: bilingual documentation (English and Italian) under `docs/`, a `console.bat` manager for the HTTP server, and `publish_me` scripts that create the GitHub repo and releases.
- add: `console.bat` opens a native WPF GUI console — profiles, Start/Stop/Restart, live workspace view, a filterable/searchable log and Problems panel, Maintenance (build/test/pack) and Integration (register/recheck Claude Code, Claude Desktop, Codex); falls back to the text console when the Windows Desktop Runtime isn't available.
- add: `scripts/pack-dist.ps1` builds a ready-to-run `dist/` package (published binaries, scripts and docs, plus a zip and the NuGet package), optionally self-contained; the bundled installer registers it with no rebuild.
- add: `--read-only` mode (also `MCP_DOTNET_READONLY=1`) that hides every tool which modifies or writes assemblies.
- add: multi-assembly workspace — load several assemblies at once and address each by id, with a selectable active assembly.
- add: ILSpy-based `decompile`, `disassemble`, `decompile_assembly_attributes` and `export_project` (whole assembly to a compilable C# project).
- add: `get_control_flow_graph` returning basic blocks plus a Mermaid diagram.
- add: cross-reference tools `find_callers`, `find_callees`, `find_field_references`, `find_type_usages` and `find_derived_types`.
- add: `scan_suspicious_apis` for security triage (dynamic code loading, injection P/Invokes, process, network, crypto, registry, unsafe deserialization, anti-analysis, capture) with per-category counts.
- add: obfuscator detection hints (ConfuserEx, Dotfuscator, .NET Reactor and others) in `get_assembly_info`.
- add: resource tools `list_resources` and `get_resource` that decode `.resources`, detect embedded PE/ZIP/high-entropy blobs and extract bytes to disk.
- add: `rename_member` for deobfuscation, with renamed names appearing immediately in decompiled output.
- add: `replace_method_body`, `nop_instructions` and `read_bytes` alongside the IL patcher.
- add: guided prompts `triage_assembly`, `malware_triage` and `find_secrets`.
- add: distribution as a .NET tool / NuGet MCP server package and an `install` script that writes Claude Code, Claude Desktop and Codex configuration.
- change: tools renamed to grouped snake_case names (for example `Decompile_Method_By_RID` → `decompile`, `GetMethodILBodyByRID` → `get_method_il`, `UpdateMethodInstructions` → `patch_method_il`); existing client configurations must be updated (mapping in the README).
- change: members are addressed by metadata token or name across all tools, with methods still accepting a RID for compatibility.
- change: list results are paginated with `offset`/`limit`/`nextOffset` instead of ad-hoc paging.
- change: runtime updated to .NET 10 with dnlib 4.5 and ILSpy decompiler 11.1.
- fix: nested and compiler-generated types are now visible to every tool instead of only top-level types.
- fix: loading an assembly no longer fails when a referenced assembly cannot be resolved.
- fix: loaded files are no longer locked on disk, so a patched assembly can be saved over the original.
- fix: decompiled output now reflects in-session IL patches and member renames.
- fix: mixed-mode (native) assemblies are detected and written back correctly.
- fix: IL patching now produces verifiable code — correct locals/arguments, field operands, long branches, `ldc.i4.s` values, static vs instance calls, scope-correct BCL references, and automatic branch, exception-handler and MaxStack fix-ups.
