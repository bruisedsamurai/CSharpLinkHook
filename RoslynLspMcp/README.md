# RoslynLspMcp

An [MCP](https://modelcontextprotocol.io) server that exposes Roslyn-powered C#
navigation over stdio. It does **not** own a Roslyn process: it reuses the warm
`roslyn-language-server` that the **RoslynLsp** broker already hosts per workspace,
talking to it through the broker's generic LSP passthrough over the same named
pipe. If the workspace is still loading, tools return a clear "retry shortly"
message instead of blocking.

## Tools

- **get_class_constructors_and_properties** `(symbol, file, line)` — resolves the
  type-name token on that line and returns the type's documentation, the full
  signature of every constructor, and all public/protected/internal properties,
  each with its documentation.
- **get_class_methods** `(symbol, file, line)` — the type's public methods,
  declared and inherited (grouped by declaring type). Members inherited from
  referenced assemblies / the .NET base class library are best-effort; the result
  notes when the list may be partial.
- **get_namespace_declarations** `(namespace)` — every top-level type declared
  directly in the exact namespace (non-recursive), each with documentation.

All documentation is the language server's hover content, reproduced verbatim.

## Running

```bash
dotnet run --project RoslynLspMcp
```

The server uses stdio transport, so all logging goes to stderr. It resolves the
broker pipe the same way RoslynLsp does (env `ROSLYN_LSP_PIPE`, else the sole
`.sln`/`.slnx` basename, else a hash of the working directory) and starts the
broker if it is not already running.
