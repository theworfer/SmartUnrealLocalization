using Xunit;

// The app layer is built around process-global state: singleton services
// (ReviewService, CommentService, QcService), AppConfig statics and the
// per-user file layout. Running test classes in parallel let one class reset
// AppConfig.ConfigDirOverride while another was still using it — which sent
// that class's writes into the developer's REAL profile folder.
//
// The whole suite runs in about a second, so serialising it costs nothing and
// removes an entire category of flakiness.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
