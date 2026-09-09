# Hindsight

Hindsight answers the question you can only ask afterwards: *what just made my PC lag?*
Task Manager shows you the present, and by the time you have opened it the stutter is
already gone. Hindsight runs quietly in the tray and keeps a rolling recording of the last
half hour of your machine — every second, per process: CPU, GPU, file and disk I/O, network
and memory. When something goes wrong you scroll back to the moment it happened and see
which process was responsible, what it was doing, and why it looked unusual.

## Highlights

- **A rolling window of the recent past.** A ring buffer keeps the last N minutes (30 by
  default) at one sample per second. It costs almost nothing and it is always already running.
- **Per-process detail.** CPU, GPU and GPU memory, file I/O, physical disk, network, private
  bytes, handles, threads and hard faults — collected from ETW, Windows performance counters
  (PDH) and per-second process snapshots.
- **Anomalies in plain English.** Hindsight marks unusual stretches on the timeline and
  writes one sentence per anomaly naming the processes that caused it, the app you had in
  front of you, and whether the machine was idle or on battery.
- **Context, not just numbers.** Service names behind a `svchost.exe`, the DNS names a
  process looked up, the endpoints it talked to and the files it touched.
- **Automatic markers.** Windows Update, Defender scans, scheduled tasks, USB plug and
  unplug, display off and on, lock and unlock, power source changes — drawn on the timeline
  so you can tell "my PC" from "something Windows started".
- **Recordings.** Save a stretch to a `.hsrec` file, reopen it later, export it, or put two
  recordings side by side and compare them.
- **Easy and Professional.** Easy is a short answer at a glance; Professional adds every
  lane, column, tab and threshold. The switch is instant, no restart.
- **Out of the way.** Lives in the tray, and a global hotkey drops a marker at the moment
  something felt wrong, so you can find it again afterwards.

## Requirements

- Windows 10 or 11, x64
- Administrator rights: the ETW session needs them. Without elevation Hindsight still
  records CPU and memory, but not file I/O or network — the status bar then says
  "ETW unavailable".
- .NET 8 Desktop Runtime, unless you use the self-contained build (which brings its own).

## Getting started

The quickest way is the self-contained `Hindsight.App.exe` from the Releases page: one file,
no installation, no runtime to install. Double-click it and confirm the elevation prompt.

From source:

    dotnet build Hindsight.slnx      # build
    .\start.ps1                      # start elevated, tray only
    .\start.ps1 -Show                # start elevated and open the window
    .\publish.ps1                    # self-contained dist\Hindsight.App.exe

`dotnet run` cannot start the app: the manifest asks for elevation, so it has to be started
through a UAC prompt. The solution file is `Hindsight.slnx`.

## Using it

**Live** shows the stat cards, the timeline and the culprits table. Each column is one
second: spikes are orange, gaps in the recording grey, markers are vertical lines. Click a
second to see which processes were busy in it, with parent chain and command line. Zoom with
the mouse wheel (hold Ctrl for finer steps), pan by dragging or with the arrow keys, and use
Fit and Follow to reset the view or re-pin it to the newest data. "Add marker" is on the page,
in the tray menu and on the hotkey.

**Recordings** is where you start and stop a named recording, or freeze the current buffer
with "Save last N min". Recordings are listed with duration and size; from there you can
open, rename, delete, export (ticks CSV, processes CSV, JSON) or select two and compare them.

**Analysis** opens a recording — or the live buffer — with the same timeline and tabs for a
single Second, an Overview, a selected Range, the Anomalies, a single Process, and Context.
Shift+drag a range on the timeline to load it into the Range tab. Anomalies appear as bands;
click one to zoom to it. The Process tab overlays one process curve on the timeline.

**Context** answers "why". Pick a second, then a process in the culprits table, and the tab
lists the DNS names it looked up, the remote endpoints it talked to and the files it touched
in that minute, with lookup counts and byte totals. Process names carry the services running
inside them, and the tooltip shows the file description and company.

**Compare** puts two recordings next to each other: Professional shows synced timelines and
both anomaly lists, Easy shows a summary and a difference table.

**Settings** covers buffer length and sample interval, the data folder, spike thresholds,
anomaly sensitivity and minimum anomaly duration, ranking weights, theme, start with Windows,
and the detail level (Easy or Professional). The marker hotkey is set by clicking the box and
pressing the combination; a single key such as F9 works too, but it is then unavailable to
every other program. Two toggles control what context is captured — "Capture file paths" and
"Capture DNS names and endpoints" — and command-line storage can be turned off as well.

## How it works

A recorder ticks once per second and writes one fixed-size record into a ring buffer on disk,
so the oldest second falls out as the newest arrives. Each tick is assembled by an aggregator
from several sources: an ETW session for file I/O, disk, network, DNS and process events;
PDH performance counters for GPU, CPU clock, disk latency, commit and paging; and a per-second
process snapshot for CPU, memory, handles, threads and faults. Details that are too fine to
keep per second — file paths, DNS names, endpoints — are rolled up per process and per minute.

An anomaly is a stretch of seconds where a metric either crosses its absolute threshold, or
rises far above its own recent baseline: above the rolling median by k times the median
absolute deviation, and clearly above a small noise floor. Consecutive anomalous seconds are
merged into one anomaly, very short ones are dropped, and the severity comes from how far the
peak sits above the baseline.

## Data and privacy

Everything stays on your machine. Hindsight has no accounts, no telemetry and no network
calls of its own; nothing is uploaded anywhere. All data lives under
`%LocalAppData%\Hindsight` (the folder is configurable): `settings.json`, the ring buffer
`ring.bin`, `identities.jsonl`, `markers.jsonl`, `context.jsonl`, the `Recordings` folder and
a small log file.

What is stored is what a diagnosis needs: process names, image paths and command lines,
per-second resource numbers, and — for the context view — DNS names, remote `ip:port`
endpoints and file paths. That is genuinely personal data: your command lines and the sites
you visit are in there. So the capture is under your control. "Capture file paths" and
"Capture DNS names and endpoints" each switch their category off entirely, command-line
storage has its own switch, and window titles are never recorded at all. Recording files are
self-contained, which also means: think before you share one.

## Recording file format

A `.hsrec` file is a ZIP holding `meta.json` (name, start and end, sample interval, core
count, tick count, format version), `ticks.bin` with the fixed-size per-second records,
`identities.jsonl` with the processes those ticks refer to, and `markers.jsonl` plus
`context.jsonl` with one JSON object per process and minute. Files are self-contained and can
be copied between machines. Older files still open; fields that did not exist yet come back
empty, and the Context tab says so.

## Building and tests

    dotnet build Hindsight.slnx
    dotnet test Hindsight.slnx

The build is warning-free, and the tests need no elevation — the Win32 and ETW tests run
against the real system. `docs/manual-checklist.md` lists the checks that only a real desktop
session can cover.

If you want to plug in your own heuristics, `ISpikeDetector` and `IProcessRanker` in
`src/Hindsight.Core/Analysis` are the extension points; the shipped implementations read the
Settings page.

## License

MIT — see [LICENSE](LICENSE).
