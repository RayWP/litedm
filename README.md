# LiteDM

A Windows desktop download manager built with .NET 10 and WinUI 3. This is a learning project with a light blue and silver interface, resumable downloads, and an optional Chrome/Edge link handoff extension.

Start with the [study notes](docs/STUDY-NOTES.md) for a guided code walkthrough, intermediate/advanced SWE concepts, tested C# 14 examples, and exercises.

## Build and run

Requirements:

- Windows x64, Windows 10 build 19041 or later; Windows 11 is the main development environment.
- .NET SDK 10.0.201 or a later stable 10.0 SDK (`global.json`).
- Windows SDK and the Windows desktop/WinUI build prerequisites. If you use Visual Studio, open `LiteDM.slnx` in **Visual Studio 2026** with the **WinUI application development** workload.

Visual Studio 2022 can report that `Microsoft.NET.Sdk` could not be found even when `dotnet --version` works. Its MSBuild 17.14 cannot load LiteDM's SDK, which requires MSBuild 18.0 or later. Build from PowerShell with the .NET CLI or open the solution in Visual Studio 2026. [Microsoft's SDK compatibility table](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs)
- Node.js if you want to run the browser-script tests; the extension itself needs no Node installation or bundling.

From the repository root:

```powershell
./scripts/build.ps1
./artifacts/LiteDM/LiteDM.App.exe
```

The build script restores, builds, tests, and publishes an unpackaged self-contained Windows x64 folder. Keep the folder together: the desktop executable and native messaging bridge depend on neighboring files. Close LiteDM through its tray **Exit** command before republishing to the same folder.

For individual checks:

```powershell
dotnet build LiteDM.slnx -c Release -p:Platform=x64
dotnet test tests/LiteDM.Tests/LiteDM.Tests.csproj -c Release -p:Platform=x64
node --test tests/browser/background.test.cjs
```

## Downloading

Paste a direct HTTP/HTTPS file URL and choose **Add download**. Review the filename and destination, then start. Downloads go through a FIFO queue, with one active download and four segments by default. Settings change the concurrency limits and the light, dark, or system appearance.

Segmentation requires a known file length, actual byte-range support, and a usable resource validator. Files below 8 MiB use one transfer. Unsupported servers use a single stream. More segments can help with some server limits; they do not increase the bandwidth of your internet connection.

Pause/resume reuses only compatible checkpointed bytes. A changed file or a server without safe resume support requires **Restart**. Retryable network errors receive bounded retries. Existing destination files are kept; a numbered alternative is chosen when needed.

Use **Remove** on a paused, failed, or cancelled transfer, or **Remove stopped** to clear all of them. Confirming removes their records and partial files.

Closing the window keeps transfers running in the tray. Use **Open LiteDM** to restore it or **Exit** to checkpoint transfers and stop the process. On the next start, interrupted jobs are restored paused.

Public direct file links and signed URLs are supported. Browser login cookies, authentication transfer, automatic browser download interception, video extraction, and a web interface are outside this version.

## Chrome and Edge extension

1. Build first, then open `chrome://extensions` or `edge://extensions` in your browser.
2. Enable Developer mode and choose **Load unpacked**. Select this repository's `extension` folder, or the copied `artifacts/extension` folder. Keep using the same folder so its development ID stays stable.
3. Copy the extension's ID and register its native host for the current Windows user:

```powershell
./scripts/register-browser.ps1 -Browser Edge `
  -PublishDirectory ./artifacts/LiteDM `
  -EdgeExtensionId YOUR_32_CHARACTER_EXTENSION_ID

./scripts/register-browser.ps1 -Browser Chrome `
  -PublishDirectory ./artifacts/LiteDM `
  -ChromeExtensionId YOUR_32_CHARACTER_EXTENSION_ID
```

For both browsers in one call, use `-Browser Both` with both ID parameters. No administrator account is needed. The scripts write only LiteDM's per-user Native Messaging registration and corresponding manifests in the publish folder.

Right-click an HTTP/HTTPS link and choose **Download with LiteDM**. The app starts if necessary and opens a review dialog. The extension's popup reports whether the request reached the app; that acknowledgement does not mean a download completed. Open the popup to retry a failed handoff or open LiteDM.

If the browser reports that the host is missing, verify the extension ID, register again after moving the publish folder, and check that `LiteDM.Bridge.exe` and `LiteDM.App.exe` are beside one another. A changed extension ID also requires registration again.

Remove the host registration before moving or deleting an installation:

```powershell
./scripts/unregister-browser.ps1 -Browser Both -PublishDirectory ./artifacts/LiteDM
```

The extension can then be removed from the browser's extensions page. Nothing is published to an extension store by this repository.

## How the code fits together

| Component | Responsibility |
| --- | --- |
| `src/LiteDM.Core` | Download lifecycle, FIFO scheduling, HTTP validation, transfer, checkpoints, final file commit |
| `src/LiteDM.App` | WinUI controls, dialogs, settings/history, tray lifecycle, single-instance coordination, pipe server |
| `src/LiteDM.Bridge` | Browser Native Messaging framing, origin validation, process startup, timed pipe handoff |
| `extension` | Manifest V3 context menu and visible handoff status |
| `tests/LiteDM.Tests` | Local HTTP fixture, transfer/persistence/protocol checks, executable C# 14 study examples |
| `tests/browser` | Extension regression tests using Node's built-in test runner |

The browser bridge uses the standard Native Messaging length-prefixed UTF-8 JSON protocol. It forwards one newline-delimited JSON request through a pipe restricted to the current Windows user. Shared DTOs and protocol names live in `Contracts.cs`. Download logic has no WinUI dependency.

Settings and download records are stored in `%LOCALAPPDATA%\LiteDM\state.db` using SQLite. On first launch the app imports an existing `state.json` into the database. The left sidebar shows the latest 20 history entries and supports file-name search and status filtering. Partial data is kept in a job-specific `.litedm-<id>` directory beside the selected destination. Signed URL query strings are part of saved job URLs. Cancellation removes the job's temporary data.

Exit the older app from its tray menu before launching an updated copy. Run `./scripts/build.ps1` to update the standard publish folder and keep the existing browser host paths. If you keep a separate publish folder, register the browser host with that folder for browser launches.

Per-segment storage and final assembly favor readable implementation. Assembly temporarily needs space for both the segments and the assembled file. There is no installer, auto-update service, or automatic startup registration.

See [DESIGN.md](DESIGN.md) for the interface choices and [docs/VALIDATION.md](docs/VALIDATION.md) for the checks performed and any remaining verification limits.
