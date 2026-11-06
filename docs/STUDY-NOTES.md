# Studying LiteDM: from working features to dependable software

These notes are a reading companion to this repository. They explain the decisions behind the code and give you exercises for improving it. Read the implementation alongside each section rather than treating the examples as a replacement for the source.

## 1. A useful reading order

1. `src/LiteDM.Core/Contracts.cs`: the vocabulary and boundaries.
2. `src/LiteDM.Core/DownloadManager.cs`: lifecycle, scheduling, transfer, and recovery.
3. `tests/LiteDM.Tests/DownloadTests.cs` and the HTTP fixture: what correct behavior means.
4. `src/LiteDM.App/MainWindow.xaml` and its code-behind: how state becomes an interface.
5. `src/LiteDM.App/LocalStore.cs`: persistence of settings and history.
6. `src/LiteDM.App/InstanceCoordinator.cs` and `TrayIcon.cs`: process and Windows integration.
7. `src/LiteDM.Bridge/NativeMessaging.cs`, then `extension/background.js`: communication across processes.
8. `tests/LiteDM.Tests/LanguageExamplesTests.cs`: executable C# 14 examples.

Before changing a method, write down its input, output, side effects, and failure paths. This habit matters more than memorizing syntax.

## 2. Boundaries: separate reasons for change

The core knows how to download files. It does not know about `TextBox`, browser tabs, or window handles. The desktop project translates user actions into core calls. The bridge translates browser messages into desktop requests.

This is separation of concerns with a practical test: can you run the download tests without starting a WinUI window? You should be able to.

An interface is not required for every class. A concrete `DownloadManager` with an injectable `HttpClient` is enough for this small application. Add an abstraction when you have a real alternate implementation or a testing boundary that needs one. More folders and interfaces do not automatically make a system easier to change.

`DownloadSnapshot` is a contract, not a handle to the engine's mutable internals. Its `record` and `init` properties allow a new value to represent a new observation:

```csharp
var paused = previous with
{
    State = DownloadState.Paused,
    BytesPerSecond = 0
};
```

The `with` expression makes a shallow copy. If you later put a mutable list inside this record, both records can still refer to that list. Immutability is something you design; the `record` keyword alone does not guarantee it.

**Exercise:** Add a display-only category to the desktop UI. Then explain why it does or does not belong in the core contract. Avoid adding category logic to the transfer loop unless it changes transfer behavior.

## 3. Model the download as a state machine

The states in `DownloadState` are more than labels. They decide which actions are legal. For example, Completed should not accept Pause, and a cancelled job must not continue writing bytes.

Describe each action using a precondition and a postcondition:

| Action | Precondition | Postcondition |
| --- | --- | --- |
| Start queued work | An execution slot is available | Exactly one worker owns the job |
| Pause | Job is active or queued | No worker writes after Pause finishes |
| Remove | Job is paused, failed, or cancelled | Its partial files and saved record are removed |
| Resume | Partial bytes are safe to reuse | Job joins the queue once |
| Restart | Previous worker has stopped | Old temporary bytes cannot enter the new result |
| Complete | Every expected segment is verified | A complete destination exists without overwriting another file |

The important distinction is between requesting an action and finishing it. Setting a cancellation token does not mean the worker has stopped. A correct pause path requests cancellation and awaits the worker before permitting another execution of the same job.

In `RemoveStoppedAsync`, the lifecycle lock serializes removal with resume and restart. The method rechecks the state after acquiring the lock and waits for any worker to finish. It deletes the job-specific partial directory before dropping the record. The UI then saves the remaining jobs to SQLite. If filesystem cleanup fails, the transfer stays available so the user can retry.

This gives you an invariant: **one job has at most one active execution**. A test that rapidly pauses and resumes is checking that invariant, not merely checking button behavior.

**Exercise:** Draw the actual allowed transitions from the public methods. Look for a path that can leave a job Queued without any future scheduler wake-up. Add a regression test before changing it.

## 4. Async I/O, concurrency, and ownership

`async` does not mean "run this on a new thread." When an HTTP read is waiting, `await` allows the caller to stop occupying a thread until there is work to do. Multiple network operations can be in flight without assigning a dedicated thread to each one.

There are two different concurrency limits here:

- How many jobs may execute at once.
- How many range requests one job may make.

With three active jobs and four segments per job, the app can create roughly twelve transfer requests, plus probing requests. Increasing both settings can overload a server or slow your own connection. Parallel requests do not create additional internet bandwidth.

A short `lock` protects decisions involving shared state: checking a job state, claiming an execution slot, and updating a queue. Do not hold that lock across network or disk waits. A `SemaphoreSlim` can serialize an asynchronous lifecycle operation because it supports `WaitAsync`.

`Task.WhenAll` waits for every task. It does not automatically cancel sibling tasks when one fails. For segmented work, consider this failure sequence:

1. Segment A discovers that the server changed the resource.
2. Segment B is still downloading.
3. The manager publishes Failed while B continues writing.
4. The user restarts, and two generations of work touch the same files.

The solution is ownership: one execution owns its cancellation source and all its children. Stop the children and await them before releasing that ownership.

`ConfigureAwait(false)` is useful in UI-independent library code because its continuations do not need the caller's UI context. It is not a command to run on a particular background thread. In the UI project, `DispatcherQueue` is the explicit boundary for changing WinUI controls.

**Exercise:** Set a breakpoint before and after an HTTP `await`. Inspect the thread IDs. Then explain why observing different IDs does not prove that async created a thread.

## 5. HTTP correctness: bytes are a protocol

A download manager needs stronger evidence than "the server returned success." A range response needs to describe the exact bytes requested. For a request such as `Range: bytes=100-199`, a successful partial response should identify that interval and the complete resource length.

Do not trust `Accept-Ranges` alone. A server can advertise support but ignore a request, and it can implement ranges without advertising that header. LiteDM probes actual behavior.

The resource also needs an identity. A strong ETag is useful for checking whether two responses represent the same version. Last-Modified is a less precise fallback: LiteDM requires the response Date to be at least 60 seconds later before trusting it for segmentation or resume. Otherwise it uses a single nonresumable stream. An `If-Range` request lets a server return a full response if old partial bytes are incompatible. The client must avoid appending that response to old bytes.

Compression is another boundary. Range offsets must correspond to the bytes written to disk. Requesting identity encoding and validating the response avoids combining offsets for one representation with bytes from another.

Think about this example:

```text
Saved bytes: first half of version A
Resumed bytes: second half of version B
Result length: correct
Result content: corrupt
```

File length alone cannot prove correctness. The fixture tests compare hashes because equal byte counts can hide different content. Read the [HTTP range specification](https://www.rfc-editor.org/rfc/rfc9110.html#name-range-requests) when changing probe or response validation logic.

Retries also need a policy. A temporary disconnect may be retried. A rejected URL, unavailable disk, or changed entity usually needs a different action. A catch-all retry can waste bandwidth and hide programming errors. Bound retries, use backoff, and preserve the reason for the final failure.

**Exercise:** Make the fixture return `200 OK` to a resumed range request. Confirm that the result is either a clean restart or a visible failure, never appended full-body bytes.

## 6. Crash recovery is a small transaction problem

History and checkpoints serve different purposes. History answers "which jobs should appear?" A checkpoint answers "which bytes can this job safely reuse?" A history percentage must never be treated as proof that those bytes exist.

### SQLite storage and migration

Read [LocalStore.cs](../src/LiteDM.App/LocalStore.cs) from `Load` through `Write`. The app keeps one settings row and one row per saved download in `state.db`. Each row stores a serialized snapshot, and download rows have a position so they load in the same order. This keeps the schema small and lets the migration reuse the existing C# records.

`Write` replaces the settings and download rows in a single transaction. If inserting a snapshot fails, the transaction rolls back the entire save, including the deletion of the previous rows. SQL parameters carry the snapshot text as a value, so a filename containing an apostrophe needs no special SQL escaping. The store uses a semaphore to serialize its operations and runs synchronous SQLite calls on a worker thread.

When the database has no settings row, `Load` checks for `state.json` and imports its settings and jobs in one transaction. The settings row marks a successful import. Subsequent loads use the database, and the old JSON file remains available. The store retains paused and failed jobs beyond the sidebar's 20-entry limit so the user can still resume or retry them.

Run `LegacyStateImportsOnceAndKeepsResumableJobs` in [LocalStoreTests.cs](../tests/LiteDM.Tests/LocalStoreTests.cs). The test changes the legacy file after the first load and opens a new store instance. It checks that the second load reads the imported record from SQLite.

The critical ordering is:

```text
write bytes -> flush the relevant data -> publish a checkpoint for those bytes
```

If metadata gets ahead of the files, recovery can skip missing bytes. If files get ahead of metadata, recovery can conservatively discard the uncheckpointed tail and download it again. Losing a small amount of progress is better than silently producing a corrupt result.

Several segment writers make this more subtle. Serializing a shared metadata object while another segment updates it can capture a combination that never existed as a durable state. Protect checkpoint creation and distinguish written offsets from checkpoint-safe offsets.

Writing a replacement JSON file and renaming it into place reduces the chance of leaving half a JSON document. It does not by itself promise survival of every power loss. File-system behavior, buffering, and flush semantics matter. Describe the actual guarantee rather than calling the operation "fully transactional."

The final assembled file is staged before being moved to the chosen destination. A collision check followed by a move still has a race: another program can create that name between the two operations. The move must refuse overwrite, and the caller must handle the resulting collision.

Per-segment storage keeps the code understandable, but assembly adds another pass over the file and temporarily uses additional disk space. A future random-access implementation can reduce that overhead at the cost of more complex checkpoint reasoning.

**Exercise:** Append extra bytes to a partial file after its saved checkpoint. Restore it and inspect which bytes are reused. Then truncate it below the saved offset and repeat.

## 7. UI state is an eventually updated view

The engine's state is authoritative. The UI receives snapshots and renders them. A progress number can arrive slightly later than the underlying write; that is acceptable. Allowing a stale UI command to start a duplicate worker is not.

Network callbacks publish snapshots from worker threads. Dispatch control updates to the UI thread. Move network, file assembly, and SQLite work out of synchronous button handlers.

It is tempting to clear and rebuild the entire list on every update. That can reset keyboard focus, create many objects, and make an accessible interface difficult to operate. Keep stable rows and update their content. Coalesce history writes so a fast transfer does not start a database transaction for every received buffer.

### Per-part progress

Follow `CopyCheckpointedAsync` and `ReportCheckpoint` in [DownloadManager.cs](../src/LiteDM.Core/DownloadManager.cs), then `UpdatePartBars` in [MainWindow.xaml.cs](../src/LiteDM.App/MainWindow.xaml.cs). After a writer flushes a part, the engine updates its saved offset under the checkpoint lock. It publishes the combined byte count and an immutable array of part counts in the same snapshot. The window creates one bar per part when the part count changes and updates their values in place. On resume, the engine reconstructs those counts from checkpoint files.

Overall progress is the sum of bytes downloaded divided by the sum of part sizes. If one part has 60 of 100 bytes and another has 0 of 50, overall progress is 40%. Averaging the two percentages would give 30% because it gives the two differently sized parts equal weight. A transfer using one connection has an empty part array and displays the overall bar.

### History as a filtered view

`DownloadSnapshot.HistoryStatus` maps the engine's job state to four labels. Completed jobs show **Finish**, paused jobs show **Paused**, and failed jobs show **Failed** or **Corrupt**. **Corrupt** means that a range response or saved checkpoint failed validation. The UI offers Restart for those jobs.

In `RecentHistory`, notice the order of operations. The method orders eligible jobs by recency, takes the latest 20, then applies the case-insensitive file-name search and optional status filter. Searching an older name therefore does not bring a 21st record into view. The test `RecentHistoryUsesStatusAndCaseInsensitiveFileNameWithinTheLatestTwenty` protects that behavior.

### Choose a destination before queuing

Both a pasted URL and a browser handoff pass through `ReviewUrlCoreAsync`. The dialog starts with the Downloads folder and lets the user type a full path or use the native folder picker. The primary button checks for a filename and a fully qualified path while the dialog is still open. After confirmation, `DownloadManager.AddAsync` prepares the directory and queues the job. This separates a cheap input check from the filesystem operation, which can still fail if the location is unavailable or unwritable.

Try cancelling the review for a new path, then repeat with confirmation in a disposable folder. Compare when the directory and the job record appear.

`async void` belongs at event-handler boundaries where the framework requires `void`. Most other asynchronous methods should return `Task` so callers can await completion and observe exceptions. An event handler still needs explicit error handling; otherwise a thrown exception can escape through the UI dispatcher.

Closing a window and stopping a process are separate actions. The tray lifecycle intentionally keeps the manager alive when the window is hidden. Explicit Exit needs a shutdown sequence: stop accepting work, stop transfers, save recoverable state, dispose integration resources, then exit.

**Exercise:** Navigate to Pause with the keyboard while a large transfer is updating. Verify that focus stays on the same control. Repeat after resizing and changing themes.

## 8. IPC: design a small protocol, not just a function call

The browser, bridge, and desktop are separate processes. Each can start late, exit early, send malformed input, or fail to respond. A local call can therefore fail even when the C# method itself is correct.

Native Messaging uses a byte-length prefix followed by UTF-8 JSON. A single stream read is not guaranteed to fill a buffer. Correct framing reads the complete header, validates the length before allocating, and reads the complete payload. Standard output belongs exclusively to that binary protocol; ordinary logging there would corrupt it.

The bridge-to-desktop pipe uses newline-delimited JSON. A JSON serializer escapes embedded newlines, so a serialized request fits one transport line. The pipe is restricted to the current Windows user. Browser host manifests separately list the allowed extension origins.

"Accepted" means the app received a request for review. It does not mean the user approved the destination or that a download finished. This distinction is the same one you encounter in asynchronous HTTP APIs and message queues.

Time out the entire operation, not only the connection attempt. A connected process that never replies can otherwise hang a bridge forever.

Direct URLs can contain signed query tokens. Saving job history means storing those URLs locally. Avoid logging them or copying them into bug reports. Browser cookies and login credentials are outside this version's protocol.

**Exercise:** Write a fake pipe server that accepts a connection but never responds. Confirm that the bridge exits with a useful failure. Then make it return malformed JSON.

## 9. Test the boundary where mistakes happen

The local HTTP fixture tests the actual HTTP stack and files, without depending on a public server's availability. It can deliberately return behaviors that are hard to reproduce manually: changing validators, malformed ranges, unknown lengths, redirects, and dropped connections.

A useful test has an observable result and a reason to fail:

- Hash equality checks content, not just a Completed state.
- Active-request counters check concurrency limits.
- Pause followed by a short wait checks that writing really stopped.
- Restore through a new manager checks that recovery does not depend on old in-memory objects.
- An existing destination checks that unrelated user data survives.

Prefer waiting for an observable condition with a timeout over a fixed sleep. Fixed sleeps often pass on one machine and fail on a slower CI worker. Still include a timeout so a broken state machine cannot hang the test run forever.

Test names should explain a guarantee. "ResumeNeverAppendsChangedResource" teaches the next reader more than "TestResume2."

## 10. What is actually new in .NET 10 and C# 14?

.NET is the runtime and library platform. C# is the language. This repository targets .NET 10 and explicitly selects C# 14 in `Directory.Build.props`. A new SDK does not make every modern-looking expression a new feature.

| Syntax/API | Introduced before C# 14? | Where to study it |
| --- | --- | --- |
| `async` / `await` | Yes | Transfer and IPC operations |
| `record`, `init`, `with` | Yes | Snapshot contracts |
| `required` | Yes | Required snapshot initialization |
| Collection expressions such as `[]` | Yes, C# 12 | Small collections in the app/core |
| Primary constructors | Yes, C# 12 | Small helper types where used |
| `await using`, `IAsyncDisposable` | Yes | Streams and manager shutdown |
| `field`-backed properties | New in C# 14 | Executable learning examples |
| Null-conditional assignment | New in C# 14 | Executable learning examples |
| Extension blocks/properties | New in C# 14 | Executable learning examples |

See Microsoft's [C# 14 overview](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14) for the release boundaries. The following examples are intentionally in `LanguageExamplesTests.cs`; they demonstrate syntax without adding unnecessary production abstractions.

### A property with a compiler-provided backing field

```csharp
public int Connections
{
    get;
    set => field = value is >= 1 and <= 16
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value));
} = 4;
```

`field` refers to the storage generated for this property. It lets one accessor contain validation without making you declare `_connections`. Validation occurs before assigning, so a rejected value leaves the previous value intact. The relational pattern and throw expression are older features used together with the new one. [Field-backed properties](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/field).

### Assign only if the receiver exists

```csharp
StudyPreferences? preferences = null;
preferences?.Connections = CalculateConnectionCount();
```

C# 14 permits the null-conditional access on the assignment's left side. If `preferences` is null, the assignment and the right-hand calculation are skipped. This can shorten optional UI updates, but it can also conceal a missing required dependency. Use an explicit exception when absence is an error. [Null-conditional assignment](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/member-access-operators#null-conditional-assignment).

### Add a computed extension property

```csharp
public static class StudyDownloadExtensions
{
    extension(DownloadSnapshot job)
    {
        public bool IsSuccessful => job.State == DownloadState.Completed;
    }
}
```

Code importing this namespace can use `job.IsSuccessful`. The extension does not modify `DownloadSnapshot`, add storage to it, or participate in virtual dispatch. It groups external behavior around a receiver type. Prefer an ordinary property on a type you own when that property belongs to its core meaning. [Extension members](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14#extension-members).

Run these examples:

```powershell
dotnet test tests/LiteDM.Tests/LiteDM.Tests.csproj --filter FullyQualifiedName~LanguageExamplesTests
```

The app also benefits from the current runtime and networking implementation without needing novel syntax in every method. Read [.NET 10 library changes](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries) when considering a specific API upgrade; measure a performance change rather than assuming a newer runtime solved an application bottleneck.

## 11. Two failures that a successful build did not catch

The first launch crashed in USER32 before the managed application constructor. The manifest had `PerMonitorV2` inside the legacy `dpiAware` element. That setting belongs in the newer `dpiAwareness` element with its own namespace; the legacy element accepts `true/PM`. The project compiled because a successful C# and XAML build does not prove that Windows can initialize the executable's native manifest.

The second failure happened only in the published folder. The build output contained compiled XAML and the app's PRI resource index, but publication initially omitted them. The app could compile and run from its build folder while failing to create the main window after distribution. `IncludeWinUIResourcesInPublish` in the desktop project explicitly includes those generated resources. Its MSBuild item metadata assigns each file its own output name.

These failures illustrate a testing ladder:

```text
type-check/build -> behavioral tests -> launch build output -> launch published output -> real integration
```

Passing an earlier step does not imply that the next one passes. The real Edge Native Messaging check exercised the extension, Windows registration, bridge executable, app startup, and pipe response together. A unit test for JSON serialization could not cover that chain.

When debugging, add temporary observations at boundaries rather than changing several unrelated settings at once. A minimal separate WinUI startup probe helped distinguish the environment from the application. Initialization milestone logs were removed after the issue was isolated; exception logging remains.

**Exercise:** Publish into a new empty directory, verify the `.xbf` and `.pri` files, and launch from that directory. Rename one generated resource in a disposable copy and inspect the failure. Explain why a test that only checks for the existence of the `.exe` would miss it.

## 12. A practice path

Work through these in order. Each exercise should include a failing test, the smallest implementation that makes it pass, and a short explanation of the tradeoff.

1. **Name hygiene:** test Windows reserved names and misleading extensions. Improve sanitization without allowing a filename to become a path.
2. **Retry policy:** distinguish temporary HTTP failures from permanent ones. Add a test for exhausted retries and explain which partial bytes remain safe.
3. **Progress smoothing:** compute a moving average for speed and ETA. Keep raw transferred-byte counts exact; measure how frequently the UI needs updates.
4. **Queue fairness:** add priority while preserving FIFO order among equal-priority jobs. Decide what happens to a currently running low-priority transfer.
5. **Diagnostics:** add structured events containing job IDs, state transitions, and durations. Redact URL query strings. Demonstrate how to investigate a failed resume.
6. **Recovery testing:** interrupt the process at several write/checkpoint/commit boundaries in a disposable test directory. Document each recoverable outcome and any remaining crash window.
7. **Performance investigation:** compare single-stream and segmented downloads against a rate-limited local fixture. Report throughput, memory, disk writes, and connection counts. Explain when extra segments make performance worse.
8. **Architecture refactor:** separate the transfer protocol from scheduling only after you can name a concrete source of complexity. Keep the existing behavioral tests unchanged while doing it.

For every change, ask: which invariant am I protecting, which failure does my test reproduce, and which complexity am I adding? Those questions are a useful bridge from intermediate C# to dependable software engineering.
