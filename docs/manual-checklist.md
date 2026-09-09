# Manual test checklist

Checks that cannot be covered by unit tests, because they need a real desktop session,
real hardware counters and a live ETW session. Run Hindsight elevated (`.\start.ps1 -Show`)
and walk through the list before tagging a release. Anything that fails is a release blocker
unless the section says otherwise.

## Startup

1. The app starts elevated without an error dialog and shows a tray icon with the tooltip "Hindsight – recording".
2. `logman query "Hindsight" -ets` lists the kernel providers plus Microsoft-Windows-Kernel-Process.
3. The window shows the Live / Recordings / Analysis / Settings navigation and the status bar reads "Recording · ETW active", with the tick time counting up.
4. Started without elevation, the status bar reads "ETW unavailable: Administrator rights are required" and the CPU and memory lanes still move.
5. Closing the window only hides it; the tray icon stays, "Open" brings the window back and "Exit" ends the process and removes the ETW session.

## Live view

6. After a minute the stat cards update every second with sparklines and every enabled lane is drawn.
7. A busy loop for ten seconds produces an orange column; clicking it lists the busy process on top with its parent chain and command line.
8. A short-lived process (for example `cmd /c dir C:\Windows\System32 >nul`) appears with the note "short-lived, estimated".
9. Copying a large file makes the File I/O lane and card spike, with the copying process as the culprit.
10. The GPU lane and card move while a game or a video plays, and the culprits table shows GPU percent and GPU memory per process.
11. The disk latency card reacts to a large copy, and on a laptop the CPU MHz value drops when the machine goes idle.
12. Wheel zoom centres on the cursor, dragging pans, the arrow keys pan, Fit resets the view and Follow re-pins to the newest tick.
13. With a ten-minute buffer the timeline keeps a fixed ten-minute window: new data enters at the right edge and empty space stays on the left while the buffer fills.
14. Selecting a second and clicking "Add marker" places the marker at that second, not at the current time.
15. The marker hotkey adds a marker; right-clicking a marker line offers "Delete marker …" and the line disappears.
16. Lock/unlock, suspend/resume and AC power changes add markers automatically.
17. After ten minutes of running, CPU stays below one percent and the working set does not grow noticeably while the machine is idle; the lost-event counter in the culprits header stays at zero.

## Recordings

18. "Start recording" with a name shows the REC indicator counting up in the status bar and "Stop recording" in the tray menu.
19. "Stop recording" saves the file, shows the "Recording saved" bar with an Open button and lists it on the Recordings page with a plausible duration and size.
20. "Save last N min" writes the current buffer to a file and lists it.
21. Rename, Delete (with confirmation) and the three exports (ticks CSV, processes CSV, JSON) all produce readable files with headers.
22. Changing the buffer length while a recording runs saves the recording, restarts the pipeline and clears the REC indicator.
23. The tray entry "Start recording…" opens the window on Live with the name box focused.
24. Restarting the app keeps the previous buffer content; a recording written by an older version still opens, with empty lanes for signals it does not contain.

## Analysis

25. Opening a recording shows the meta header, the timeline and the tabs for the current detail level.
26. Shift+drag on the timeline selects a range and the Range tab reports totals for that range only.
27. A CPU burn of about fifteen seconds shows up in the Anomalies tab with a one-line explanation naming the busy process; clicking the entry zooms the timeline and highlights the band.
28. Raising the anomaly sensitivity finds more anomalies on the same recording, lowering it finds fewer.
29. Detection on a one-hour recording finishes in about a second: the busy indicator disappears quickly.
30. The Process tab search overlays a process curve on the timeline and Clear removes it again.
31. Selecting two recordings and choosing Compare shows both: synced timelines and anomaly lists in Professional, a summary and a difference table in Easy, and the difference table follows the metric picker.
32. A recording without GPU or disk data reports "not recorded" for those rows and produces no anomalies for them.

## Context

33. svchost.exe rows show their service names, for example "svchost.exe (Dhcp, Dnscache)", and the Process cell tooltip shows description and company.
34. After a download, selecting that second and the downloading process lists the DNS names and remote endpoints with byte totals in the Context tab.
35. After a large file copy, the Context tab lists the file paths with byte totals for the copying process.
36. A virus scan adds markers with the shield glyph at its start and end; a scheduled task and a Windows Update add their own markers.
37. Plugging a USB device in and removing it adds two eject-glyph markers; turning the display off and on adds the moon and sun markers.
38. With "Capture file paths" off, no file paths are recorded; with "Capture DNS names and endpoints" off, no DNS names or endpoints are recorded. The per-second metrics keep running in both cases.

## Settings

39. Changing the CPU spike threshold takes effect within seconds, and changing the theme applies immediately.
40. Changing the buffer length or the sample interval reports that the pipeline was restarted, and the Live page shows the new buffer text and "Save last N min" value at once.
41. An invalid value (for example a buffer of 999 minutes) shows the validation error on Apply and saves nothing.
42. Pointing the data folder at a new directory moves the data files there after the pipeline restarts.
43. Clicking the hotkey box and pressing a combination shows it in the box; after Apply the old gesture no longer adds a marker and the new one does. A single key such as F9 also works, and is then unavailable to other programs.
44. If the hotkey is already taken by another program, the "Hotkey not registered" bar appears instead of failing silently.
45. "Start with Windows" on registers a scheduled task with the highest run level; off removes it again.
46. The Easy/Pro switch in the status bar and the level combo in Settings change the cards, columns, lanes and available tabs live, with no restart.
47. The Settings page scrolls with the mouse wheel down to the Apply button, and the Recordings page scrolls when the list is long.
