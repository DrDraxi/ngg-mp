namespace NGGMultiplayer.Core.Net
{
    public struct IncomingPacket
    {
        public PeerId From;
        public Channel Channel;

        /// <summary>Exactly the received payload; owned by whoever polled it.</summary>
        public byte[] Data;

        public IncomingPacket(PeerId from, Channel channel, byte[] data)
        {
            From = from;
            Channel = channel;
            Data = data;
        }
    }
}
