using Xunit;

namespace Krut.Netcode.Tests
{
    /// <summary>
    /// Network tests share one process and its environment variables, so they run serially
    /// against each other and against the other collections.
    /// </summary>
    [CollectionDefinition(NetworkTestCollection.Name, DisableParallelization = true)]
    public class NetworkTestCollection
    {
        /// <summary>The name of the collection that forces serial runs.</summary>
        public const string Name = "Network tests run serially";
    }
}
