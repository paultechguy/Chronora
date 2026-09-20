// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace PaulTechGuy.CN.Presentation;

/// <summary>
/// How a run describes itself in the journal.
///
/// A loose string map rather than a typed record, deliberately. This is written once and
/// read back by builds that do not exist yet, and a stored run should still list after the
/// rule model changes underneath it - which is precisely when somebody would want to undo
/// it. Extra keys a future version adds are simply carried along.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class RunRecordJsonContext : JsonSerializerContext;
