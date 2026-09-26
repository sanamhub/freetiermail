using System.Reflection;
using Xunit;

namespace FreeTierMail.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void The_library_assembly_loads()
    {
        var assembly = Assembly.Load("FreeTierMail");

        Assert.Equal("FreeTierMail", assembly.GetName().Name);
    }
}
