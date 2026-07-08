using Xunit;

namespace WebPhone.Tests;

[CollectionDefinition(nameof(IntegrationTestCollection))]
public class IntegrationTestCollection : ICollectionFixture<object>
{
    // Collection fixture to serialize integration tests that create
    // TestWebApplicationFactory instances, preventing parallel backend conflicts.
}
