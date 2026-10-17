using Xunit;

// The app has a few process-wide singletons/statics (ConfigService, AppPaths,
// SoundboardConfiguration folders). Run tests sequentially to keep them isolated.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
