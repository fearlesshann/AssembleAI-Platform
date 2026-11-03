namespace AssembleAI.Platform.Identity.Sessions;

/// <summary>Safe to report or log without exposing connection details or session contents.</summary>
public sealed class BffSessionStoreException() : Exception("The BFF session store is unavailable.")
{
}
