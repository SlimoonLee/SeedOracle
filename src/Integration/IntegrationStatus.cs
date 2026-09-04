namespace SeedOracle.Integration;

internal sealed record IntegrationStatus(
    string ModId,
    string Version,
    bool IsLoaded,
    IReadOnlyDictionary<string, bool> Capabilities)
{
    public string Describe()
    {
        var capabilities = string.Join(", ", Capabilities.Select(pair => $"{pair.Key}={pair.Value.ToString().ToLowerInvariant()}"));
        return $"{ModId} version={Version} loaded={IsLoaded.ToString().ToLowerInvariant()} {capabilities}";
    }
}

