using System;
using System.Collections.Generic;
using NGGMultiplayer.Core;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Core.Sync;
using NGGMultiplayer.Tests.Sync;

namespace NGGMultiplayer.Tests.Integration
{
    /// <summary><see cref="InMemorySaveBackend"/> whose snapshot application can be made to fail.</summary>
    internal sealed class FlakySaveBackend : ISaveBackend
    {
        public FlakySaveBackend(InMemorySaveBackend inner)
        {
            Inner = inner;
        }

        public InMemorySaveBackend Inner { get; }

        public bool FailApply { get; set; }

        public int FailedApplies { get; private set; }

        public event Action<SaveDelta> LocalProgressChanged
        {
            add => Inner.LocalProgressChanged += value;
            remove => Inner.LocalProgressChanged -= value;
        }

        public bool SuppressDiskWrites
        {
            get => Inner.SuppressDiskWrites;
            set => Inner.SuppressDiskWrites = value;
        }

        public byte[] CaptureSnapshot() => Inner.CaptureSnapshot();

        public void ApplySnapshot(byte[] snapshot)
        {
            if (FailApply)
            {
                FailedApplies++;
                throw new InvalidOperationException("simulated load failure");
            }
            Inner.ApplySnapshot(snapshot);
        }

        public void BackupLocalSave() => Inner.BackupLocalSave();

        public void RestoreLocalSave() => Inner.RestoreLocalSave();

        public void ApplyDelta(SaveDelta delta) => Inner.ApplyDelta(delta);
    }

    /// <summary>Records which entities were created while the local save was not yet the host's.</summary>
    internal sealed class RecordingFactory : INetEntityFactory
    {
        private readonly Func<bool> _isSynced;

        public RecordingFactory(Func<bool> isSynced)
        {
            _isSynced = isSynced;
        }

        public TestFactory Inner { get; } = new TestFactory();

        public int CreatedBeforeSync { get; private set; }

        public TestEntity this[NetEntityId id] => Inner[id];

        public bool Has(NetEntityId id) => Inner.ById.ContainsKey(id);

        public INetEntity CreateEntity(NetEntity entity)
        {
            if (!_isSynced())
                CreatedBeforeSync++;
            return Inner.CreateEntity(entity);
        }
    }

    internal sealed class NetPeer
    {
        public const string ChatRpc = "test.chat";

        public NetPeer(LoopbackHub hub, ulong id, ISaveBackend save, NetSessionOptions options)
        {
            Id = new PeerId(id);
            Transport = hub.CreateTransport(Id);
            SaveBackend = save;
            Factory = new RecordingFactory(() => Net != null && (Net.IsHost || (Net.SaveClient?.IsSynced ?? false)));
            Net = new NetSession(Transport, save, Factory, options);
            Net.Started += () => Log.Add("started");
            Net.Ready += () => { ReadyCount++; Log.Add("ready"); };
            Net.PeerReady += p => { ReadyPeers.Add(p); Log.Add("peer-ready:" + p.Value); };
            Net.Ended += info => { Ended.Add(info); Log.Add("ended:" + info.Reason); };
            Net.RegisterRpc(ChatRpc, (sender, r) => Chat.Add((sender, r.ReadString(256))),
                (sender, target, peer, args) => args.ReadString(256) != "forbidden");
        }

        public PeerId Id { get; }

        public LoopbackTransport Transport { get; }

        public ISaveBackend SaveBackend { get; }

        public InMemorySaveBackend Save => SaveBackend as InMemorySaveBackend ?? ((FlakySaveBackend)SaveBackend).Inner;

        public RecordingFactory Factory { get; }

        public NetSession Net { get; }

        /// <summary>Added to the hub clock for this peer, so every peer has its own time base.</summary>
        public double ClockOffset { get; set; }

        /// <summary>Runs before each of this peer's updates (e.g. to move entities it owns).</summary>
        public Action<NetPeer> BeforeUpdate { get; set; }

        public int ReadyCount { get; private set; }

        public List<PeerId> ReadyPeers { get; } = new List<PeerId>();

        public List<SessionEndInfo> Ended { get; } = new List<SessionEndInfo>();

        public List<(PeerId Sender, string Text)> Chat { get; } = new List<(PeerId, string)>();

        public List<string> Log { get; } = new List<string>();

        public bool SendChat(string text, RpcTarget target = RpcTarget.All) =>
            Net.Rpc.Send(ChatRpc, target, text, (w, t) => w.WriteString(t));
    }

    /// <summary>Peers running <see cref="NetSession"/> over one <see cref="LoopbackHub"/> with a manual clock.</summary>
    internal sealed class NetTestNet
    {
        public const double Step = 0.01;

        public NetTestNet(int seed = 7)
        {
            Hub = new LoopbackHub(seed);
        }

        public LoopbackHub Hub { get; }

        public List<NetPeer> Peers { get; } = new List<NetPeer>();

        public NetPeer Add(ulong id, ISaveBackend save = null, NetSessionOptions options = null, double clockOffset = 0)
        {
            var peer = new NetPeer(Hub, id, save ?? new InMemorySaveBackend(), options ?? new NetSessionOptions())
            {
                ClockOffset = clockOffset
            };
            Peers.Add(peer);
            return peer;
        }

        public void Frame()
        {
            Hub.Advance(Step);
            foreach (var p in Peers)
            {
                p.BeforeUpdate?.Invoke(p);
                p.Net.Update(Hub.Now + p.ClockOffset);
            }
        }

        public void Run(double seconds)
        {
            int frames = (int)Math.Round(seconds / Step);
            for (int i = 0; i < frames; i++)
                Frame();
        }

        public bool RunUntil(Func<bool> condition, double maxSeconds)
        {
            int frames = (int)Math.Round(maxSeconds / Step);
            for (int i = 0; i < frames; i++)
            {
                if (condition())
                    return true;
                Frame();
            }
            return condition();
        }
    }
}
