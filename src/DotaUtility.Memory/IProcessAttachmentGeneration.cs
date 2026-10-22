namespace DotaUtility.Memory;

/// <summary>
/// Identifies one process-memory attachment. Increases whenever the underlying
/// process handle is replaced or cleared, even if the numeric PID is unchanged.
/// Consumers without an attachment identity must not reuse process-bound caches.
/// </summary>
public interface IProcessAttachmentGeneration
{
    long AttachmentGeneration { get; }
}
