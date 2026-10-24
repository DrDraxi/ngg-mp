namespace NGGMultiplayer.Core.Net
{
    public enum Channel : byte
    {
        Control = 0,
        Save = 1,
        State = 2,
        Events = 3
    }

    public static class ChannelInfo
    {
        /// <summary>Number of defined channels; valid channels are 0 .. Count-1.</summary>
        public const int Count = 4;

        public static bool IsDefined(Channel channel) => (byte)channel < Count;
    }
}
