namespace Hindsight.App.Controls;

/// <summary>One row in the Context tab's DNS / endpoint / file lists: the name (a host, an endpoint or a path)
/// and its already-formatted figure (a lookup count for DNS, bytes otherwise). The lists arrive pre-sorted by
/// the store, so no unformatted sort key is kept.</summary>
public sealed record ContextRow(string Key, string Value);
