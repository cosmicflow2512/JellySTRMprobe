using Xunit;

// Tests share mutable global state: TestHelpers.EnsurePluginInstance() replaces the static
// Plugin.Instance, and several test classes then read and write Plugin.Instance.Configuration.
// xunit runs test classes in parallel by default, so one class can swap the instance out from
// under another mid-test. The suite runs in about two seconds, so serialising it is cheaper
// than making every class that touches the singleton remember to join a shared collection.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
