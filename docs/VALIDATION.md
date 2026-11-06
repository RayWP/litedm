# LiteDM validation record

Implementation session: November 6, 2026 (Asia/Taipei).

## Automated checks

- Release solution build on Windows x64: successful, zero warnings/errors.
- .NET behavioral tests: 35 passed, none skipped.
- Node extension regression tests: 5 passed.
- C# whitespace formatting verification: successful.
- PowerShell setup scripts: parser checks successful.
- JavaScript background/popup scripts: syntax checks successful.
- Self-contained publication: successful. The build script checks for both executables, the app's PRI index, and both compiled XAML files.

Download coverage includes segmented and single-stream content hashes, ignored ranges, chunked unknown lengths, empty files, redirects, interrupted responses, permanent HTTP errors, safe resume, checkpoint recovery, changed validators, queue concurrency/FIFO behavior, paused queue reordering, collision suffixes, and cancellation cleanup. A fresh Last-Modified value without a strong ETag is deliberately downloaded through one stream.

Persistence tests cover defaults, corrupt/unsupported files, invalid limits, settings/history round trips, terminal-history limits, and retention of unfinished/failed jobs. Protocol tests cover short reads, malformed/truncated/oversized frames, exact extension origins, request validation, isolated pipe forwarding, cancellation of hanging replies, and malformed desktop replies. Three executable tests demonstrate C# 14 syntax.

## Runtime and browser checks

- The app was launched from its build output after correcting the native DPI manifest.
- The published app was launched through the actual native host after fixing publication of compiled XAML resources.
- Edge: extension loaded in an isolated test profile; popup rendered without horizontal overflow; native `show` and `addDownload` requests returned acceptance.
- Chrome 156: extension loaded in an isolated test profile with CDP `Extensions.loadUnpacked`; unsupported URLs were rejected, `show` returned acceptance, and an HTTP link sent through the background handler produced the popup's “Sent to LiteDM for review” status.
- Browser tests used the standard-input/output native host executable and the running app's current-user named pipe. They did not replace the native host with a mock.
- Tests confirmed handoff acceptance. Approval of the desktop review dialog and completion of a download through that UI have not been verified.

Chrome's test browser was launched without Playwright's default `--disable-extensions` argument. The ordinary user setup remains Load unpacked plus the per-user registration script; no browser debugging flags are needed for normal use.

The repository extension's development ID was registered for both browsers during testing. Installing from a different directory can produce a different ID, so users should run registration with the ID displayed in their own browser. Tests did not install the extension into a personal browser profile.

## Design review status

Direction: native Windows download utility, light blue and silver, ENERGY 1 / RHYTHM 1 / MOTION 1. The interface uses native controls, system typography, a visible URL label, stable transfer rows, explicit errors, and light/dark/system appearance settings. The stock tray icon is a development placeholder.

The source palette was checked numerically using WCAG relative luminance: light secondary text `#50677D` on silver `#E8EDF2` is approximately 4.98:1; dark ink `#17324D` on light blue `#DCEFFC` is approximately 11.12:1. The dark action button uses dark ink on its light blue background instead of white text. These measurements do not substitute for inspecting all rendered control states.

The Anti Slop delivery gate remains open because the Windows desktop was locked during desktop capture. Computer Use guidance requires stopping interaction until the user unlocks Windows. No claim of a completed visual/interaction signoff is made.

Pending desktop checks:

- Add/review/cancel flow and destination folder picker.
- Pause/resume/retry/restart/cancel through the visible controls.
- Light, dark, and system appearance at normal and minimum window sizes.
- Keyboard focus and navigation while progress updates.
- Window close-to-tray, tray Open/Exit, and restored state after Exit/relaunch.
- Multiple browser requests producing sequential review dialogs.

## Known boundaries

This version handles direct HTTP/HTTPS files and signed links. It does not copy browser credentials, intercept automatic downloads, extract videos, or provide a web UI. Partial segments plus final assembly temporarily require additional disk space. Speed improvements depend on the server and connection; no speedup ratio is claimed.

The local build and test results above were observed. The GitHub Actions workflow has been added but has not been run on GitHub during this session.
