using System.Collections.Concurrent;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using SeedOracle.Smoke;

namespace SeedOracle.UI;

internal sealed partial class SeedOracleDispatcher : Node
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static SeedOracleDispatcher? _instance;

    public static void Ensure(NGame host)
    {
        if (_instance is not null && GodotObject.IsInstanceValid(_instance))
            return;
        _instance = new SeedOracleDispatcher { Name = "SeedOracleDispatcher" };
        host.AddChild(_instance);
        _instance.SetProcess(true);
    }

    public static void Post(Action action) => Queue.Enqueue(action);

    public override void _Process(double delta)
    {
        while (Queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Entry.Logger.Error($"Seed Oracle main-thread callback failed: {exception}");
            }
        }

        SmokeRunner.Tick();
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }
}
