namespace Locres.IO;

/// <summary>
/// A named group of entries. File order is preserved exactly, and duplicate
/// namespace names are legal in the format — they stay separate groups here.
/// The empty name ("") is the common anonymous namespace.
/// </summary>
public sealed class LocresNamespace
{
    public string Name { get; }

    public IReadOnlyList<LocresEntry> Entries => _entries;

    private readonly List<LocresEntry> _entries;

    internal LocresNamespace(string name, List<LocresEntry> entries)
    {
        Name = name;
        _entries = entries;
    }
}
