namespace Hindsight.Core.Model;

/// <summary>A labelled point in time, either set by the user or derived from a system event.</summary>
public sealed record Marker(long UnixTime, MarkerKind Kind, string Text);
