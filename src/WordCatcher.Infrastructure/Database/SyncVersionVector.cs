using System.Text.Json;

namespace WordCatcher.Infrastructure.Database;

internal enum VersionVectorOrder
{
    Equal,
    LocalDominates,
    RemoteDominates,
    Concurrent
}

internal static class SyncVersionVector
{
    public static Dictionary<string, long> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, long>>(json)
            ?? new Dictionary<string, long>(StringComparer.Ordinal);
    }

    public static string Serialize(IReadOnlyDictionary<string, long> vector)
        => JsonSerializer.Serialize(vector.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static Dictionary<string, long> Increment(IReadOnlyDictionary<string, long> vector, string deviceId)
    {
        var next = new Dictionary<string, long>(vector, StringComparer.Ordinal);
        next.TryGetValue(deviceId, out var current);
        next[deviceId] = checked(current + 1);
        return next;
    }

    public static Dictionary<string, long> Merge(IReadOnlyDictionary<string, long> left, IReadOnlyDictionary<string, long> right)
    {
        var merged = new Dictionary<string, long>(left, StringComparer.Ordinal);
        foreach (var (device, value) in right)
        {
            if (!merged.TryGetValue(device, out var current) || value > current)
                merged[device] = value;
        }
        return merged;
    }

    public static VersionVectorOrder Compare(IReadOnlyDictionary<string, long> local, IReadOnlyDictionary<string, long> remote)
    {
        var greater = false;
        var less = false;
        foreach (var device in local.Keys.Concat(remote.Keys).Distinct(StringComparer.Ordinal))
        {
            local.TryGetValue(device, out var left);
            remote.TryGetValue(device, out var right);
            greater |= left > right;
            less |= left < right;
        }
        if (greater && less) return VersionVectorOrder.Concurrent;
        if (greater) return VersionVectorOrder.LocalDominates;
        if (less) return VersionVectorOrder.RemoteDominates;
        return VersionVectorOrder.Equal;
    }

    public static bool TryParse(JsonElement element, out Dictionary<string, long> vector)
    {
        vector = new Dictionary<string, long>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var properties = element.EnumerateObject().ToList();
        if (properties.Count == 0 || properties.Count > 64) return false;
        foreach (var property in properties)
        {
            if (property.Name.Length is < 1 or > 128
                || !property.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                || property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt64(out var value) || value < 1
                || !vector.TryAdd(property.Name, value))
                return false;
        }
        return true;
    }
}
