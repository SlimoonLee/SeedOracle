using System.Collections.Concurrent;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace BlitzShopOracle.UI;

internal sealed partial class BlitzShopOracleDispatcher : Node
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static BlitzShopOracleDispatcher? _instance;

    internal static void Ensure(NGame host)
    {
        if (_instance is not null && GodotObject.IsInstanceValid(_instance))
            return;
        _instance = new BlitzShopOracleDispatcher { Name = "BlitzShopOracleDispatcher" };
        host.AddChild(_instance);
        _instance.SetProcess(true);
    }

    internal static void Post(Action action) => Queue.Enqueue(action);

    public override void _Process(double delta)
    {
        while (Queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception exception) { BlitzShopOracle.Entry.Logger.Error($"Blitz Shop Oracle callback failed: {exception}"); }
        }
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}
