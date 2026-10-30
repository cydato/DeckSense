# Third-party components

DeckSense itself is MIT licensed, see [LICENSE](LICENSE). It depends on the
following packages, which carry their own licenses:

* LibreHardwareMonitorLib - Mozilla Public License 2.0
  https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
* System.Management - MIT License (Microsoft)

The RTSS shared memory layout used by this project was reimplemented from
RTSSSharedMemory.h, which ships with the RivaTuner Statistics Server SDK. No
RTSS code or headers are redistributed here.

Nothing from these projects is modified. The NuGet packages are restored at
build time and shipped alongside the executables, each under its own license.
