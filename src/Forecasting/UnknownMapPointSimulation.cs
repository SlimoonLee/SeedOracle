using System.Reflection;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal sealed class UnknownMapPointSimulation
{
    private static readonly RoomType[] NonEventOrder =
    [
        RoomType.Monster,
        RoomType.Elite,
        RoomType.Treasure,
        RoomType.Shop
    ];

    private static readonly FieldInfo? BaseOddsField = typeof(UnknownMapPointOdds).GetField(
        "_baseOdds",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly RunState _run;
    private readonly Rng _rng;
    private readonly IReadOnlyList<AbstractModel> _listeners;
    private readonly Dictionary<RoomType, float> _currentOdds;
    private readonly Dictionary<RoomType, float> _baseOdds;
    private int _unknownsVisited;

    public UnknownMapPointSimulation(
        RunState run,
        IReadOnlyList<AbstractModel> listeners)
    {
        _run = run;
        _rng = new Rng(run.Rng.UnknownMapPoint.ToSerializable());
        _listeners = listeners;
        _unknownsVisited = run.MapPointHistory
            .SelectMany(entries => entries)
            .Count(entry => entry.MapPointType == MapPointType.Unknown);

        var odds = run.Odds.UnknownMapPoint;
        _currentOdds = new Dictionary<RoomType, float>
        {
            [RoomType.Monster] = odds.MonsterOdds,
            [RoomType.Elite] = odds.EliteOdds,
            [RoomType.Treasure] = odds.TreasureOdds,
            [RoomType.Shop] = odds.ShopOdds
        };
        _baseOdds = ReadBaseOdds(odds);
    }

    public RoomType Roll(MapPoint point, bool previousRoomWasShop)
    {
        var priorUnknowns = _unknownsVisited++;
        if (_run.UnlockState.NumberOfRuns == 0)
        {
            if (priorUnknowns < 2)
                return RoomType.Event;
            if (priorUnknowns == 2)
                return RoomType.Monster;
        }

        var shopIsBlacklisted = previousRoomWasShop
                                || (point.Children.Count > 0
                                    && point.Children.All(child => child.PointType == MapPointType.Shop));
        var allowed = NonEventOrder
            .Append(RoomType.Event)
            .Where(roomType => !shopIsBlacklisted || roomType != RoomType.Shop)
            .ToHashSet();

        foreach (var listener in _listeners)
            allowed = listener.ModifyUnknownMapPointRoomTypes(allowed).ToHashSet();

        if (allowed.Count == 0)
            throw new InvalidOperationException("All unknown-room outcomes were excluded by hooks.");

        var result = allowed.Contains(RoomType.Event)
            ? RoomType.Event
            : allowed.Order().First();
        var roll = _rng.NextFloat();
        var cumulative = 0f;
        foreach (var roomType in NonEventOrder)
        {
            var value = _currentOdds[roomType];
            if (!allowed.Contains(roomType) || value < 0f)
                continue;

            cumulative += value;
            if (roll <= cumulative)
            {
                result = roomType;
                break;
            }
        }

        foreach (var roomType in NonEventOrder)
        {
            if (result == roomType)
            {
                _currentOdds[roomType] = _baseOdds[roomType];
                continue;
            }

            if (!allowed.Contains(roomType))
                continue;

            var increase = _baseOdds[roomType];
            foreach (var listener in _listeners)
                increase = listener.ModifyOddsIncreaseForUnrolledRoomType(roomType, increase);
            _currentOdds[roomType] += increase;
        }

        return result;
    }

    private static Dictionary<RoomType, float> ReadBaseOdds(UnknownMapPointOdds odds)
    {
        if (BaseOddsField?.GetValue(odds) is IDictionary<RoomType, float> values)
            return values.ToDictionary(pair => pair.Key, pair => pair.Value);

        return new Dictionary<RoomType, float>
        {
            [RoomType.Monster] = UnknownMapPointOdds.baseMonsterOdds,
            [RoomType.Elite] = odds.EliteOdds >= 0f
                ? UnknownMapPointOdds.baseMonsterOdds
                : UnknownMapPointOdds.baseEliteOdds,
            [RoomType.Treasure] = UnknownMapPointOdds.baseTreasureOdds,
            [RoomType.Shop] = UnknownMapPointOdds.baseShopOdds
        };
    }
}
