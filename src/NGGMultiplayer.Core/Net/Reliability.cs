namespace NGGMultiplayer.Core.Net
{
    public enum Reliability : byte
    {
        /// <summary>May be dropped or arrive out of order.</summary>
        Unreliable,
        /// <summary>Delivered exactly once, in order per (sender, receiver, channel).</summary>
        Reliable
    }
}
