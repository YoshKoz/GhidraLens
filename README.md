# Ghidra Lens

A WinUI 3 front-end for Ghidra. Ghidra stays the analysis engine; Ghidra Lens talks to it through the
[GhidraMCP](https://github.com/bethington/ghidra-mcp) plugin's HTTP API.

## Requirements

- Windows 10 19041+ / Windows 11, .NET 10 SDK
- Ghidra 12.1.4 with the GhidraMCP 7.0.0 extension enabled (HTTP server on `127.0.0.1:8089`)

## Build

```powershell
dotnet build GhidraLens.csproj -c Release -p:Platform=x64
```

Output: `bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\GhidraLens.exe` (unpackaged, self-contained).

## Pages

| Page | Backed by |
|---|---|
| Program | `list_project_files`, `open_program`, `save_program`, `get_metadata` |
| CodeBrowser | `disassemble_function`, `decompile_function`, `read_memory` |
| Functions | decompile, disassembly, xrefs, callers/callees, variables; rename, comment, tag, prototype |
| Strings / Imports / Exports / Segments / Classes / Globals | list endpoints + `get_xrefs_to` |
| Time | `debugger/*` run control, registers, stack, breakpoints, traces |
| Static Mappings | `debugger/modules`, `static_to_dynamic`, `dynamic_to_static` |
| P-code Stepper | `get_function_pcode`, `emulate_function` |
| Tools | every endpoint from `/mcp/schema`, with generated forms |
