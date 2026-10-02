namespace MgtOcr.Api.Services;

// Shared between FileCompressWorker and FileArchiveWorker: tells the archiver whether it must wait
// for a file to be compressed before uploading it, so every SharePoint copy is the compressed one.
public sealed class CompressionState
{
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completes once FileCompressWorker has started; the value is "compression is on".
    public Task<bool> WhenReady => _ready.Task;

    public void Set(bool active) => _ready.TrySetResult(active);
}
