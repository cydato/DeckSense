# DeckSense

Hardware sensors and FPS on your Elgato Stream Deck - without HWiNFO, and
without a kernel driver.

> **Version 1.0.0.** The sensor service, the Stream Deck plugin and the setup
> window are in daily use on the author's machine, and HWiNFO has been
> uninstalled. [Download below](#installation).

## Why this exists

Two problems with the usual setup:

* **HWiNFO's free edition shuts its shared memory off after 12 hours.** The
  common workaround - restarting it from a scheduled batch file - is
  unreliable.
* **SMART polling can cause DPC latency spikes.** On the author's machine,
  HWiNFO querying one particular NVMe drive produced micro-stutters and audible
  crackling in audio.

DeckSense is a deliberately small replacement: a background service that reads
a fixed set of sensors on its own schedule, and a Stream Deck plugin that draws
the keys.

**The first rule of this project is that it must not cause latency problems.**
One sensor fewer, or updated less often, beats a single stutter.

## What it reads

| Sensor | Source | Kernel driver |
| ------ | ------ | ------------- |
| CPU usage (%) | PDH, `\Processor(_Total)\% Processor Time` | no |
| CPU utility (%) | PDH, `\Processor Information(_Total)\% Processor Utility` | no |
| RAM used (MB) | `GlobalMemoryStatusEx` | no |
| GPU load, temperature, fan (%) | LibreHardwareMonitor via NVAPI | no |
| GPU VRAM used (MB), power (W), hot spot and VRAM temperature (°C) | LibreHardwareMonitor via NVAPI | no |
| CPU temperature (°C) | LibreHardwareMonitor via PawnIO | **yes, opt-in** |
| FPS and frame time | RivaTuner Statistics Server shared memory | no |
| Drive temperatures (°C) | Windows Storage WMI, per drive | no (admin required) |

CPU utility is the clock-adjusted figure that Task Manager shows; it can exceed
100 %. FPS is averaged over roughly one second, while frame time is the
*last* rendered frame - the two legitimately disagree, and
`1000 / frametime` is **not** the FPS value.

### CPU temperature is deliberately missing

Reading a Ryzen's temperature requires an MSR, which requires ring 0, which
requires a kernel driver - in practice PawnIO, as used by LibreHardwareMonitor.

This project targets a machine that plays online games, and anti-cheat systems
block kernel drivers they do not trust. Riot's Vanguard has blocked MSI
Afterburner's `RTCore64.sys` even with the game closed, because Vanguard loads
at boot. PawnIO's signing certificate is also used by cheat software, which is
why FACEIT blocks it.

One sensor out of fourteen is not worth that risk. If you disagree, install
PawnIO and set `hardware.cpu` to `true` - the service picks it up on the next
start, no code change needed.

## How it stays out of the way

| Property | DeckSense |
| -------- | --------- |
| Kernel driver | none |
| Game hooks | none |
| Code injection | none |
| `OpenProcess` on games | never |
| Reading game process memory | never |
| Network | `127.0.0.1` only |

The foreground window's process ID comes from `GetForegroundWindow` and
`GetWindowThreadProcessId` - **no process handle is ever opened**. Frame rates
are read from the shared memory section that RTSS itself creates; the game is
never touched.

One dependency remains: RTSS does hook games. That is RTSS's business, and you
already run it if you want an on-screen display. If an anti-cheat blocks RTSS,
DeckSense simply reports FPS and frame time as unavailable and keeps running.

### Measured impact

LatencyMon 7.31, with all seven drives being polled:

| Driver | Highest DPC execution |
| ------ | --------------------- |
| `storport.sys` | 57 µs |
| `stornvme.sys` | 23 µs |
| `storahci.sys` | 5 µs |
| `disk.sys`, `nvmedisk.sys` | 0 |

For comparison, the graphics stack on the same run reached 1582 µs. The storage
path stays in the microsecond range.

Resource use in the **full configuration** - all eight drives, 21 sensors,
measured over 40 minutes: **0.099 % CPU** across 32 cores, 49 MB of private
memory, and a working set that went from 90 MB down to 74 MB rather than up.

The managed heap is compacted every five minutes, so **memory use does not grow
with uptime**. Without that it does: at this allocation rate .NET's default
policy on a machine with plenty of RAM never collects at all, and the figure
climbed by roughly 1 MB per minute. Note that this is a deliberate departure
from the usual advice never to call `GC.Collect` - a tool meant to run for days
is the case where it earns its keep.

Across that run it stayed at 2.3 MB: roughly 6 MB is allocated between
compactions, and each compaction takes 0 to 16 ms. For comparison, the figure
that started this work was 110 → 126 → 139 MB with **zero** collections.

## HTTP API

The service exposes everything it reads on `http://127.0.0.1:5891`, loopback
only - never `0.0.0.0`. Verified: the machine's LAN address refuses the
connection.

```
curl http://127.0.0.1:5891/api/sensors
curl http://127.0.0.1:5891/api/sensors/cpu.usage
```

```json
{
  "generatedAt": "2026-10-29T18:27:25.779Z",
  "version": "0.7.0",
  "sensors": [
    { "id": "cpu.usage", "name": "CPU Usage", "category": "cpu",
      "value": 2.6, "unit": "%", "available": true,
      "updatedAt": "2026-10-29T18:27:25.779Z" }
  ]
}
```

A request **never** triggers a hardware read - it only returns the cache.
Otherwise the number of Stream Deck keys would decide how often your drives get
polled. `value` is `null` exactly when `available` is `false`, never `0`.

Short read failures are bridged: if a reading fails while the previous one is
still recent, the old value stays, `available` remains `true`, and the sensor
carries `"stale": true` with its original `updatedAt`. Idle hard drives do this
regularly - they decline the temperature query rather than spin up. Drives hold
for 60 seconds, per-second sensors for 3, and FPS and frame time not at all: no
game running is information, and a held frame rate would simply be wrong.

No authentication and no CORS: binding to loopback is the boundary. The fields
are described above; there is nothing else to the format.

## Requirements

* Windows 10 or 11, 64-bit
* .NET 10 runtime
* An Elgato Stream Deck (any size), with Stream Deck software **7.1 or newer**
* RivaTuner Statistics Server, if you want FPS and frame time
* Administrator rights, if you want drive temperatures

## Installation

Grab the two files from the
[latest release](https://github.com/cydato/DeckSense/releases/latest):

| File | What it is |
| ---- | ---------- |
| `DeckSense-1.0.0-win-x64.zip` | the sensor service and the setup window, one executable each |
| `com.cydato.decksense.streamDeckPlugin` | the Stream Deck plugin |

There is no installer. Unpack the ZIP anywhere and run `DeckSense.Setup.exe`
from it. If you would rather build it yourself, see
[building from source](#building-from-source) and run the setup window from
your own publish folder instead.

In that window:

1. Check the target folder. The default is `C:\Program Files\Cydato\DeckSense`.
2. **Install program** - copies the files. Windows asks for administrator
   rights; only the copying runs elevated, not the window.
3. **Set up autostart** - creates a scheduled task that starts the service when
   you log in. **Show task as XML** prints exactly what will be registered,
   before anything is.
4. **Start service** - or just log out and back in.

Both steps are reversible from the same window: *Remove autostart* deletes the
task, and uninstalling is deleting the folder.

### The Stream Deck plugin

The plugin is a **separate file** and is not touched by the steps above. That
separation is deliberate: plugins live in the Stream Deck application's own
folder and need no administrator rights, while the service writes to
`Program Files` and does.

**Double-click `com.cydato.decksense.streamDeckPlugin`** from the release and
the Stream Deck application installs it and restarts itself. No administrator
rights, nothing to configure.

To build it yourself instead:

```
cd plugin
npm ci
npm run build
npm run pack
```

That produces the same file in `artifacts\`.

Then drag the **Sensor** action onto a key and pick a sensor from the dropdown.
The list comes live from the running service, so start the service first. You
can override the title, turn the history graph on or off, and set thresholds
per key.

A key shows `--` whenever the service is unreachable or its value is
unavailable. It keeps retrying; it never shows `0` for a missing value.

One thing to know if you file an issue: **the log file is in German.** The
windows, dialogs and help text are English, but the service and the plugin log
in German, because that is the language of the people who maintain this. Paste
the lines in anyway, they carry timestamps, sensor IDs and driver names.

**To update later, run `DeckSense.Setup.exe` from the newly built folder, not
the installed one.** The source is always the folder the running
`DeckSense.Setup.exe` sits in, so starting the installed copy would make source
and target the same folder - the window says so instead of doing anything. A
service running from the target folder is stopped and restarted around the
copy, because its executable cannot be overwritten while it runs. Your
`appsettings.json` is kept.

**Do not run it from a network drive.** The scheduled task fires at logon, when
a share may not be connected yet - the service would simply not be there.

Why no installer: this tool reads SMART data and needs administrator rights.
An executable you place somewhere, plus a button that creates a scheduled task,
is easier to inspect than a setup program. An unsigned installer would trigger
SmartScreen anyway.

## Building from source

```
git clone https://github.com/cydato/DeckSense.git
dotnet build src/DeckSense.slnx
dotnet run --project src/DeckSense.Service -- --once
```

Needs the .NET 10 SDK. The solution uses the `.slnx` format, which Visual
Studio 2026 opens directly.

| Argument | Effect |
| -------- | ------ |
| *(none)* | runs silently, logs to file, keeps values cached |
| `--once` | take one reading, print a table, exit |
| `--diag` | run continuously and print one line per tick |
| `--sensors` | list every sensor LibreHardwareMonitor can see, then exit |
| `--help` | usage |

## Configuration

**There is nothing to configure to get started.** On first run DeckSense detects
the drives in your machine and writes an `appsettings.json`. An existing file is
never overwritten - not on first run, and not when you install a newer build
over an old one.

Where that file lives depends on where the program lives:

| Program is in …                   | Config and `logs/` are in …      |
| --------------------------------- | -------------------------------- |
| `Program Files`                   | `%ProgramData%\Cydato\DeckSense` |
| anywhere else, e.g. a ZIP you unpacked | next to the executable      |

This is decided by the path, not by trying to write: the service runs elevated
from the scheduled task and *could* write into `Program Files`, which would mean
the same install looked for its config in two different places depending on how
it was started. The log notes which folder it picked at startup.

To see what it found, without changing anything:

```
DeckSense.Service.exe --detect
```

Comments are allowed in the file - the loader skips them. The repository ships
`appsettings.example.json` for reference; the real file is not in the
repository, because it contains serial numbers.

Drives are matched by **serial number**, never by drive number, because drive
numbers shift when you change ports. Two lists control what is touched:
`storage.allowed` is the only thing ever queried, and `storage.excluded` is a
hard veto that wins even if a drive appears in both - useful if one of your
drives misbehaves when its SMART data is read.

Generated sensor IDs combine the model name with the last four characters of
the serial number, for example `storage.ct1000t700ssd3-b009`. The suffix is
always present, not just when two drives share a model name: otherwise adding a
second identical drive would change the first one's ID and break its key
assignment.

USB devices and drives reporting no size are **not** enabled automatically - those are card readers and removable bays, and they do not report a usable
temperature. They appear as comments in the generated file so you can add them
by hand if you want.

## About the author

**I am not a developer.** I built this with AI assistance - vibe coding, as
people call it - plus a lot of money and a lot of Cola Zero Zero.

I am telling you this because it matters for a program that asks for
administrator rights and reads SMART data from your drives. Please judge it on
that basis: read the source and decide for yourself whether you want to run it.
It is small enough to read in an evening, and the parts that touch your
hardware are deliberately narrow: two lists in one config file decide which
drives are ever queried, and nothing outside them is touched.

The numbers above are measured, not estimated, and the measurements that went
against my expectations are in there too. The CPU temperature section exists
because the first answer was wrong.

If you are an actual developer and see something broken, an issue or a pull
request is very welcome.

## License

[MIT](LICENSE). Bundled third-party components keep their own licenses and are
listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md):
LibreHardwareMonitorLib is MPL-2.0. The RTSS shared memory layout was
reimplemented from the SDK header; no RTSS code is redistributed here.
