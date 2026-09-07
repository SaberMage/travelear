using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class HelperOptionsTests
{
    [Fact]
    public void No_args_gives_defaults()
    {
        var o = HelperOptions.Parse(Array.Empty<string>());
        Assert.Equal(HelperOptions.Default, o);
        Assert.Equal("", o.EndpointSetting);
        Assert.False(o.Tone);
        Assert.Equal("TravelEar.Sink", o.PipeName);
        Assert.False(o.ShowHelp);
    }

    [Fact]
    public void Parses_space_and_equals_forms()
    {
        var a = HelperOptions.Parse(new[] { "--endpoint", "CABLE Input", "--tone", "--pipe", "Test.Pipe" });
        var b = HelperOptions.Parse(new[] { "--endpoint=CABLE Input", "--tone", "--pipe=Test.Pipe" });
        Assert.Equal(a, b);
        Assert.Equal("CABLE Input", a.EndpointSetting);
        Assert.True(a.Tone);
        Assert.Equal("Test.Pipe", a.PipeName);
    }

    [Fact]
    public void Options_are_case_insensitive_and_help_is_recognised()
    {
        Assert.True(HelperOptions.Parse(new[] { "--TONE" }).Tone);
        Assert.True(HelperOptions.Parse(new[] { "--help" }).ShowHelp);
        Assert.True(HelperOptions.Parse(new[] { "-h" }).ShowHelp);
        Assert.True(HelperOptions.Parse(new[] { "/?" }).ShowHelp);
    }

    [Fact]
    public void Empty_endpoint_value_is_allowed_and_means_default_device()
    {
        Assert.Equal("", HelperOptions.Parse(new[] { "--endpoint", "" }).EndpointSetting);
        Assert.Equal("", HelperOptions.Parse(new[] { "--endpoint=" }).EndpointSetting);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--endpoint")]
    [InlineData("--pipe")]
    [InlineData("--pipe=")]
    [InlineData("--tone=yes")]
    [InlineData("stray")]
    public void Rejects_unknown_options_and_missing_values(string arg)
    {
        Assert.Throws<ArgumentException>(() => HelperOptions.Parse(new[] { arg }));
    }

    [Fact]
    public void ToArgs_round_trips_through_Parse()
    {
        var o = new HelperOptions("VoiceMeeter Aux", Tone: true, PipeName: "Other.Pipe", ShowHelp: false);
        Assert.Equal(o, HelperOptions.Parse(o.ToArgs()));
        Assert.Empty(HelperOptions.Default.ToArgs());
    }
}
