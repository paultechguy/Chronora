# Artwork

Three files, and which one is used where is not interchangeable.

| File | Used by | Why this format |
|---|---|---|
| `ChronoraLogo.svg` | Nothing at runtime. The **master** | Vector, so new icon sizes, banners or a favicon can be re-exported from it |
| `ChronoraLogo.png` | The README, and `src/PaulTechGuy.CN.App/Assets/` for the About window | See below |
| `ChronoraLogo.ico` | `src/PaulTechGuy.CN.App/Assets/` — embedded in the exe and set on each window | Windows takes an `.ico` for a window or shortcut icon and nothing else |

## Why the app does not ship the SVG

`SvgImageSource` is backed by Direct2D, whose SVG support covers **neither CSS class
selectors nor text elements**. An export using either draws the wrong thing, and writes
nothing to the log — so the failure is silent and looks like a layout problem.

This is not hypothetical for this file. `ChronoraLogo.svg` is a CorelDRAW export and opens
with a `<style>` block and `class=` attributes, which is precisely the case Direct2D
does not handle. Marqora hit this and moved to a bitmap for the same reason.

So: the vector is the master and stays here. The PNG is what ships.

## The icon

Six sizes — 16, 32, 48, 64, 128, 256 — all 32bpp with alpha. Windows picks different ones for
the taskbar, alt-tab, Explorer's views and the Settings ▸ Apps list, and a single-size icon is
scaled badly in whichever of those it was not made for. 256 is the one Add/Remove Programs
reads; 16, 32 and 48 are what Explorer and the taskbar use.

That set is the standard one and is enough. An earlier version also carried 20, 24 and 40 —
the intermediates Windows wants at 125% and 150% scaling — and dropping them costs very
little: Windows synthesises those from 32 and 16, and downscaling 32 to 24 or 40 is close to
free visually. If small sizes ever look soft on a 125% display, adding 20, 24 and 40 back is
the first thing to try.

`build/New-AppIcon.ps1` does not exist here; Marqora has one worth adapting if re-exporting
becomes a chore.
