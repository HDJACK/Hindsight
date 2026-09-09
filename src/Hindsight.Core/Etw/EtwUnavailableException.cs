namespace Hindsight.Core.Etw;

/// <summary>Thrown when the ETW session cannot be started, most often for lack of administrator rights.</summary>
public sealed class EtwUnavailableException : Exception
{
    public EtwUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
