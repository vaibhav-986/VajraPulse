namespace VajraPulse.Core;

public enum IslandMode
{
    Compact,
    Expanded,
    Settings
}

public sealed class IslandState
{
    public IslandMode Mode { get; private set; } = IslandMode.Compact;

    public void Expand() => Mode = IslandMode.Expanded;

    public void Collapse() => Mode = IslandMode.Compact;

    public void Toggle()
    {
        Mode = Mode == IslandMode.Compact
            ? IslandMode.Expanded
            : IslandMode.Compact;
    }

    public void OpenSettings() => Mode = IslandMode.Settings;

    public void CloseSettings()
    {
        if (Mode == IslandMode.Settings)
        {
            Mode = IslandMode.Expanded;
        }
    }
}
