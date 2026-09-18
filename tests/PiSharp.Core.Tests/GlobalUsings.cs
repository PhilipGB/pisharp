global using Xunit;

// Process-wide state (Console.Out redirects, environment variables) is shared by several
// test classes, so the suite runs serialized: parallel classes could otherwise race the
// process Console (captured output leaking between tests) or clobber each other's env vars.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
