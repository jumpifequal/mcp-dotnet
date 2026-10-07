# About this fork

**mcp-dotNET** is a fork of **MCPPOC / mcp-dotnet** by **neoz**
(<https://github.com/neoz/mcp-dotnet>), the original proof-of-concept MCP server
for .NET reverse engineering built on dnlib and ILSpy.

This fork keeps the original idea and the dnlib + ILSpy foundation, and rebuilds
the server on the MCP C# SDK 2.x (protocol `2025-11-25`) with .NET 10. See
[CHANGELOG.md](CHANGELOG.md) for the full list of changes and
[README.md](README.md#breaking-changes-from-1x) for the tool-name mapping.

## Credit

- Original author: **neoz** — <https://github.com/neoz/mcp-dotnet>
- The original tool-use workflow was demonstrated by **benina** (reaonline.net)
  in a .NET crackme tutorial: <https://forum.exetools.com/showthread.php?t=21259>

## Third-party components

- [dnlib](https://github.com/0xd4d/dnlib) — MIT
- [ICSharpCode.Decompiler (ILSpy)](https://github.com/icsharpcode/ILSpy) — MIT
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — MIT

All are used under their respective licenses.
