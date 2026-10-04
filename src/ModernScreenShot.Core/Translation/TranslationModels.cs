namespace ModernScreenShot.Core.Translation;

/// <summary>
/// Outcome of one translation pass. <see cref="Text"/> is the translation, <see cref="SourceText"/>
/// the (reflowed) input that was actually sent, and <see cref="ToCode"/> the target language.
///
/// There is no per-pair package any more: Hy-MT2 is one multilingual model, so a direction is just
/// the target language — the source is whatever the text is.
/// </summary>
public sealed record TranslationOutcome(
    string SourceText,
    string Text,
    string ToCode,
    long ElapsedMs);
