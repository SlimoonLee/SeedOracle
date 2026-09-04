using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Validation;

internal sealed class LiveFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true,
        WriteIndented = false
    };

    private readonly IReadOnlyDictionary<string, string> _sectionHashes;

    private LiveFingerprint(IReadOnlyDictionary<string, string> sectionHashes)
    {
        _sectionHashes = sectionHashes;
    }

    public static LiveFingerprint Capture(RunState run)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run_rng"] = Hash(run.Rng.ToSerializable()),
            ["run_odds"] = Hash(run.Odds.ToSerializable()),
            ["shared_relic_grab_bag"] = Hash(run.SharedRelicGrabBag.ToSerializable()),
            ["acts_and_encounter_counters"] = Hash(run.Acts.Select(act => act.ToSave()).ToArray()),
            ["visited_event_ids"] = Hash(run.VisitedEventIds.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray()),
            ["visited_map_coords"] = Hash(run.VisitedMapCoords.ToArray()),
            ["map_point_history"] = Hash(run.MapPointHistory.Select(history => history.ToArray()).ToArray()),
            ["players"] = Hash(run.Players.OrderBy(player => player.NetId).Select(player => player.ToSerializable()).ToArray()),
            ["run_extra_fields"] = Hash(run.ExtraFields.ToSerializable()),
            ["run_position"] = Hash(new
            {
                run.CurrentActIndex,
                run.ActFloor,
                run.TotalFloor,
                run.NextRoomId,
                run.CurrentMapCoord,
                run.CurrentRoomCount
            })
        };

        return new LiveFingerprint(sections);
    }

    public string? DescribeDifference(LiveFingerprint other)
    {
        var changed = _sectionHashes.Keys
            .Union(other._sectionHashes.Keys, StringComparer.Ordinal)
            .Where(key => !_sectionHashes.TryGetValue(key, out var before)
                          || !other._sectionHashes.TryGetValue(key, out var after)
                          || !string.Equals(before, after, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        return changed.Length == 0 ? null : string.Join(", ", changed);
    }

    private static string Hash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
