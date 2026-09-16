using OpenShelf.Core;

namespace OpenShelf.Formats;

/// <summary>
/// A format-level failure which can be classified without inspecting exception text.
/// </summary>
public sealed class FormatImportException : Exception
{
    public FormatImportException(
        ImportOutcome outcome,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Outcome = outcome;
    }

    public ImportOutcome Outcome { get; }

    public static FormatImportException PasswordRequired(string? message = null, Exception? inner = null) =>
        new(ImportOutcome.PasswordRequired, message ?? "This book requires a password.", inner);

    public static FormatImportException DrmProtected(string? message = null, Exception? inner = null) =>
        new(ImportOutcome.DrmProtected, message ?? "This book is protected by unsupported DRM.", inner);

    public static FormatImportException Unsupported(string message, Exception? inner = null) =>
        new(ImportOutcome.Unsupported, message, inner);

    public static FormatImportException Corrupt(string message, Exception? inner = null) =>
        new(ImportOutcome.Corrupt, message, inner);
}
