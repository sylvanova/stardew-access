# Repository Guidelines

## Project Structure & Module Organization
- `stardew-access/` is the repo root (solution file lives here).
- `stardew-access/stardew-access/` contains the mod code.
  - `Features/`, `Patches/`, `Integrations/`, `Utils/` are the main C# modules.
  - `assets/` holds runtime data files.
  - `i18n/` contains Fluent translation files (`*.ftl`).
- `docs/` holds documentation sources and the compiler script.
- `ref/` stores reference DLLs and mod refs used during build.

## Build, Test, and Development Commands
- Build the mod:
  ```sh
  dotnet build stardew-access.sln
  ```
  Produces `stardew-access.dll` under `stardew-access/bin/Debug/net6.0/`.
- Compile docs (optional):
  ```sh
  cd docs
  ruby compiler_script.rb
  ```
- Manual run: copy the built `stardew-access` folder into your Stardew Valley `Mods` directory and launch via SMAPI.

## Coding Style & Naming Conventions
- C# code follows existing conventions: `PascalCase` for types/methods, `camelCase` for locals, `_camelCase` for private fields.
- Keep translations in `i18n/` and reuse existing key prefixes where possible.
- Prefer small, readable helpers and avoid large in-method logic blocks.

## Testing Guidelines
- No automated test suite is present.
- Validate changes manually in-game and check the SMAPI log for regressions.

## Commit & Pull Request Guidelines
- Recent history uses prefixes like `feat:`, `fix:`, `docs:`, `chore:`, `logs:`—follow the same style.
- PRs should include a concise description, steps to test, and any relevant screenshots/log snippets when UI or menu behavior changes.

## Dependencies & Setup Notes
- Requires .NET 6 SDK.
- Docs build requires Ruby + `kramdown`.
- Project Fluent is required at runtime for localization support.
