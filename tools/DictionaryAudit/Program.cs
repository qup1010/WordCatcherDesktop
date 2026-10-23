using System.Text.Encodings.Web;
using System.Text.Json;
using WordCatcher.Infrastructure.Dictionary;

// Read-only audit of the installed dictionary through the application's lookup path.
using var dictionary = new OfflineDictionaryService(args.Length > 1 ? args[1] : null);
if (!dictionary.IsInstalled) throw new InvalidOperationException("未安装待检查的词典。");
var words = new[] { "the", "with", "wait", "a", "an", "of", "bank", "run", "set", "light", "digital", "environment", "developing", "managed", "went", "children", "better", "a little", "take off", "look up", "in spite of", "New York", "qzxwunknown", "This is a complete sentence." };
var results = new List<object>();
foreach (var query in words)
{
    var result = await dictionary.LookupAsync(query);
    results.Add(new { query, found = result != null, word = result?.Word, source = result?.Source.ToString(),
        reading = result?.Reading, partOfSpeech = result?.PartOfSpeech, definition = result?.Definition,
        groups = result?.PosGroups?.Select(g => new { pos = g.Pos, properName = g.IsProperName, summary = g.QuickSummary,
            senses = g.Meanings.Count, priorities = g.Meanings.Select(m => m.Priority).ToArray() }) });
}
var json = JsonSerializer.Serialize(new { checkedAtUtc = DateTimeOffset.UtcNow, results }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
if (args.Length > 0) await File.WriteAllTextAsync(args[0], json);
else Console.WriteLine(json);
