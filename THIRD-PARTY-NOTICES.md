# Third-party notices

Chronora is Apache-2.0. This file records what else it uses, and — for the one thing it
does not ship — what that distinction means.

## ExifTool — not distributed

Chronora reads and writes photo and video capture dates by running **ExifTool** by Phil
Harvey (<https://exiftool.org>), licensed under the GNU General Public License v1 or later,
or the Artistic License.

**No part of ExifTool is included in Chronora, in its repository, or in any Chronora
release.** The release archive contains no third-party binary and no GPL code, and CI
fails the build if anything named `exiftool*` other than Chronora's own manifest appears
in the publish output.

Chronora runs ExifTool as a separate process over its documented `-stay_open` interface.
A copy reaches the machine one of three ways, all chosen by the person using it:

1. A copy already installed — found on `PATH`, or where winget or Chocolatey put it. This
   one stays entirely theirs: Chronora does not manage, update or remove it.
2. A copy downloaded on request into `%LOCALAPPDATA%\PaulTechGuy\Chronora\exiftool\`,
   after an explicit choice, pinned to the version and SHA-256 in
   [`docs/exiftool.json`](docs/exiftool.json), and removable from Settings.
3. A copy pointed at by hand through **Browse**.

Chronora works without it. Every file-date feature is unaffected, and the photo and video
surfaces report that the helper is not available rather than disappearing.

## Packages

Resolved from NuGet at build time and subject to their own licences, none of which are
copyleft:

| Package | Licence |
|---|---|
| Microsoft.WindowsAppSDK.{Base, DWrite, Foundation, InteractiveExperiences, Runtime, WinUI} | MIT |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Data.Sqlite | MIT |
| SQLitePCLRaw.bundle_e_sqlite3 | Apache-2.0 |
| SQLite itself, as built by SQLitePCLRaw | Public domain |
| Microsoft.Extensions.{Hosting, DependencyInjection.Abstractions, Logging.Abstractions, Options} | MIT |
| Serilog, and the Extensions.Hosting, Settings.Configuration, Sinks.Async, Sinks.Debug and Sinks.File packages | Apache-2.0 |
| xunit.v3, xunit.runner.visualstudio *(tests only)* | Apache-2.0 |
| Microsoft.NET.Test.Sdk *(tests only)* | MIT |
| Shouldly *(tests only)* | BSD-3-Clause |

The Windows App SDK is referenced as individual components rather than the umbrella
package, which is why it appears here as six entries.

## Prior work by the same author

Chronora's shape borrows from two earlier projects of Paul Carver's, both his own code:
**PaulTechGuy.Marqora** (the build pipeline, installer and window conventions) and
**PaulTechGuy.FileTouch** (the multi-rule model, the pattern filters, and the file/
directory/root toggles).
