namespace Zapret.Core;

// Only the recoverable missing-backup case may permit a controlled exit.
public sealed class InterruptedTestRecoveryException(string message) : IOException(message);
