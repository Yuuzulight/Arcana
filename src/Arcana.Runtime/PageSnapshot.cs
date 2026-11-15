namespace Arcana.Runtime;

public sealed record PageSnapshot(string Url, string Title, IReadOnlyList<PageElement> Elements, bool Truncated)
{
    public string Text { get; init; } = "";
    public bool TextTruncated { get; init; }
    public bool? Sensitive { get; init; }
}

public sealed record PageElementBounds(double X, double Y, double Width, double Height);

public sealed record PageElement(string Reference, string Tag, string Role, string Name, string? Href,
    string? Type, bool Disabled, string? Value = null, bool? Checked = null, bool? Selected = null,
    bool? Sensitive = null, PageElementBounds? Bounds = null);
