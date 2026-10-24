using System;
using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Core.Save
{
    /// <summary>
    /// Save sync wire constants. Every save message travels on <see cref="SyncChannel"/>, reliable, so a
    /// SaveBegin, its chunks and later SaveDeltas from the host arrive in send order.
    /// <code>
    /// SaveBegin             varuint totalBytes, varuint chunkCount, uint crc32, varuint revision
    /// SaveChunk             varuint index, bytes data (ChunkSize each, the last one may be shorter)
    /// SaveAck               varuint revision
    /// SaveDelta             varuint revision, SaveDelta
    /// ProgressClaim         SaveDelta
    /// SaveResyncRequest     varuint clientRevision (diagnostic only)
    /// ProgressClaimRejected SaveDelta, string reason
    /// </code>
    /// </summary>
    public static class SaveProtocol
    {
        public const int ChunkSize = 1000;
        public const int MaxSnapshotBytes = 16 * 1024 * 1024;
        public const int MaxReasonBytes = 256;
        public const Channel SyncChannel = Channel.Save;

        public static int GetChunkCount(int totalBytes)
        {
            if (totalBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(totalBytes));
            return (int)(((long)totalBytes + ChunkSize - 1) / ChunkSize);
        }
    }
}
