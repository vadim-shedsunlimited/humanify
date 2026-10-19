# Humanify C# Version

A C# port of HumanifyJS - a tool to deobfuscate JavaScript code using LLMs.

## Status

This is a work-in-progress C# implementation of HumanifyJS. The core CLI structure and basic transformations are implemented, but some features are still in development.

## Features Implemented

- ✅ CLI framework with System.CommandLine
- ✅ OpenAI command with API integration
- ✅ Basic Babel-like transformations (void 0 → undefined, comparison flipping, scientific notation)
- ✅ Task state management and resume capability
- ✅ Parallel processing with configurable agents
- ✅ Local command structure (placeholder - requires LLM integration)
- ✅ Download command structure (placeholder)

## Features Not Yet Implemented

- ❌ Local LLM integration (llama.cpp, etc.)
- ❌ Prettier formatting
- ❌ Advanced AST transformations
- ❌ WebUI command
- ❌ Full test suite

## Building

```bash
cd csharp/Humanify
dotnet build
```

## Usage

### Install as Global Tool

```bash
dotnet pack
dotnet tool install --global --add-source ./nupkg humanify
```

### Run Directly

```bash
dotnet run -- openai input.js --apiKey YOUR_API_KEY
```

### Available Commands

```bash
# OpenAI mode
dotnet run -- openai input.js --apiKey YOUR_API_KEY --model gpt-4o-mini

# Local mode (placeholder)
dotnet run -- local input.js

# Download models (placeholder)
dotnet run -- download
```

## Project Structure

```
Humanify/
├── Program.cs              # Main entry point
├── Unminify.cs            # Core processing logic
├── Commands/
│   ├── OpenAICommand.cs   # OpenAI integration
│   ├── LocalCommand.cs    # Local LLM (placeholder)
│   └── DownloadCommand.cs # Model download (placeholder)
├── Plugins/
│   ├── Babel/
│   │   └── BabelPlugin.cs # AST transformations
│   └── OpenAI/
│       └── OpenAIRenamePlugin.cs # AI-powered renaming
└── Verbose.cs             # Logging utilities
```

## Differences from TypeScript Version

1. **Language**: C# instead of TypeScript/Node.js
2. **Dependencies**: Uses System.CommandLine instead of Commander.js
3. **HTTP Client**: Uses HttpClient instead of native fetch
4. **JSON**: Uses System.Text.Json instead of native JSON
5. **AST Processing**: Simplified regex-based approach (for now) instead of Babel AST

## Development Roadmap

1. **Phase 1**: Basic CLI and OpenAI integration ✅
2. **Phase 2**: Add Prettier formatting
3. **Phase 3**: Integrate with a C# JavaScript parser library
4. **Phase 4**: Add local LLM support via llama.cpp bindings
5. **Phase 5**: Full feature parity with TypeScript version

## Contributing

This is a new port, so contributions are welcome! Areas that need work:

- Better JavaScript AST parsing
- Local LLM integration
- Performance optimizations
- Test coverage
- Documentation

## License

MIT - Same as the original HumanifyJS project.
