using Xunit;

namespace SimpleStore.IntegrationTests;

[CollectionDefinition(Name)]
public class AppHostCollection : ICollectionFixture<AppHostFixture>
{
    public const string Name = "AppHost";
}
