using System;

namespace NGGMultiplayer.Core.Protocol
{
    /// <summary>Thrown when a packet is malformed. Handlers should drop the packet and log.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message)
            : base(message)
        {
        }

        public ProtocolException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
