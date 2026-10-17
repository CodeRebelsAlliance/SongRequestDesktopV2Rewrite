using Xunit;

// Serialize integration tests: they share lazily generated FFmpeg fixtures and
// open OpenAL devices, which are not safe to hammer in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
