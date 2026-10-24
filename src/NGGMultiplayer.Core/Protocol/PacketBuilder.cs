using System;

namespace NGGMultiplayer.Core.Protocol
{
    /// <summary>Starts packets: every packet is [byte MessageType][payload].</summary>
    public static class PacketBuilder
    {
        /// <summary>New writer with the message type already written.</summary>
        public static NetWriter Begin(byte messageType)
        {
            var writer = new NetWriter();
            writer.WriteByte(messageType);
            return writer;
        }

        /// <summary>Resets a reusable writer and writes the message type.</summary>
        public static NetWriter Begin(NetWriter writer, byte messageType)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            writer.Reset();
            writer.WriteByte(messageType);
            return writer;
        }

        /// <summary>
        /// Pooled writer with the message type written; dispose to return it.
        /// <code>using (var p = PacketBuilder.Rent(MessageType.Ping)) { p.Writer.WriteDouble(t); transport.Send(peer, ch, rel, p.Segment); }</code>
        /// Safe because <c>ITransport.Send</c> copies the data before returning.
        /// </summary>
        public static PooledPacket Rent(byte messageType)
        {
            var writer = NetWriterPool.Rent();
            writer.WriteByte(messageType);
            return new PooledPacket(writer);
        }
    }

    public struct PooledPacket : IDisposable
    {
        private NetWriter _writer;

        internal PooledPacket(NetWriter writer)
        {
            _writer = writer;
        }

        public NetWriter Writer => _writer ?? throw new ObjectDisposedException(nameof(PooledPacket));

        public ArraySegment<byte> Segment => Writer.AsSegment();

        public void Dispose()
        {
            var writer = _writer;
            _writer = null;
            if (writer != null)
                NetWriterPool.Return(writer);
        }
    }
}
