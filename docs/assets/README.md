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
with a `<style>` block and four `class=` attributes, which is precisely the case Direct2D
does not handle. Marqora hit this and moved to a bitmap for the same reason.

So: the vector is the master and stays here. The PNG is what ships.

## The icon

Nine sizes — 16, 20, 24, 32, 40, 48, 64, 128, 256 — all 32bpp with alpha. Windows picks
different ones for the taskbar, alt-tab, Explorer's views and the Settings ▸ Apps list, and
a single-size icon is scaled badly in whichever of those it was not made for. 256 is the one
Add/Remove Programs reads.

Re-exporting? Keep all nine. `build/New-AppIcon.ps1` does not exist here yet; Marqora has one
worth adapting if this becomes a chore.
