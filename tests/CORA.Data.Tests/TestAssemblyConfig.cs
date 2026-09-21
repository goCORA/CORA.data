// Cora holds the master key in static state, and several test classes (re)initialize it, so
// running classes in parallel can swap the key out from under a test mid-run. The suite takes
// about a second, so serialize it rather than guard each class.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
