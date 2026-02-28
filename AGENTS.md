## Cursor Cloud specific instructions

### Project Overview

Sandcastle Help File Builder (SHFB) is a .NET documentation generation tool. It produces help files (CHM, Website, Markdown, etc.) from XML comments in .NET assemblies. The codebase is primarily Windows-oriented (.NET Framework 4.7.2 / WPF / WinForms), but core libraries target `netstandard2.0` and key CLI tools dual-target `net472;netcoreapp3.1`.

### What builds on Linux

Only the cross-platform subset can be built in this environment:

| Target | Projects |
|--------|----------|
| `netstandard2.0` | SandcastleCore, BuildComponentTargets, SandcastleBuilderUtils, SyntaxComponents, BuildComponents, PresentationStyles, SandcastleBuilderPlugIns, ColorizerLibrary |
| `netcoreapp3.1` | MRefBuilder, GenerateInheritedDocs, SandcastleBuilderMSBuild |
| `net6.0` | DotNetStandardTestCases |

The remaining ~18 projects target `net472` (WPF/WinForms/VSIX) and **cannot build on Linux**.

### Building

Build individual cross-platform projects:
```bash
dotnet build SHFB/Source/SandcastleCore/SandcastleCore.csproj -c Debug /p:GeneratePackageOnBuild=false
```

For multi-target projects, specify the Linux-compatible framework:
```bash
dotnet build SHFB/Source/MRefBuilder/MRefBuilder.csproj -c Debug -f netcoreapp3.1 /p:GeneratePackageOnBuild=false
```

**Important:** Always pass `/p:GeneratePackageOnBuild=false` on Linux. Several `.csproj` files have `GeneratePackageOnBuild=true` with a post-pack `CopyPackage` target that uses Windows batch syntax (`IF`, `Goto`, `Copy`), which fails on Linux shells.

### Linting / Analyzers

Projects already have `<EnableNETAnalyzers>true</EnableNETAnalyzers>` configured. Analyzer warnings/errors appear during normal `dotnet build`. No separate lint command is needed.

### No automated tests

This codebase has no unit test runner or test framework. The `TestCasesDotNetStandard` and `TestCaseProject` directories contain sample code used as input for documentation generation, not test suites.

### Key output paths

- `SHFB/Deploy/Components/` — netstandard2.0 component DLLs
- `SHFB/Deploy/netcoreapp3.1/` — netcoreapp3.1 CLI tool DLLs (MRefBuilder, GenerateInheritedDocs, SandcastleBuilder.MSBuild)

### MasterBuild.bat

`MasterBuild.bat` is the full Windows build script. It requires Visual Studio MSBuild and builds all solutions including VSIX packages and documentation. It cannot run on Linux.
