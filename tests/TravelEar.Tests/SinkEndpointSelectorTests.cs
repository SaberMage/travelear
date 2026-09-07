using TravelEar.Core;
using Xunit;

namespace TravelEar.Tests;

public class SinkEndpointSelectorTests
{
    private static readonly string[] Devices =
    {
        "Speakers (Realtek(R) Audio)",
        "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)",
        "VoiceMeeter Aux Input (VB-Audio VoiceMeeter AUX VAIO)",
        "CABLE Input (VB-Audio Virtual Cable)",
        "LG TV (NVIDIA High Definition Audio)",
    };

    // [unit->REQ-SINK-ENDPOINT-CONFIG]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_setting_selects_the_system_default_device(string? setting)
    {
        Assert.True(SinkEndpointSelector.TryChoose(Devices, setting, out var index));
        Assert.Equal(SinkEndpointSelector.DefaultDevice, index);
    }

    [Fact]
    public void Substring_match_is_case_insensitive_and_takes_the_first_in_enumeration_order()
    {
        Assert.True(SinkEndpointSelector.TryChoose(Devices, "voicemeeter", out var index));
        Assert.Equal(1, index);

        Assert.True(SinkEndpointSelector.TryChoose(Devices, "cable", out index));
        Assert.Equal(3, index);
    }

    [Fact]
    public void Exact_name_wins_over_an_earlier_substring_match()
    {
        var names = new[] { "CABLE Input extra", "CABLE Input" };
        Assert.True(SinkEndpointSelector.TryChoose(names, "cable input", out var index));
        Assert.Equal(1, index);
    }

    [Fact]
    public void Setting_is_trimmed_before_matching()
    {
        Assert.True(SinkEndpointSelector.TryChoose(Devices, "  Aux Input ", out var index));
        Assert.Equal(2, index);
    }

    [Fact]
    public void No_match_reports_failure_instead_of_falling_back_to_default()
    {
        Assert.False(SinkEndpointSelector.TryChoose(Devices, "Headset", out var index));
        Assert.Equal(SinkEndpointSelector.DefaultDevice, index);
        Assert.False(SinkEndpointSelector.TryChoose(Array.Empty<string>(), "anything", out _));
    }
}
