using TokenBar.App;

namespace TokenBar.Core.Tests;

public class ProductIdentityTests
{
    [Fact]
    public void NameIsSyrtis()
    {
        Assert.Equal("Syrtis", ProductIdentity.Name);
    }
}
