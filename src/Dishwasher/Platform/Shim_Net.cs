// Local compatibility shim for XNA 3.0 Microsoft.Xna.Framework.Net.
//
// MonoGame has no networking stack. This file implements just enough of the
// XNA NetworkSession model for the game's System Link (LAN) multiplayer path:
//   - NetworkSession.Create/Find/Join (+ Begin/End async forms)
//   - LocalNetworkGamer.SendData / ReceiveData, IsDataAvailable
//   - AllGamers / LocalGamers / RemoteGamers, GamerJoined/Left/SessionEnded
//
// Transport: plain System.Net.Sockets UDP on the local WiFi network.
//   * discovery  : broadcast + subnet-directed broadcast + a /24 unicast scan
//                  (some APs drop broadcast; the unicast scan is the fallback)
//   * session data: unicast UDP between the two devices
//
// The Xbox-LIVE (PlayerMatch) path is not implemented; Create/Find for it
// yields an inert local-only session so the menu cannot crash if reached, but
// the LIVE co-op entry is hidden by the port (see MainMenu.cs // PORT (mp)).
// NetworkSessionType.Local (same-device split-screen / leaderboard helper) is
// implemented as a purely local session with no sockets.
//
// Everything here is Android-appropriate: no Xbox services, no extra packages.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.GamerServices;

namespace Microsoft.Xna.Framework.Net
{
    internal sealed class ShimNetAsyncResult : IAsyncResult
    {
        private readonly ManualResetEvent _handle;
        public ShimNetAsyncResult(object state)
        {
            AsyncState = state;
            _handle = new ManualResetEvent(true);
        }
        public object AsyncState { get; }
        public WaitHandle AsyncWaitHandle => _handle;
        public bool CompletedSynchronously => true;
        public bool IsCompleted => true;
    }

    // A real (background) async result for Find/Join. Result/Error are published
    // before the volatile done flag, so a polling reader sees them.
    internal sealed class NetOpResult : IAsyncResult
    {
        private readonly ManualResetEvent _event = new ManualResetEvent(false);
        private int _done;

        public NetOpResult(object state) { AsyncState = state; }
        public object AsyncState { get; }
        public WaitHandle AsyncWaitHandle => _event;
        public bool CompletedSynchronously => false;
        public bool IsCompleted => Volatile.Read(ref _done) != 0;

        public object Result;
        public Exception Error;

        internal void Complete()
        {
            Volatile.Write(ref _done, 1);
            _event.Set();
        }

        internal bool Wait(int milliseconds) => _event.WaitOne(milliseconds);
    }

    public class PacketReader : BinaryReader
    {
        public int Length => (int)BaseStream.Length;

        public int Position
        {
            get => (int)BaseStream.Position;
            set => BaseStream.Position = value;
        }

        internal byte[] ByteArray => ((MemoryStream)BaseStream).GetBuffer();

        public PacketReader() : this(0) { }
        public PacketReader(int capacity) : base(new MemoryStream(capacity)) { }

        internal void Resize(int size)
        {
            MemoryStream memoryStream = (MemoryStream)BaseStream;
            memoryStream.SetLength(size);
            memoryStream.Position = 0L;
        }

        // PORT (mp): load a received UDP payload into the reader's buffer.
        internal void Load(byte[] buffer, int offset, int count)
        {
            MemoryStream memoryStream = (MemoryStream)BaseStream;
            memoryStream.SetLength(count);
            if (count > 0)
            {
                Buffer.BlockCopy(buffer, offset, memoryStream.GetBuffer(), 0, count);
            }
            memoryStream.Position = 0L;
        }

        public Vector2 ReadVector2()
        {
            Vector2 result = default;
            result.X = ReadSingle();
            result.Y = ReadSingle();
            return result;
        }

        public Vector3 ReadVector3()
        {
            Vector3 result = default;
            result.X = ReadSingle();
            result.Y = ReadSingle();
            result.Z = ReadSingle();
            return result;
        }

        public Vector4 ReadVector4()
        {
            Vector4 result = default;
            result.X = ReadSingle();
            result.Y = ReadSingle();
            result.Z = ReadSingle();
            result.W = ReadSingle();
            return result;
        }

        public Matrix ReadMatrix()
        {
            Matrix result = default;
            result.M11 = ReadSingle();
            result.M12 = ReadSingle();
            result.M13 = ReadSingle();
            result.M14 = ReadSingle();
            result.M21 = ReadSingle();
            result.M22 = ReadSingle();
            result.M23 = ReadSingle();
            result.M24 = ReadSingle();
            result.M31 = ReadSingle();
            result.M32 = ReadSingle();
            result.M33 = ReadSingle();
            result.M34 = ReadSingle();
            result.M41 = ReadSingle();
            result.M42 = ReadSingle();
            result.M43 = ReadSingle();
            result.M44 = ReadSingle();
            return result;
        }

        public Quaternion ReadQuaternion()
        {
            Quaternion result = default;
            result.X = ReadSingle();
            result.Y = ReadSingle();
            result.Z = ReadSingle();
            result.W = ReadSingle();
            return result;
        }
    }

    public class PacketWriter : BinaryWriter
    {
        public int Length => (int)BaseStream.Length;

        public int Position
        {
            get => (int)BaseStream.Position;
            set => BaseStream.Position = value;
        }

        internal byte[] ByteArray => ((MemoryStream)BaseStream).GetBuffer();

        public PacketWriter() : this(0) { }
        public PacketWriter(int capacity) : base(new MemoryStream(capacity)) { }

        internal void Clear()
        {
            MemoryStream memoryStream = (MemoryStream)BaseStream;
            memoryStream.SetLength(0L);
            memoryStream.Position = 0L;
        }

        public void Write(Vector2 value) { Write(value.X); Write(value.Y); }
        public void Write(Vector3 value) { Write(value.X); Write(value.Y); Write(value.Z); }
        public void Write(Vector4 value) { Write(value.X); Write(value.Y); Write(value.Z); Write(value.W); }
        public void Write(Quaternion value) { Write(value.X); Write(value.Y); Write(value.Z); Write(value.W); }

        public void Write(Matrix value)
        {
            Write(value.M11); Write(value.M12); Write(value.M13); Write(value.M14);
            Write(value.M21); Write(value.M22); Write(value.M23); Write(value.M24);
            Write(value.M31); Write(value.M32); Write(value.M33); Write(value.M34);
            Write(value.M41); Write(value.M42); Write(value.M43); Write(value.M44);
        }
    }

    [Flags]
    public enum SendDataOptions
    {
        None = 0,
        Reliable = 1,
        InOrder = 2,
        ReliableInOrder = Reliable | InOrder
    }

    public enum NetworkSessionType
    {
        SystemLink,
        Local,
        PlayerMatch,
        Ranked
    }

    public enum NetworkSessionState
    {
        Lobby,
        Playing,
        Ended
    }

    public enum NetworkSessionEndReason
    {
        HostEndedSession,
        RemotePlayerLeft,
        Disconnected,
        ClientSignedOut
    }

    // ------------------------------------------------------------------
    // LAN wire protocol.
    //
    // Every datagram starts with a 4-byte magic ("DWMP") + 1-byte protocol
    // version + 1-byte type. Payload follows at offset 6.
    //
    // Fixed port 27315 so the host always has the same rendezvous point. The
    // client discovers the host via broadcast (and, as a fallback, a /24
    // unicast scan), then joins the same port. Data and heartbeats ride the
    // same UDP sockets.
    // ------------------------------------------------------------------
    internal static class LanNet
    {
        public const int Port = 27315;
        public const byte Proto = 1;
        public const int Header = 6;
        public static readonly byte[] Magic = { (byte)'D', (byte)'W', (byte)'M', (byte)'P' };

        public const byte TDiscover = 1; // client -> broadcast
        public const byte TOffer = 2;    // host   -> client (unicast)
        public const byte TJoin = 3;     // client -> host
        public const byte TAccept = 4;   // host   -> client
        public const byte TReject = 5;   // host   -> client
        public const byte TData = 6;     // both ways
        public const byte TBye = 7;      // both ways
        public const byte TPing = 8;     // both ways
        public const byte TPong = 9;     // both ways
        public const byte TStart = 10;   // host   -> clients
        public const byte TEnd = 11;     // host   -> clients

        public static bool HasHeader(byte[] b, int n)
            => n >= Header
               && b[0] == Magic[0] && b[1] == Magic[1]
               && b[2] == Magic[2] && b[3] == Magic[3]
               && b[4] == Proto;

        public static int ReadInt(byte[] b, int o)
            => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        public static void WriteInt(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        public static long ReadLong(byte[] b, int o)
            => (uint)ReadInt(b, o) | ((long)(uint)ReadInt(b, o + 4) << 32);

        public static void WriteLong(byte[] b, int o, long v)
        {
            WriteInt(b, o, (int)v);
            WriteInt(b, o + 4, (int)(v >> 32));
        }

        public static byte[] HeaderPacket(byte type)
        {
            byte[] p = new byte[Header];
            Buffer.BlockCopy(Magic, 0, p, 0, 4);
            p[4] = Proto;
            p[5] = type;
            return p;
        }

        public static byte[] EncodeUtf8(string s)
            => string.IsNullOrEmpty(s) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(s);

        public static string DecodeUtf8(byte[] b, int o, int len)
            => len <= 0 ? "" : Encoding.UTF8.GetString(b, o, len);
    }

    internal sealed class ReceivedPacket
    {
        public NetworkGamer Sender;
        public byte[] Data;
        public ReceivedPacket(NetworkGamer sender, byte[] data) { Sender = sender; Data = data; }
    }

    public sealed class NetworkMachine
    {
        public GamerCollection<NetworkGamer> Gamers { get; } = new GamerCollection<NetworkGamer>();
    }

    public class NetworkGamer : Gamer
    {
        public NetworkMachine Machine { get; internal set; }
        public bool IsReady { get; set; }
        public TimeSpan RoundtripTime { get; set; } = TimeSpan.Zero;
        public LeaderboardWriter LeaderboardWriter { get; } = new LeaderboardWriter();
        internal virtual bool HasVoice => false;
        internal virtual bool IsLocal => false;
        internal virtual bool IsTalking => false;
        internal virtual bool IsMutedByLocalUser => false;

        // PORT (mp)
        internal int GamerId;
        internal IPEndPoint Endpoint;
        internal long LastSeenTick;
    }

    public sealed class LocalNetworkGamer : NetworkGamer
    {
        public SignedInGamer SignedInGamer { get; internal set; }
        internal NetworkSession Session;

        // PORT (mp): inbound packets for this local gamer, filled by Session.Update.
        internal readonly Queue<ReceivedPacket> Incoming = new Queue<ReceivedPacket>();

        public bool IsDataAvailable => Incoming.Count > 0;

        internal override bool IsLocal => true;

        public void EnableSendVoice(NetworkGamer remoteGamer, bool enable) { }

        public void SendData(byte[] data, SendDataOptions options)
            => Session?.SendLocal(this, data, 0, data?.Length ?? 0, null);
        public void SendData(byte[] data, SendDataOptions options, NetworkGamer recipient)
            => Session?.SendLocal(this, data, 0, data?.Length ?? 0, recipient);
        public void SendData(byte[] data, int offset, int count, SendDataOptions options)
            => Session?.SendLocal(this, data, offset, count, null);
        public void SendData(byte[] data, int offset, int count, SendDataOptions options, NetworkGamer recipient)
            => Session?.SendLocal(this, data, offset, count, recipient);
        public void SendData(PacketWriter data, SendDataOptions options)
            => Session?.SendLocal(this, data, options, null);
        public void SendData(PacketWriter data, SendDataOptions options, NetworkGamer recipient)
            => Session?.SendLocal(this, data, options, recipient);

        public int ReceiveData(byte[] data, out NetworkGamer sender)
            => ReceiveData(data, 0, out sender);
        public int ReceiveData(byte[] data, int offset, out NetworkGamer sender)
        {
            sender = null;
            if (Incoming.Count == 0) return 0;
            ReceivedPacket p = Incoming.Dequeue();
            sender = p.Sender;
            int n = data.Length - offset;
            if (n > p.Data.Length) n = p.Data.Length;
            Buffer.BlockCopy(p.Data, 0, data, offset, n);
            return n;
        }
        public int ReceiveData(PacketReader data, out NetworkGamer sender)
        {
            sender = null;
            if (Incoming.Count == 0) return 0;
            ReceivedPacket p = Incoming.Dequeue();
            sender = p.Sender;
            data.Load(p.Data, 0, p.Data.Length);
            return p.Data.Length;
        }
    }

    public class NetworkSessionProperties
    {
        public string GameMode { get; set; }
        public int? this[int index]
        {
            get => null;
            set { }
        }
    }

    public sealed class AvailableNetworkSession
    {
        public QualityOfService QualityOfService { get; internal set; }
        public NetworkSessionProperties SessionProperties { get; internal set; }
        public int CurrentGamerCount { get; internal set; }
        public int OpenPublicGamerSlots { get; internal set; }
        public int OpenPrivateGamerSlots { get; internal set; }
        public string HostGamertag { get; internal set; }

        // PORT (mp): everything needed to actually join this LAN host.
        internal IPEndPoint HostEndpoint;
        internal int SessionId;
        internal int MaxGamers;
        internal NetworkSessionType Type;
        internal SignedInGamer[] LocalGamers;
    }

    public sealed class AvailableNetworkSessionCollection : ReadOnlyCollection<AvailableNetworkSession>, IDisposable
    {
        public AvailableNetworkSessionCollection() : base(new List<AvailableNetworkSession>()) { }
        internal AvailableNetworkSessionCollection(IList<AvailableNetworkSession> list) : base(list) { }
        public void Dispose() { }
    }

    public struct QualityOfService
    {
        public int AverageRoundtripTime { get; internal set; }
    }

    public class LeaderboardEntry
    {
        public long Rating { get; set; }
        public long Rank { get; set; }
        public Gamer Gamer { get; set; }
        public IDictionary<string, object> Columns { get; } = new Dictionary<string, object>();
    }

    public sealed class LeaderboardWriter
    {
        public LeaderboardEntry GetLeaderboard(string leaderboardKey) => new LeaderboardEntry();
    }

    public sealed class LeaderboardReader : IDisposable
    {
        private static readonly List<LeaderboardEntry> Empty = new List<LeaderboardEntry>();

        public string LeaderboardKey { get; internal set; }
        public int TotalLeaderboardSize { get; internal set; }
        public int PageStart { get; internal set; }
        public IList<LeaderboardEntry> Entries => Empty;
        public bool IsDisposed { get; private set; }
        public bool CanPageUp => false;
        public bool CanPageDown => false;

        public static LeaderboardReader Read(string leaderboardKey, IEnumerable<Gamer> gamers, Gamer pivotGamer, int pageSize)
            => new LeaderboardReader { LeaderboardKey = leaderboardKey };
        public static LeaderboardReader Read(string leaderboardKey, Gamer pivotGamer, int pageSize)
            => new LeaderboardReader { LeaderboardKey = leaderboardKey };
        public static LeaderboardReader Read(string leaderboardKey, int pageStart, int pageSize)
            => new LeaderboardReader { LeaderboardKey = leaderboardKey };

        public static IAsyncResult BeginRead(string leaderboardKey, IEnumerable<Gamer> gamers, Gamer pivotGamer, int pageSize, AsyncCallback callback, object asyncState)
        {
            var result = new ShimNetAsyncResult(asyncState);
            callback?.Invoke(result);
            return result;
        }
        public static IAsyncResult BeginRead(string leaderboardKey, Gamer pivotGamer, int pageSize, AsyncCallback callback, object asyncState)
        {
            var result = new ShimNetAsyncResult(asyncState);
            callback?.Invoke(result);
            return result;
        }
        public static IAsyncResult BeginRead(string leaderboardKey, int pageStart, int pageSize, AsyncCallback callback, object asyncState)
        {
            var result = new ShimNetAsyncResult(asyncState);
            callback?.Invoke(result);
            return result;
        }

        public static LeaderboardReader EndRead(IAsyncResult result)
            => new LeaderboardReader { LeaderboardKey = string.Empty, TotalLeaderboardSize = 0 };

        public void Dispose() { IsDisposed = true; }
        public void PageUp() { }
        public void PageDown() { }
        public IAsyncResult BeginPageUp(AsyncCallback callback, object asyncState) => new ShimNetAsyncResult(asyncState);
        public IAsyncResult BeginPageDown(AsyncCallback callback, object asyncState) => new ShimNetAsyncResult(asyncState);
        public void EndPageUp(IAsyncResult result) { }
        public void EndPageDown(IAsyncResult result) { }
    }

    public sealed class NetworkSessionEndedEventArgs : EventArgs
    {
        public NetworkSessionEndReason EndReason { get; internal set; }
    }
    public sealed class GamerJoinedEventArgs : EventArgs
    {
        public NetworkGamer Gamer { get; internal set; }
    }
    public sealed class GamerLeftEventArgs : EventArgs
    {
        public NetworkGamer Gamer { get; internal set; }
    }
    public sealed class GameStartedEventArgs : EventArgs { }
    public sealed class GameEndedEventArgs : EventArgs { }
    public sealed class HostChangedEventArgs : EventArgs { }
    public sealed class InviteAcceptedEventArgs : EventArgs
    {
        public Gamer Gamer { get; internal set; }
    }

    public sealed class NetworkSession : IDisposable
    {
        public const int MaxSupportedGamers = 31;

        public NetworkSessionType SessionType { get; internal set; }
        public NetworkSessionState SessionState { get; internal set; }
        public GamerCollection<NetworkGamer> AllGamers { get; } = new GamerCollection<NetworkGamer>();
        public GamerCollection<LocalNetworkGamer> LocalGamers { get; } = new GamerCollection<LocalNetworkGamer>();
        public GamerCollection<NetworkGamer> RemoteGamers { get; } = new GamerCollection<NetworkGamer>();
        public bool IsEveryoneReady => true;
        public int MaxGamers { get; internal set; }
        public int PrivateGamerSlots { get; internal set; }
        public bool AllowJoinInProgress { get; set; }
        public bool AllowHostMigration { get; set; }
        public int BytesPerSecondSent { get; internal set; }
        public int BytesPerSecondReceived { get; internal set; }
        public float SimulatedPacketLoss { get; set; }
        public TimeSpan SimulatedLatency { get; set; }

        public event EventHandler<NetworkSessionEndedEventArgs> SessionEnded;
        public event EventHandler<GamerJoinedEventArgs> GamerJoined;
        public event EventHandler<GamerLeftEventArgs> GamerLeft;
        public event EventHandler<GameStartedEventArgs> GameStarted;
        public event EventHandler<GameEndedEventArgs> GameEnded;
        public event EventHandler<HostChangedEventArgs> HostChanged;
        public event EventHandler WriteLeaderboard;
        public static event EventHandler<InviteAcceptedEventArgs> InviteAccepted;

        internal static void RaiseInviteAccepted() => InviteAccepted?.Invoke(null, new InviteAcceptedEventArgs());

        // ---- PORT (mp) state ----
        private static int _sessionCounter;
        private static readonly string LocalTag = MakeTag();

        internal bool IsHost;
        internal bool LanEnabled;      // sockets in use (SystemLink)
        internal bool Disposed;
        internal int SessionId;
        internal Socket Udp;
        internal IPEndPoint HostEndpoint;
        internal int NextGamerId;
        internal readonly Dictionary<int, NetworkGamer> RemoteById = new Dictionary<int, NetworkGamer>();
        internal long LastSendTick;
        internal long LastRecvTick;
        internal int BytesSentRaw;
        internal int BytesRecvRaw;
        private long _bytesWindowTick;
        private int _bytesSentWindow;

        private NetworkSession() { SessionState = NetworkSessionState.Lobby; }

        private static string MakeTag()
        {
            // Cosmetic per-device suffix so two phones with the same gamertag
            // ("Player") are distinguishable in the lobby.
            string s = Guid.NewGuid().ToString("N");
            return s.Substring(0, 3).ToUpperInvariant();
        }

        // ==================================================================
        //  Static factory API
        // ==================================================================

        public static NetworkSession Create(NetworkSessionType sessionType, int maxLocalGamers, int maxGamers)
            => CreateInternal(sessionType, SyntheticGamers(maxLocalGamers), maxGamers, 0, null);
        public static NetworkSession Create(NetworkSessionType sessionType, int maxLocalGamers, int maxGamers, int privateGamerSlots, NetworkSessionProperties sessionProperties)
            => CreateInternal(sessionType, SyntheticGamers(maxLocalGamers), maxGamers, privateGamerSlots, sessionProperties);
        public static NetworkSession Create(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, int maxGamers, int privateGamerSlots, NetworkSessionProperties sessionProperties)
            => CreateInternal(sessionType, localGamers, maxGamers, privateGamerSlots, sessionProperties);

        public static IAsyncResult BeginCreate(NetworkSessionType sessionType, int maxLocalGamers, int maxGamers, AsyncCallback callback, object asyncState)
            => BeginCreateInternal(sessionType, SyntheticGamers(maxLocalGamers), maxGamers, 0, null, callback, asyncState);
        public static IAsyncResult BeginCreate(NetworkSessionType sessionType, int maxLocalGamers, int maxGamers, int privateGamerSlots, NetworkSessionProperties sessionProperties, AsyncCallback callback, object asyncState)
            => BeginCreateInternal(sessionType, SyntheticGamers(maxLocalGamers), maxGamers, privateGamerSlots, sessionProperties, callback, asyncState);
        public static IAsyncResult BeginCreate(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, int maxGamers, int privateGamerSlots, NetworkSessionProperties sessionProperties, AsyncCallback callback, object asyncState)
            => BeginCreateInternal(sessionType, localGamers, maxGamers, privateGamerSlots, sessionProperties, callback, asyncState);
        public static NetworkSession EndCreate(IAsyncResult result)
        {
            if (result is NetOpResult op)
            {
                if (!op.Wait(5000)) throw new TimeoutException("EndCreate timed out");
                if (op.Error != null) throw op.Error;
                return (NetworkSession)op.Result;
            }
            throw new ArgumentException("Invalid IAsyncResult", nameof(result));
        }

        public static AvailableNetworkSessionCollection Find(NetworkSessionType sessionType, int maxLocalGamers, NetworkSessionProperties searchProperties)
            => FindInternal(sessionType, SyntheticGamers(maxLocalGamers), searchProperties);
        public static AvailableNetworkSessionCollection Find(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, NetworkSessionProperties searchProperties)
            => FindInternal(sessionType, localGamers, searchProperties);
        public static IAsyncResult BeginFind(NetworkSessionType sessionType, int maxLocalGamers, NetworkSessionProperties searchProperties, AsyncCallback callback, object asyncState)
            => BeginFindInternal(sessionType, SyntheticGamers(maxLocalGamers), searchProperties, callback, asyncState);
        public static IAsyncResult BeginFind(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, NetworkSessionProperties searchProperties, AsyncCallback callback, object asyncState)
            => BeginFindInternal(sessionType, localGamers, searchProperties, callback, asyncState);
        public static AvailableNetworkSessionCollection EndFind(IAsyncResult result)
        {
            if (result is NetOpResult op)
            {
                if (!op.Wait(8000)) throw new TimeoutException("EndFind timed out");
                if (op.Error != null) throw op.Error;
                return (AvailableNetworkSessionCollection)op.Result;
            }
            throw new ArgumentException("Invalid IAsyncResult", nameof(result));
        }

        public static NetworkSession Join(AvailableNetworkSession availableSession)
            => JoinBlocking(availableSession);

        public static IAsyncResult BeginJoin(AvailableNetworkSession availableSession, AsyncCallback callback, object asyncState)
        {
            var op = new NetOpResult(asyncState);
            Thread t = new Thread(() =>
            {
                try { op.Result = JoinBlocking(availableSession); }
                catch (Exception ex) { op.Error = ex; }
                finally { op.Complete(); callback?.Invoke(op); }
            });
            t.IsBackground = true;
            t.Start();
            return op;
        }
        public static NetworkSession EndJoin(IAsyncResult result)
        {
            if (result is NetOpResult op)
            {
                if (!op.Wait(6000)) throw new TimeoutException("EndJoin timed out");
                if (op.Error != null) throw op.Error;
                return (NetworkSession)op.Result;
            }
            throw new ArgumentException("Invalid IAsyncResult", nameof(result));
        }

        // Xbox LIVE invitations do not exist on this port.
        public static NetworkSession JoinInvited(int maxLocalGamers)
            => throw new NotSupportedException("Xbox LIVE invitations are not available on this port.");
        public static NetworkSession JoinInvited(IEnumerable<SignedInGamer> localGamers)
            => throw new NotSupportedException("Xbox LIVE invitations are not available on this port.");
        public static IAsyncResult BeginJoinInvited(int maxLocalGamers, AsyncCallback callback, object asyncState)
            => throw new NotSupportedException("Xbox LIVE invitations are not available on this port.");
        public static IAsyncResult BeginJoinInvited(IEnumerable<SignedInGamer> localGamers, AsyncCallback callback, object asyncState)
            => throw new NotSupportedException("Xbox LIVE invitations are not available on this port.");
        public static NetworkSession EndJoinInvited(IAsyncResult result)
            => throw new NotSupportedException("Xbox LIVE invitations are not available on this port.");

        // ==================================================================
        //  Factory internals
        // ==================================================================

        private static List<SignedInGamer> SyntheticGamers(int count)
        {
            var list = new List<SignedInGamer>();
            for (int i = 0; i < count && i < 4; i++)
                list.Add(new SignedInGamer { Gamertag = "Player" + (i + 1), PlayerIndex = (PlayerIndex)i });
            return list;
        }

        private static NetworkSession CreateInternal(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, int maxGamers, int privateGamerSlots, NetworkSessionProperties props)
        {
            var gamers = new List<SignedInGamer>();
            if (localGamers != null)
                foreach (SignedInGamer g in localGamers)
                    if (g != null) gamers.Add(g);
            if (gamers.Count == 0)
                gamers.Add(Gamer.SignedInGamers.Count > 0 ? Gamer.SignedInGamers[0] : new SignedInGamer { Gamertag = "Player", PlayerIndex = PlayerIndex.One });

            var s = new NetworkSession
            {
                SessionType = sessionType,
                MaxGamers = maxGamers > 0 ? maxGamers : 2,
                PrivateGamerSlots = privateGamerSlots,
                IsHost = true,
                SessionState = NetworkSessionState.Lobby
            };

            // Only SystemLink is a real cross-device LAN session. Local (split
            // screen / leaderboard helper) and PlayerMatch (LIVE, hidden) are
            // inert local sessions with no sockets.
            if (sessionType == NetworkSessionType.SystemLink)
            {
                s.SessionId = Interlocked.Increment(ref _sessionCounter) ^ Environment.TickCount;
                try
                {
                    s.Udp = OpenUdp(LanNet.Port, nonBlocking: true);
                    s.LanEnabled = true;
                }
                catch (Exception ex)
                {
                    Dishwasher.Log.Exception("mp.Create", ex);
                    s.LanEnabled = false;
                }
            }

            for (int i = 0; i < gamers.Count; i++)
            {
                var lg = new LocalNetworkGamer
                {
                    GamerId = i,
                    Gamertag = gamers[i].Gamertag,
                    PlayerIndex = gamers[i].PlayerIndex,
                    SignedInGamer = gamers[i],
                    Session = s,
                    Machine = new NetworkMachine()
                };
                s.LocalGamers.Add(lg);
                s.AllGamers.Add(lg);
            }
            s.NextGamerId = gamers.Count;
            Dishwasher.Log.Info($"[mp] {(sessionType == NetworkSessionType.SystemLink ? "host" : "local")} session created id={s.SessionId} port={(s.LanEnabled ? LanNet.Port.ToString() : "-")} gamers={gamers.Count}");
            return s;
        }

        private static IAsyncResult BeginCreateInternal(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, int maxGamers, int privateGamerSlots, NetworkSessionProperties props, AsyncCallback callback, object asyncState)
        {
            var op = new NetOpResult(asyncState);
            try
            {
                op.Result = CreateInternal(sessionType, localGamers, maxGamers, privateGamerSlots, props);
            }
            catch (Exception ex) { op.Error = ex; }
            op.Complete();
            callback?.Invoke(op);
            return op;
        }

        private static AvailableNetworkSessionCollection FindInternal(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, NetworkSessionProperties props)
        {
            var local = new List<SignedInGamer>();
            if (localGamers != null)
                foreach (SignedInGamer g in localGamers)
                    if (g != null) local.Add(g);

            if (sessionType != NetworkSessionType.SystemLink)
            {
                // No online (LIVE) discovery on this port.
                return new AvailableNetworkSessionCollection(new List<AvailableNetworkSession>());
            }

            List<AvailableNetworkSession> found = Discover(1300);
            foreach (AvailableNetworkSession a in found)
            {
                a.LocalGamers = local.ToArray();
                a.Type = sessionType;
            }
            Dishwasher.Log.Info($"[mp] find: {found.Count} session(s)");
            return new AvailableNetworkSessionCollection(found);
        }

        private static IAsyncResult BeginFindInternal(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, NetworkSessionProperties props, AsyncCallback callback, object asyncState)
        {
            var op = new NetOpResult(asyncState);
            if (sessionType != NetworkSessionType.SystemLink)
            {
                op.Result = new AvailableNetworkSessionCollection(new List<AvailableNetworkSession>());
                op.Complete();
                callback?.Invoke(op);
                return op;
            }
            Thread t = new Thread(() =>
            {
                try { op.Result = FindInternal(sessionType, localGamers, props); }
                catch (Exception ex) { op.Error = ex; }
                finally { op.Complete(); callback?.Invoke(op); }
            });
            t.IsBackground = true;
            t.Start();
            return op;
        }

        // ------------------------------------------------------------------
        //  Discovery: broadcast + subnet-directed broadcast + /24 scan.
        // ------------------------------------------------------------------
        private static List<AvailableNetworkSession> Discover(int listenMs)
        {
            var found = new Dictionary<string, AvailableNetworkSession>();
            Socket sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sock.Bind(new IPEndPoint(IPAddress.Any, 0));
                sock.EnableBroadcast = true;

                List<IPEndPoint> bcast = BroadcastTargets();
                List<IPEndPoint> scan = ScanTargets();
                byte[] probe = LanNet.HeaderPacket(LanNet.TDiscover);

                int deadline = Environment.TickCount + listenMs;
                int scanTick = 0;
                while (true)
                {
                    int remain = deadline - Environment.TickCount;
                    if (remain <= 0) break;
                    int wait = Math.Min(remain, 250);
                    if (sock.Poll(wait * 1000, SelectMode.SelectRead))
                    {
                        var buf = new byte[2048];
                        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        int n;
                        try { n = sock.ReceiveFrom(buf, ref ep); } catch (SocketException) { continue; }
                        if (n > 0) ParseOffer(buf, n, (IPEndPoint)ep, found);
                    }
                    else
                    {
                        // (Re)broadcast the probe periodically.
                        SendAll(sock, probe, bcast);
                        // Run the unicast fallback scan once, ~half-way through.
                        if (scanTick == 0 && Environment.TickCount - (deadline - listenMs) > listenMs / 2 - 200)
                        {
                            scanTick = 1;
                            SendAll(sock, probe, scan);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Dishwasher.Log.Exception("mp.discover", ex);
            }
            finally
            {
                try { sock?.Close(); } catch { }
            }
            return new List<AvailableNetworkSession>(found.Values);
        }

        private static void ParseOffer(byte[] b, int n, IPEndPoint from, Dictionary<string, AvailableNetworkSession> found)
        {
            if (!LanNet.HasHeader(b, n) || b[5] != LanNet.TOffer || n < LanNet.Header + 6) return;
            int sessionId = LanNet.ReadInt(b, LanNet.Header);
            int count = b[LanNet.Header + 4];
            int max = b[LanNet.Header + 5];
            int tagLen = b[LanNet.Header + 6];
            string tag = tagLen > 0 && LanNet.Header + 7 + tagLen <= n
                ? LanNet.DecodeUtf8(b, LanNet.Header + 7, tagLen) : "Player";

            string key = from.Address + ":" + from.Port + "#" + sessionId;
            if (found.ContainsKey(key)) return;
            found[key] = new AvailableNetworkSession
            {
                HostEndpoint = from,
                SessionId = sessionId,
                HostGamertag = tag,
                CurrentGamerCount = count,
                MaxGamers = max,
                OpenPublicGamerSlots = Math.Max(0, max - count),
                OpenPrivateGamerSlots = 0,
                QualityOfService = new QualityOfService { AverageRoundtripTime = 0 },
                SessionProperties = new NetworkSessionProperties(),
                Type = NetworkSessionType.SystemLink
            };
        }

        private static List<IPEndPoint> BroadcastTargets()
        {
            var seen = new HashSet<string>();
            var result = new List<IPEndPoint>();
            void Add(IPAddress a)
            {
                if (a == null) return;
                string s = a.ToString();
                if (seen.Add(s)) result.Add(new IPEndPoint(a, LanNet.Port));
            }

            Add(IPAddress.Broadcast); // 255.255.255.255
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        byte[] m = ua.IPv4Mask.GetAddressBytes();
                        byte[] b = { (byte)(ip[0] | (m[0] ^ 255)), (byte)(ip[1] | (m[1] ^ 255)), (byte)(ip[2] | (m[2] ^ 255)), (byte)(ip[3] | (m[3] ^ 255)) };
                        Add(new IPAddress(b));
                    }
                }
            }
            catch { }
            return result;
        }

        private static List<IPEndPoint> ScanTargets()
        {
            var seen = new HashSet<string>();
            var result = new List<IPEndPoint>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        for (int host = 1; host <= 254; host++)
                        {
                            if (host == ip[3]) continue;
                            byte[] b = { ip[0], ip[1], ip[2], (byte)host };
                            string s = new IPAddress(b).ToString();
                            if (seen.Add(s)) result.Add(new IPEndPoint(new IPAddress(b), LanNet.Port));
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private static void SendAll(Socket sock, byte[] data, List<IPEndPoint> targets)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                try { sock.SendTo(data, targets[i]); } catch { }
            }
        }

        // ------------------------------------------------------------------
        //  Join
        // ------------------------------------------------------------------
        private static NetworkSession JoinBlocking(AvailableNetworkSession available)
        {
            if (available == null || available.HostEndpoint == null)
                throw new InvalidOperationException("No host endpoint");

            Socket sock = OpenUdp(0, nonBlocking: false);
            try
            {
                byte[] tag = LanNet.EncodeUtf8(Gamer.SignedInGamers.Count > 0 ? Gamer.SignedInGamers[0].Gamertag : "Player");
                byte[] join = new byte[LanNet.Header + 1 + tag.Length];
                Buffer.BlockCopy(LanNet.Magic, 0, join, 0, 4);
                join[4] = LanNet.Proto;
                join[5] = LanNet.TJoin;
                join[LanNet.Header] = (byte)tag.Length;
                Buffer.BlockCopy(tag, 0, join, LanNet.Header + 1, tag.Length);

                int deadline = Environment.TickCount + 3500;
                int nextProbe = 0;
                while (Environment.TickCount < deadline)
                {
                    if (Environment.TickCount >= nextProbe)
                    {
                        try { sock.SendTo(join, available.HostEndpoint); } catch { }
                        nextProbe = Environment.TickCount + 500;
                    }
                    if (!sock.Poll(150 * 1000, SelectMode.SelectRead)) continue;
                    var buf = new byte[2048];
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = sock.ReceiveFrom(buf, ref ep); } catch (SocketException) { continue; }
                    if (!LanNet.HasHeader(buf, n)) continue;
                    if (buf[5] == LanNet.TReject)
                        throw new InvalidOperationException("Host rejected the join (session full).");
                    if (buf[5] != LanNet.TAccept || n < LanNet.Header + 8) continue;

                    int sessionId = LanNet.ReadInt(buf, LanNet.Header);
                    int hostId = buf[LanNet.Header + 4];
                    int assignedId = buf[LanNet.Header + 5];
                    int tagLen = buf[LanNet.Header + 6];
                    int maxGamers = buf[LanNet.Header + 7];
                    string hostTag = tagLen > 0 && LanNet.Header + 8 + tagLen <= n
                        ? LanNet.DecodeUtf8(buf, LanNet.Header + 8, tagLen) : "Player";

                    var s = new NetworkSession
                    {
                        SessionType = NetworkSessionType.SystemLink,
                        IsHost = false,
                        LanEnabled = true,
                        Udp = sock,
                        HostEndpoint = (IPEndPoint)ep,
                        SessionId = sessionId,
                        MaxGamers = maxGamers > 0 ? maxGamers : available.MaxGamers,
                        PrivateGamerSlots = 0,
                        SessionState = NetworkSessionState.Lobby,
                        LastRecvTick = Environment.TickCount64
                    };

                    var hostGamer = new NetworkGamer
                    {
                        GamerId = hostId,
                        Gamertag = hostTag + " #1",   // PORT (mp): distinguish from local "Player"
                        Endpoint = (IPEndPoint)ep,
                        LastSeenTick = Environment.TickCount64,
                        Machine = new NetworkMachine()
                    };
                    s.RemoteById[hostId] = hostGamer;
                    s.RemoteGamers.Add(hostGamer);
                    s.AllGamers.Add(hostGamer);      // AllGamers[0] == host

                    var locals = available.LocalGamers;
                    int localCount = (locals != null && locals.Length > 0) ? locals.Length : 1;
                    for (int i = 0; i < localCount; i++)
                    {
                        SignedInGamer sg = (locals != null && i < locals.Length) ? locals[i]
                            : (Gamer.SignedInGamers.Count > 0 ? Gamer.SignedInGamers[0] : new SignedInGamer { Gamertag = "Player" });
                        var lg = new LocalNetworkGamer
                        {
                            GamerId = assignedId + i,
                            Gamertag = sg.Gamertag,      // exact tag: Leader.cs matches on it
                            PlayerIndex = sg.PlayerIndex,
                            SignedInGamer = sg,
                            Session = s,
                            Machine = new NetworkMachine()
                        };
                        s.LocalGamers.Add(lg);
                        s.AllGamers.Add(lg);         // AllGamers[1] == local player
                    }
                    s.NextGamerId = assignedId + localCount;
                    try { sock.Blocking = false; } catch { }   // pump via Poll in Update
                    Dishwasher.Log.Info($"[mp] joined host={s.HostEndpoint} id={sessionId} assigned={assignedId}");
                    return s;
                }
                throw new TimeoutException("No response from host (LAN).");
            }
            catch
            {
                try { sock.Close(); } catch { }
                throw;
            }
        }

        // ==================================================================
        //  Per-frame pump
        // ==================================================================
        public void Update()
        {
            if (Disposed || !LanEnabled || Udp == null) return;
            PumpSocket();
            Heartbeat();
            RollByteCounters();
        }

        private void PumpSocket()
        {
            for (int guard = 0; guard < 256; guard++)
            {
                bool readable;
                try { readable = Udp.Poll(0, SelectMode.SelectRead); }
                catch (Exception) { return; }
                if (!readable) return;

                int avail;
                try { avail = Udp.Available; } catch (Exception) { return; }
                if (avail <= 0) return;
                if (avail > 65535) avail = 65535;
                var buf = new byte[avail];
                EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = Udp.ReceiveFrom(buf, ref ep); }
                catch (SocketException) { continue; }
                if (n <= 0) continue;
                BytesRecvRaw += n;
                LastRecvTick = Environment.TickCount64;
                TouchRemote((IPEndPoint)ep);
                try { HandlePacket(buf, n, (IPEndPoint)ep); }
                catch (Exception ex) { Dishwasher.Log.Exception("mp.rx", ex); }
            }
        }

        private void HandlePacket(byte[] b, int n, IPEndPoint from)
        {
            if (!LanNet.HasHeader(b, n)) return;
            byte type = b[5];

            if (IsHost)
            {
                switch (type)
                {
                    case LanNet.TDiscover: SendOffer(from); break;
                    case LanNet.TJoin: HandleJoin(b, n, from); break;
                    case LanNet.TData: HandleData(b, n, from); break;
                    case LanNet.TBye: RemoveByEndpoint(from, LanNet.Header < n ? b[LanNet.Header] : (byte)255); break;
                    case LanNet.TPing: SendPong(from, b, n); break;
                    case LanNet.TPong: HandlePong(from, b, n); break;
                }
            }
            else
            {
                switch (type)
                {
                    case LanNet.TData: HandleData(b, n, from); break;
                    case LanNet.TStart:
                        if (SessionState != NetworkSessionState.Playing)
                        {
                            SessionState = NetworkSessionState.Playing;
                            GameStarted?.Invoke(this, new GameStartedEventArgs());
                        }
                        break;
                    case LanNet.TEnd:
                        SessionState = NetworkSessionState.Ended;
                        GameEnded?.Invoke(this, new GameEndedEventArgs());
                        break;
                    case LanNet.TBye:
                        SessionState = NetworkSessionState.Ended;
                        SessionEnded?.Invoke(this, new NetworkSessionEndedEventArgs { EndReason = NetworkSessionEndReason.HostEndedSession });
                        break;
                    case LanNet.TPing: SendPong(from, b, n); break;
                    case LanNet.TPong: HandlePong(from, b, n); break;
                }
            }
        }

        private void HandleData(byte[] b, int n, IPEndPoint from)
        {
            if (n < LanNet.Header + 1) return;
            int senderId = b[LanNet.Header];
            if (!RemoteById.TryGetValue(senderId, out NetworkGamer sender)) return;
            sender.LastSeenTick = Environment.TickCount64;
            int len = n - LanNet.Header - 1;
            byte[] payload = new byte[len];
            if (len > 0) Buffer.BlockCopy(b, LanNet.Header + 1, payload, 0, len);
            for (int i = 0; i < LocalGamers.Count; i++)
                LocalGamers[i].Incoming.Enqueue(new ReceivedPacket(sender, payload));
        }

        private void SendOffer(IPEndPoint to)
        {
            string tag = AllGamers.Count > 0 ? (AllGamers[0].Gamertag ?? "Player") : "Player";
            byte[] tagBytes = LanNet.EncodeUtf8(tag);
            byte[] p = new byte[LanNet.Header + 7 + tagBytes.Length];
            Buffer.BlockCopy(LanNet.Magic, 0, p, 0, 4);
            p[4] = LanNet.Proto;
            p[5] = LanNet.TOffer;
            LanNet.WriteInt(p, LanNet.Header, SessionId);
            p[LanNet.Header + 4] = (byte)Math.Min(255, AllGamers.Count);
            p[LanNet.Header + 5] = (byte)Math.Min(255, MaxGamers);
            p[LanNet.Header + 6] = (byte)tagBytes.Length;
            Buffer.BlockCopy(tagBytes, 0, p, LanNet.Header + 7, tagBytes.Length);
            try { Udp.SendTo(p, to); } catch { }
        }

        private void HandleJoin(byte[] b, int n, IPEndPoint from)
        {
            if (n < LanNet.Header + 1) return;
            int tagLen = b[LanNet.Header];
            string tag = tagLen > 0 && LanNet.Header + 1 + tagLen <= n
                ? LanNet.DecodeUtf8(b, LanNet.Header + 1, tagLen) : "Player";

            if (AllGamers.Count >= MaxGamers || RemoteGamers.Count >= (MaxGamers - LocalGamers.Count))
            {
                byte[] rej = LanNet.HeaderPacket(LanNet.TReject);
                try { Udp.SendTo(rej, from); } catch { }
                return;
            }

            int id = NextGamerId++;
            var g = new NetworkGamer
            {
                GamerId = id,
                Gamertag = tag + " #2",   // PORT (mp): distinguish from local "Player"
                Endpoint = from,
                LastSeenTick = Environment.TickCount64,
                Machine = new NetworkMachine()
            };
            RemoteById[id] = g;
            RemoteGamers.Add(g);
            AllGamers.Add(g);

            SendAccept(from, id);
            Dishwasher.Log.Info($"[mp] gamer joined '{g.Gamertag}' id={id} from {from} (total {AllGamers.Count})");
            GamerJoined?.Invoke(this, new GamerJoinedEventArgs { Gamer = g });
        }

        private void SendAccept(IPEndPoint to, int assignedId)
        {
            string hostTag = AllGamers.Count > 0 ? (AllGamers[0].Gamertag ?? "Player") : "Player";
            byte[] tagBytes = LanNet.EncodeUtf8(hostTag);
            // header + sessionId(4) + hostId(1) + assignedId(1) + maxGamers(1) + tagLen(1) + tag
            byte[] p = new byte[LanNet.Header + 8 + tagBytes.Length];
            Buffer.BlockCopy(LanNet.Magic, 0, p, 0, 4);
            p[4] = LanNet.Proto;
            p[5] = LanNet.TAccept;
            LanNet.WriteInt(p, LanNet.Header, SessionId);
            p[LanNet.Header + 4] = 0;                 // host gamer id
            p[LanNet.Header + 5] = (byte)assignedId;
            p[LanNet.Header + 6] = (byte)tagBytes.Length;
            p[LanNet.Header + 7] = (byte)Math.Min(255, MaxGamers);
            Buffer.BlockCopy(tagBytes, 0, p, LanNet.Header + 8, tagBytes.Length);
            try { Udp.SendTo(p, to); } catch { }
        }

        private void SendPong(IPEndPoint to, byte[] ping, int n)
        {
            byte[] p = new byte[LanNet.Header + 8];
            Buffer.BlockCopy(LanNet.Magic, 0, p, 0, 4);
            p[4] = LanNet.Proto;
            p[5] = LanNet.TPong;
            if (n >= LanNet.Header + 8) Buffer.BlockCopy(ping, LanNet.Header, p, LanNet.Header, 8);
            try { Udp.SendTo(p, to); } catch { }
        }

        private void HandlePong(IPEndPoint from, byte[] pong, int n)
        {
            if (n < LanNet.Header + 8) return;
            long sent = LanNet.ReadLong(pong, LanNet.Header);
            long rtt = Environment.TickCount64 - sent;
            if (rtt < 0) rtt = 0;
            TimeSpan ts = TimeSpan.FromMilliseconds(rtt);
            for (int i = 0; i < LocalGamers.Count; i++)
                LocalGamers[i].RoundtripTime = ts;
            if (RemoteById.Count > 0)
            {
                foreach (NetworkGamer g in RemoteGamers)
                    if (g.Endpoint != null && g.Endpoint.Equals(from)) g.RoundtripTime = ts;
            }
        }

        // PORT (mp): any datagram from a joined client keeps it alive (includes
        // PING/PONG heartbeat, which is all the lobby exchanges before a game).
        private void TouchRemote(IPEndPoint from)
        {
            if (!IsHost) return;
            for (int i = 0; i < RemoteGamers.Count; i++)
            {
                NetworkGamer g = RemoteGamers[i];
                if (g.Endpoint != null && g.Endpoint.Equals(from))
                {
                    g.LastSeenTick = Environment.TickCount64;
                    return;
                }
            }
        }

        private void RemoveByEndpoint(IPEndPoint from, byte id)
        {
            NetworkGamer g = null;
            if (id != 255 && RemoteById.TryGetValue(id, out NetworkGamer byId) && byId.Endpoint != null && byId.Endpoint.Equals(from))
            {
                g = byId;
            }
            else
            {
                foreach (NetworkGamer rg in RemoteGamers)
                    if (rg.Endpoint != null && rg.Endpoint.Equals(from)) { g = rg; break; }
            }
            if (g == null) return;
            RemoteById.Remove(g.GamerId);
            RemoteGamers.Remove(g);
            AllGamers.Remove(g);
            Dishwasher.Log.Info($"[mp] gamer left '{g.Gamertag}' (total {AllGamers.Count})");
            GamerLeft?.Invoke(this, new GamerLeftEventArgs { Gamer = g });
        }

        private void Heartbeat()
        {
            long now = Environment.TickCount64;
            if (now - LastSendTick >= 1000)
            {
                LastSendTick = now;
                byte[] ping = new byte[LanNet.Header + 8];
                Buffer.BlockCopy(LanNet.Magic, 0, ping, 0, 4);
                ping[4] = LanNet.Proto;
                ping[5] = LanNet.TPing;
                LanNet.WriteLong(ping, LanNet.Header, now);
                if (IsHost)
                {
                    for (int i = 0; i < RemoteGamers.Count; i++)
                    {
                        NetworkGamer g = RemoteGamers[i];
                        try { if (g.Endpoint != null) Udp.SendTo(ping, g.Endpoint); } catch { }
                    }
                }
                else if (HostEndpoint != null)
                {
                    try { Udp.SendTo(ping, HostEndpoint); } catch { }
                }
            }

            // Drop clients that went silent (host), or detect a dead host (client).
            if (IsHost)
            {
                for (int i = RemoteGamers.Count - 1; i >= 0; i--)
                {
                    NetworkGamer g = RemoteGamers[i];
                    if (now - g.LastSeenTick > 6000)
                    {
                        RemoteById.Remove(g.GamerId);
                        RemoteGamers.RemoveAt(i);
                        AllGamers.Remove(g);
                        Dishwasher.Log.Info($"[mp] gamer timeout '{g.Gamertag}'");
                        GamerLeft?.Invoke(this, new GamerLeftEventArgs { Gamer = g });
                    }
                }
            }
            else if (now - LastRecvTick > 6000 && SessionState != NetworkSessionState.Ended)
            {
                SessionState = NetworkSessionState.Ended;
                Dishwasher.Log.Info("[mp] host timeout -> session ended");
                SessionEnded?.Invoke(this, new NetworkSessionEndedEventArgs { EndReason = NetworkSessionEndReason.Disconnected });
            }
        }

        private void RollByteCounters()
        {
            long now = Environment.TickCount64;
            if (now - _bytesWindowTick >= 1000)
            {
                long dt = now - _bytesWindowTick;
                if (dt <= 0) dt = 1000;
                BytesPerSecondSent = (int)(_bytesSentWindow * 1000 / dt);
                BytesPerSecondReceived = BytesRecvRaw; // raw, window-approx below
                _bytesSentWindow = 0;
                _bytesWindowTick = now;
                BytesRecvRaw = 0;
            }
        }

        // ==================================================================
        //  Sending
        // ==================================================================
        internal void SendLocal(LocalNetworkGamer gamer, PacketWriter writer, SendDataOptions options, NetworkGamer recipient)
        {
            byte[] payload;
            int len = writer.Length;
            byte[] buf = writer.ByteArray;
            payload = new byte[len];
            if (len > 0) Buffer.BlockCopy(buf, 0, payload, 0, len);
            writer.Clear(); // XNA SendData consumes the writer's contents
            SendPayload(gamer, payload, recipient);
        }

        internal void SendLocal(LocalNetworkGamer gamer, byte[] data, int offset, int count, NetworkGamer recipient)
        {
            if (data == null || count <= 0) { SendPayload(gamer, Array.Empty<byte>(), recipient); return; }
            byte[] payload = new byte[count];
            Buffer.BlockCopy(data, offset, payload, 0, count);
            SendPayload(gamer, payload, recipient);
        }

        private void SendPayload(LocalNetworkGamer gamer, byte[] payload, NetworkGamer recipient)
        {
            if (!LanEnabled || Udp == null || Disposed) return;
            byte[] p = new byte[LanNet.Header + 1 + payload.Length];
            Buffer.BlockCopy(LanNet.Magic, 0, p, 0, 4);
            p[4] = LanNet.Proto;
            p[5] = LanNet.TData;
            p[LanNet.Header] = (byte)gamer.GamerId;
            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, p, LanNet.Header + 1, payload.Length);
            _bytesSentWindow += p.Length;

            if (recipient != null)
            {
                if (recipient.Endpoint != null)
                {
                    try { Udp.SendTo(p, recipient.Endpoint); } catch { }
                }
                return;
            }

            if (IsHost)
            {
                for (int i = 0; i < RemoteGamers.Count; i++)
                {
                    IPEndPoint ep = RemoteGamers[i].Endpoint;
                    if (ep == null) continue;
                    try { Udp.SendTo(p, ep); } catch { }
                }
            }
            else if (HostEndpoint != null)
            {
                try { Udp.SendTo(p, HostEndpoint); } catch { }
            }
        }

        private void BroadcastControl(byte type)
        {
            if (!LanEnabled || Udp == null || !IsHost) return;
            byte[] p = LanNet.HeaderPacket(type);
            for (int i = 0; i < RemoteGamers.Count; i++)
            {
                IPEndPoint ep = RemoteGamers[i].Endpoint;
                if (ep == null) continue;
                try { Udp.SendTo(p, ep); } catch { }
            }
        }

        private void SendBye()
        {
            if (!LanEnabled || Udp == null) return;
            byte[] p = new byte[LanNet.Header + 1];
            Buffer.BlockCopy(LanNet.Magic, 0, p, 0, 4);
            p[4] = LanNet.Proto;
            p[5] = LanNet.TBye;
            p[LanNet.Header] = (byte)(LocalGamers.Count > 0 ? LocalGamers[0].GamerId : 0);
            if (IsHost)
            {
                for (int i = 0; i < RemoteGamers.Count; i++)
                    try { if (RemoteGamers[i].Endpoint != null) Udp.SendTo(p, RemoteGamers[i].Endpoint); } catch { }
            }
            else if (HostEndpoint != null)
            {
                try { Udp.SendTo(p, HostEndpoint); } catch { }
            }
        }

        // ==================================================================
        //  Lifecycle
        // ==================================================================
        public void StartGame()
        {
            SessionState = NetworkSessionState.Playing;
            BroadcastControl(LanNet.TStart);
        }

        public void EndGame()
        {
            SessionState = NetworkSessionState.Ended;
            BroadcastControl(LanNet.TEnd);
        }

        public void ResetReady() { }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (LanEnabled && Udp != null)
            {
                try { SendBye(); } catch { }
                try { Udp.Close(); } catch { }
            }
            LocalGamers.Clear();
            RemoteGamers.Clear();
            RemoteById.Clear();
            AllGamers.Clear();
        }

        // ------------------------------------------------------------------
        //  Socket helper
        // ------------------------------------------------------------------
        private static Socket OpenUdp(int port, bool nonBlocking)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (port != 0)
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.Bind(new IPEndPoint(IPAddress.Any, port));
            sock.EnableBroadcast = true;
            sock.Blocking = !nonBlocking;
            return sock;
        }
    }
}
