using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal sealed class EventQueueSimulation(
    RunState run,
    IReadOnlyList<AbstractModel> listeners)
{
    private readonly HashSet<ModelId> _visitedEventIds = run.VisitedEventIds.ToHashSet();
    private int _eventsVisited = run.Act._rooms.eventsVisited;

    public EventModel? Peek() => Select(consume: false);

    public EventModel? Consume() => Select(consume: true);

    private EventModel? Select(bool consume)
    {
        var events = run.Act._rooms.events;
        if (events.Count == 0)
            return null;

        for (var index = 0; index < events.Count; index++)
        {
            var candidate = events[_eventsVisited % events.Count];
            if (candidate.IsAllowed(run) && !_visitedEventIds.Contains(candidate.Id))
                break;
            _eventsVisited++;
        }

        EventModel result = events[_eventsVisited % events.Count];
        foreach (var listener in listeners)
            result = listener.ModifyNextEvent(result);

        if (consume)
        {
            _visitedEventIds.Add(result.Id);
            _eventsVisited++;
        }

        return result;
    }
}
