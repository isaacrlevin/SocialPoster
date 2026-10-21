using SocialPoster.Core;

namespace SocialPoster.Components;

/// <summary>An immutable snapshot captured when Publish or Schedule is clicked.</summary>
public sealed record ComposerSubmission(string DraftId, string SourceUrl, string BaseTemplate,
    IReadOnlyDictionary<Platform, string> Templates, IReadOnlyList<MediaAttachment> Media);

/// <summary>Optional host integration executed before publishing or freezing a schedule.</summary>
public interface IComposerWorkflow
{
    Task<ComposerSubmission> PrepareAsync(ComposerSubmission submission);
    string? CaptureState();
    void RestoreState(string? state);
    void Reset();
}

/// <summary>
/// Thrown from <see cref="IComposerWorkflow.PrepareAsync"/> when the host handled the submission itself
/// (for example, queued it for later) and nothing should be published. The composer stops quietly,
/// without an error note; the host is responsible for telling the user what happened.
/// </summary>
public sealed class ComposerStagedException(string message) : Exception(message);
