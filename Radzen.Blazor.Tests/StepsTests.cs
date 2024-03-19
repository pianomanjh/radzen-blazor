using Bunit;
using Xunit;

namespace Radzen.Blazor.Tests;

public class StepsTests
{
    [Fact]
    public void Steps_Renders_CssClass()
    {
        using var ctx = new TestContext();

        var component = ctx.RenderComponent<RadzenSteps>();

        Assert.Contains(@$"tablist", component.Markup);
    }
}