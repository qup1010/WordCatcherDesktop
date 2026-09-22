using System.Collections.Generic;
using WordCatcher.Core.Enums;

namespace WordCatcher.Core.Models;

public sealed record TranslationResult(
    string Word,
    string Reading,
    string PartOfSpeech,
    string Definition,
    string ContextTranslation,
    string? MemoryHook,
    LookupSource Source,
    string? SourceEntryId,
    string? SourceSchemaVersion,
    IReadOnlyList<DictPosGroup>? PosGroups = null);

public sealed record SaveCardCommand(
    CaptureResult Capture,
    TranslationResult Translation);
