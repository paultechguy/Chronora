// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Rules;

/// <summary>
/// The patterns that cover the overwhelming majority of real filenames, so that most users
/// never meet the pattern editor at all.
///
/// Order is load-bearing: the first plausible match wins, so specific patterns must precede
/// general ones. A bare date pattern placed too early would match the date inside
/// IMG_20240315_142530 and discard the time.
/// </summary>
public static class BuiltInPatterns
{
    public static IReadOnlyList<FilenamePattern> All { get; } =
    [
        P("android-camera", "Android camera", "IMG_{yyyy}{MM}{dd}_{HH}{mm}{ss}", DatePrecision.Second, 10),
        P("android-video", "Android video", "VID_{yyyy}{MM}{dd}_{HH}{mm}{ss}", DatePrecision.Second, 11),
        P("pixel", "Google Pixel", "PXL_{yyyy}{MM}{dd}_{HH}{mm}{ss}{fff}", DatePrecision.Millisecond, 12),

        // WhatsApp encodes only the day. The counter after WA is not a time, and treating it
        // as one would invent a time of day that never existed.
        P("whatsapp-img", "WhatsApp image", "IMG-{yyyy}{MM}{dd}-WA{#}", DatePrecision.Day, 20),
        P("whatsapp-vid", "WhatsApp video", "VID-{yyyy}{MM}{dd}-WA{#}", DatePrecision.Day, 21),

        P("signal", "Signal", "signal-{yyyy}-{MM}-{dd}-{HH}{mm}{ss}", DatePrecision.Second, 30),
        P("telegram", "Telegram", "photo_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}", DatePrecision.Second, 31),

        P("screenshot-windows", "Windows screenshot", "Screenshot {yyyy}-{MM}-{dd} {HH}{mm}{ss}", DatePrecision.Second, 40),
        P("screenshot-android", "Android screenshot", "Screenshot{?}{yyyy}{MM}{dd}{?}{HH}{mm}{ss}", DatePrecision.Second, 41),
        P("screenshot-macos", "macOS screenshot", "Screen Shot {yyyy}-{MM}-{dd} at {hh}.{mm}.{ss} {tt}", DatePrecision.Second, 42),

        P("iphone-import", "iPhone import", "{yyyy}-{MM}-{dd} {HH}.{mm}.{ss}", DatePrecision.Second, 50),
        P("dashcam", "Dashcam", "{yyyy}_{MM}{dd}_{HH}{mm}{ss}", DatePrecision.Second, 51),
        P("burst", "Burst shot", "BURST{yyyy}{MM}{dd}{HH}{mm}{ss}", DatePrecision.Second, 52),

        P("iso-8601", "ISO 8601", "{yyyy}-{MM}-{dd}T{HH}:{mm}:{ss}{z}", DatePrecision.Second, 60),
        P("iso-basic", "ISO basic", "{yyyy}{MM}{dd}{?}{HH}{mm}{ss}", DatePrecision.Second, 61),

        P("unix-millis", "Unix milliseconds", "{unixms}", DatePrecision.Millisecond, 70),
        P("unix-seconds", "Unix seconds", "{unix}", DatePrecision.Second, 71),

        P("date-dashed", "Plain date", "{yyyy}-{MM}-{dd}", DatePrecision.Day, 80),

        // Disabled by default, and that is the design rather than caution. It matches the
        // date inside almost every other pattern here, and it matches serial numbers.
        P("date-compact", "Compact date (8 digits)", "{yyyy}{MM}{dd}", DatePrecision.Day, 90, enabled: false),
    ];

    public static FilenamePattern? ById(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    private static FilenamePattern P(
        string id,
        string name,
        string tokens,
        DatePrecision precision,
        int order,
        bool enabled = true) =>
        new(
            id,
            name,
            PatternMode.Tokens,
            tokens,
            PatternScope.FileNameWithoutExtension,
            precision,
            IsBuiltIn: true,
            IsEnabled: enabled,
            Order: order);
}
