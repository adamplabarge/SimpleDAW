namespace SimpleDAW;

/// <summary>An input channel choice for a track, with a friendly label.</summary>
public sealed record ChannelOption(int Index, string Label, bool IsMain);

/// <summary>A click accent-pattern choice, with a friendly label.</summary>
public sealed record ClickAccentOption(string Label, ClickAccent Value);

/// <summary>
/// Produces friendly names for hardware input channels. A 12-channel device is
/// treated as a TASCAM Model 12: channels 1-10 are inputs and 11-12 are the
/// stereo main mix (Main L / Main R).
/// </summary>
public static class ChannelLabels
{
    public static string Label(int index, int totalChannels)
    {
        if (totalChannels == 12)
        {
            if (index == 10)
            {
                return "Main L";
            }

            if (index == 11)
            {
                return "Main R";
            }
        }

        return (index + 1).ToString();
    }

    public static bool IsMain(int index, int totalChannels) =>
        totalChannels == 12 && index >= 10;
}
