namespace Arcana.Runtime;

public sealed record PageSnapshot(string Url, string Title, IReadOnlyList<PageElement> Elements, bool Truncated);

public sealed record PageElement(string Reference, string Tag, string Role, string Name, string? Href,
    string? Type, bool Disabled);
