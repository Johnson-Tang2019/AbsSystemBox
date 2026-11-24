// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public sealed record ScriptDefinition(Guid Id, string Name, string Path, string[] Arguments,
    bool StartOnLogin, bool RestartOnExit, bool AllowAgent, bool Enabled);

public sealed record ScriptStatus(Guid Id, string Name, string State, bool Enabled, int? ProcessId,
    DateTimeOffset? StartedAt, DateTimeOffset? ExitedAt, int? ExitCode, DateTimeOffset? RetryAt,
    int RestartCount, string? Error, ScriptDefinition? Definition);

public sealed record ScriptOutput(DateTimeOffset Timestamp, string Stream, string Text);
