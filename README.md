# LLMeta Desktop Template

A clean Windows desktop application template with system tray resident functionality.

## Features
- System tray icon with context menu (background resident)
- WPF main window
- MVVM pattern implementation
- Settings persistence (JSON)
- Windows startup integration
- Logging functionality
- Velopack update support

## Requirements
- Windows 11
- .NET SDK 10

## Development
```
dotnet run --project .\LLMeta.App\
```

## Project Structure
- `App.xaml.cs` - Application entry point with tray icon setup
- `MainWindow.xaml` - Main UI window
- `ViewModels/` - MVVM view models
- `Models/` - Data models (AppSettings)
- `Stores/` - Data persistence (SettingsStore)
- `Services/` - Business logic services
- `Utils/` - Utility classes (Logger, Paths, Commands)

## Formatter / Linter / Hooks

This project uses [Husky.Net](https://github.com/alirezanet/Husky.Net) for git hooks.

### Setup

After restoring NuGet packages, husky will be automatically configured:

```
dotnet restore
```

Or manually install husky:

```
dotnet husky install
```

### Manual Commands

Format code:
```
dotnet csharpier format .
```

Lint (build with analyzers):
```
dotnet build .\LLMeta.App\LLMeta.App.csproj
```

Run hooks manually:
```
dotnet husky run
```
