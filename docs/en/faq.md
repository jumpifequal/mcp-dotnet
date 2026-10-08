# FAQ and troubleshooting

🇬🇧 English · [🇮🇹 Italiano](../it/faq.md)

### The client doesn't see the server / no tools appear

- Confirm the build exists: `bin/MCPPOC/Release/net10.0/mcp-dotnet.dll`.
- Check the command runs: `dotnet .../mcp-dotnet.dll --help` should print usage.
- Fully restart the client (Claude Desktop must be quit from the tray, not just closed).
- Check the config path and that `command`/`args` are correct. Logs go to stderr; raise them with `--log-level Information`.

### "No assembly loaded"

Call `load_assembly` with an absolute path first. Each tool then uses the active assembly, or an explicit `assembly` id (see `list_assemblies`).

### A tool returns an error result instead of data

Tool errors are returned as normal MCP results with `isError: true` and an actionable message (unknown token, type not found with suggestions, invalid IL, and so on). That is by design — the model can read the message and retry.

### Can it run the program, or debug it?

No. mcp-dotNET is **static** analysis only. It reads and patches metadata and IL; it never executes the assembly. For dynamic analysis use a debugger.

### Can I decompile the whole assembly at once?

Yes — `export_project` writes a compilable SDK-style C# project to an empty directory. For large assemblies that is better than decompiling type by type; then grep the output.

### Does patching really work? Will the patched file run?

Yes. The IL patcher fixes up branches, exception handlers and MaxStack, and `save_assembly` keeps metadata tokens stable. The test suite saves patched assemblies, loads them into the .NET runtime and executes them. Still, verify your own patches: an unbalanced evaluation stack produces an invalid method.

### Obfuscated assemblies?

`get_assembly_info` reports obfuscator hints (ConfuserEx, Dotfuscator, .NET Reactor and others) and renaming/control-flow signals. You can still browse, decompile and cross-reference; use `rename_member` as you understand members so later decompilation reads well. Encrypted strings/bodies that are only decrypted at runtime cannot be recovered by static analysis alone.

### Mixed-mode / native assemblies?

Managed metadata and IL are available. `get_assembly_info` reports mixed-mode; the native parts are not decompiled. `save_assembly` writes mixed-mode images back with their native code.

### HTTP: I get 401 Unauthorized

Send `Authorization: Bearer <token>`, where the token is the value of `MCP_DOTNET_HTTP_TOKEN` (or the ephemeral token printed at startup on a loopback bind). Use `--gen-token` to mint one.

### HTTP: I get 421 Misdirected Request

The `Host` header is not in the allow-list (DNS-rebinding protection). On a loopback bind only `localhost`/`127.0.0.1`/`::1` are accepted; add others with `--allowed-host`.

### Is it safe to analyse malware?

Reading and patching are static and safe. But decompiled strings, resources and code are third-party content: do not combine the session with high-impact tools (email, shell, browser), and treat any paths or URLs a sample references as untrusted. Never run the sample.

### Which .NET versions can it analyse?

Any ECMA-335 assembly: .NET Framework, .NET Core, .NET 5–10, Mono. The server itself needs the .NET 10 SDK to run.

### Where are the limits?

No runtime/dynamic analysis, no recovery of runtime-only decryption, no editing of native code, and decompilation of heavily obfuscated code may be partial. Report issues with a minimal sample where possible.
