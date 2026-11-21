using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface IOfflineDictionaryService
{
    bool IsInstalled { get; }
    DictionaryMetadata? Metadata { get; }
    Task<TranslationResult?> LookupAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SuggestAsync(string prefix, int limit = 6, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
