using System.Collections.Generic;

namespace NGGMultiplayer.Core.Protocol
{
    /// <summary>Small thread-safe pool of <see cref="NetWriter"/>s. Oversized buffers are not retained.</summary>
    public static class NetWriterPool
    {
        public const int MaxPooled = 16;
        public const int MaxRetainedCapacity = 64 * 1024;

        private static readonly Stack<NetWriter> Pool = new Stack<NetWriter>();

        /// <summary>Returns an empty writer.</summary>
        public static NetWriter Rent()
        {
            lock (Pool)
            {
                if (Pool.Count > 0)
                    return Pool.Pop();
            }
            return new NetWriter();
        }

        public static void Return(NetWriter writer)
        {
            if (writer == null || writer.Capacity > MaxRetainedCapacity)
                return;
            writer.Reset();
            lock (Pool)
            {
                if (Pool.Count < MaxPooled && !Pool.Contains(writer))
                    Pool.Push(writer);
            }
        }
    }
}
