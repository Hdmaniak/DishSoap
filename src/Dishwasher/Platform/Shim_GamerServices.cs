// Local compatibility shim for Xbox LIVE / GamerServices.
//
// The original game targets XNA 3.0 on Xbox 360. MonoGame has no
// GamerServices/Net/Storage implementation, so this file provides a minimal
// single-player-friendly local player. Everything is hardcoded to one
// signed-in local gamer ("Player", PlayerIndex.One); leaderboards and
// achievement IO are no-ops.
//
// Runtime behaviour: a single local gamer is always present, so all the
// `Gamer.SignedInGamers[...] == null` single-player gates pass.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Microsoft.Xna.Framework.GamerServices
{
    internal sealed class ShimAsyncResult : IAsyncResult
    {
        private readonly ManualResetEvent _handle;
        public ShimAsyncResult(object state = null)
        {
            AsyncState = state;
            _handle = new ManualResetEvent(true);
        }
        public object AsyncState { get; }
        public WaitHandle AsyncWaitHandle => _handle;
        public bool CompletedSynchronously => true;
        public bool IsCompleted => true;
    }

    public class Gamer
    {
        public string Gamertag { get; set; } = "Player";
        public object Tag { get; set; }
        public virtual PlayerIndex PlayerIndex { get; set; } = PlayerIndex.One;
        public virtual bool IsGuest => false;
        public virtual bool IsSignedInToLive => true;

        public GamerProfile GetProfile() => new GamerProfile();
        public IAsyncResult BeginGetProfile(AsyncCallback callback, object asyncState)
        {
            callback?.Invoke(new ShimAsyncResult(asyncState));
            return new ShimAsyncResult(asyncState);
        }
        public GamerProfile EndGetProfile(IAsyncResult result) => new GamerProfile();

        public static SignedInGamerCollection SignedInGamers { get; } = new SignedInGamerCollection();
    }

    public sealed class SignedInGamer : Gamer
    {
        public GameDefaults GameDefaults { get; } = new GameDefaults();
        public GamerPrivileges Privileges { get; } = new GamerPrivileges();
        public GamerPresence Presence { get; } = new GamerPresence();

        public bool IsFriend(Gamer gamer) => false;
        public FriendCollection GetFriends() => new FriendCollection();

        public void AwardAchievement(string achievementKey) { }
        public void AwardGamerPicture(string pictureKey) { }

        public IAsyncResult BeginGetAchievements(AsyncCallback callback, object asyncState)
        {
            callback?.Invoke(new ShimAsyncResult(asyncState));
            return new ShimAsyncResult(asyncState);
        }
        public AchievementCollection EndGetAchievements(IAsyncResult result) => new AchievementCollection();
        public AchievementCollection GetAchievements() => new AchievementCollection();

        public static event EventHandler<SignedInEventArgs> SignedIn;
        public static event EventHandler<SignedOutEventArgs> SignedOut;
        internal static void RaiseSignedIn(SignedInGamer g) => SignedIn?.Invoke(null, new SignedInEventArgs(g));
        internal static void RaiseSignedOut(SignedInGamer g) => SignedOut?.Invoke(null, new SignedOutEventArgs(g));
    }

    public class GamerCollection<T> : List<T> where T : Gamer
    {
        public GamerCollection() { }
        public GamerCollection(IEnumerable<T> collection) : base(collection) { }

        public struct GamerCollectionEnumerator : IEnumerator<T>
        {
            private List<T>.Enumerator _inner;
            internal GamerCollectionEnumerator(List<T> list) { _inner = list.GetEnumerator(); }
            public T Current => _inner.Current;
            object IEnumerator.Current => _inner.Current;
            public bool MoveNext() => _inner.MoveNext();
            public void Reset() => ((IEnumerator)_inner).Reset();
            public void Dispose() => _inner.Dispose();
        }

        public new GamerCollectionEnumerator GetEnumerator() => new GamerCollectionEnumerator(this);
    }

    public sealed class SignedInGamerCollection : GamerCollection<SignedInGamer>
    {
        public SignedInGamerCollection()
        {
            Add(new SignedInGamer { Gamertag = "Player", PlayerIndex = PlayerIndex.One });
        }

        public SignedInGamer this[PlayerIndex index]
        {
            get
            {
                for (int i = 0; i < Count; i++)
                {
                    if (this[i].PlayerIndex == index) return this[i];
                }
                return null;
            }
        }
    }

    public sealed class Achievement
    {
        public bool IsEarned { get; set; }
        public string Key { get; set; }
    }

    public sealed class AchievementCollection : List<Achievement>
    {
        public void Dispose() { }
    }

    public sealed class GamerProfile : IDisposable
    {
        public Texture2D GamerPicture => null;
        public void Dispose() { }
    }

    public sealed class GamerPresence
    {
        public string PresenceMode { get; set; }
    }

    public sealed class GameDefaults
    {
        public bool AutoAim { get; set; }
    }

    public sealed class GamerPrivileges
    {
        public bool AllowOnlineSessions { get; set; } = true;
        public GamerPrivilegeSetting AllowCommunication { get; set; } = GamerPrivilegeSetting.Everyone;
        public GamerPrivilegeSetting AllowProfileViewing { get; set; } = GamerPrivilegeSetting.Everyone;
        public GamerPrivilegeSetting AllowUserCreatedContent { get; set; } = GamerPrivilegeSetting.Everyone;
    }

    public enum GamerPrivilegeSetting
    {
        Blocked,
        Everyone,
        FriendsOnly
    }

    public sealed class FriendGamer : Gamer
    {
        public bool FriendRequestReceivedFrom { get; set; }
        public bool FriendRequestSentTo { get; set; }
    }

    public sealed class FriendCollection : GamerCollection<FriendGamer>, IDisposable
    {
        public void Dispose() { }
    }

    public sealed class SignedInEventArgs : EventArgs
    {
        public SignedInEventArgs(SignedInGamer gamer) { Gamer = gamer; }
        public SignedInGamer Gamer { get; }
    }

    public sealed class SignedOutEventArgs : EventArgs
    {
        public SignedOutEventArgs(SignedInGamer gamer) { Gamer = gamer; }
        public SignedInGamer Gamer { get; }
    }

    public enum MessageBoxIcon
    {
        None,
        Error,
        Warning,
        Info
    }

    public enum NotificationPosition
    {
        TopLeft,
        TopCenter,
        TopRight,
        BottomLeft,
        BottomCenter,
        BottomRight
    }

    public static class Guide
    {
        public static bool IsVisible => false;
        public static bool IsNetworkCableUnplugged => false;
        public static bool IsTrialMode { get; set; }
        public static bool SimulateTrialMode { get; set; }
        public static bool IsScreenSaverEnabled { get; set; }
        public static NotificationPosition NotificationPosition { get; set; }

        public static IAsyncResult BeginShowMessageBox(PlayerIndex player, string title, string text,
            IEnumerable<string> buttons, int focusButton, MessageBoxIcon icon, AsyncCallback callback, object state)
        {
            var result = new ShimAsyncResult(state);
            callback?.Invoke(result);
            return result;
        }

        public static int? EndShowMessageBox(IAsyncResult result) => 0;

        public static void ShowSignIn(int paneCount, bool onlineOnly) { }

        public static IAsyncResult BeginShowKeyboardInput(PlayerIndex player, string title, string description,
            string defaultText, AsyncCallback callback, object state)
        {
            var result = new ShimAsyncResult(state);
            callback?.Invoke(result);
            return result;
        }

        public static string EndShowKeyboardInput(IAsyncResult result) => string.Empty;

        public static IAsyncResult BeginShowStorageDeviceSelector(AsyncCallback callback, object state)
        {
            var result = new ShimAsyncResult(state);
            callback?.Invoke(result);
            return result;
        }

        public static IAsyncResult BeginShowStorageDeviceSelector(int sizeInBytes, int directoryCount, AsyncCallback callback, object state)
        {
            var result = new ShimAsyncResult(state);
            callback?.Invoke(result);
            return result;
        }

        public static IAsyncResult BeginShowStorageDeviceSelector(PlayerIndex player, AsyncCallback callback, object state)
        {
            var result = new ShimAsyncResult(state);
            callback?.Invoke(result);
            return result;
        }

        public static IAsyncResult BeginShowStorageDeviceSelector(PlayerIndex player, int sizeInBytes, int directoryCount, AsyncCallback callback, object state)
        {
            var result = new ShimAsyncResult(state);
            callback?.Invoke(result);
            return result;
        }

        public static Storage.StorageDevice EndShowStorageDeviceSelector(IAsyncResult result) => new Storage.StorageDevice();

        public static void ShowMessages(PlayerIndex player) { }
        public static void ShowFriends(PlayerIndex player) { }
        public static void ShowPlayers(PlayerIndex player) { }
        public static void ShowFriendRequest(PlayerIndex player, Gamer gamer) { }
        public static void ShowPlayerReview(PlayerIndex player, Gamer gamer) { }
        public static void ShowGamerCard(PlayerIndex player, Gamer gamer) { }
        public static void ShowComposeMessage(PlayerIndex player, string text, IEnumerable<Gamer> recipients) { }
        public static void DelayNotifications(TimeSpan delay) { }
        public static void ShowAchievements(PlayerIndex player) { }
        public static void ShowGameInvite(PlayerIndex player, IEnumerable<Gamer> recipients) { }
        public static void ShowMarketplace(PlayerIndex player, ulong offerId) { }
    }

    public class GamerServicesComponent : GameComponent
    {
        public GamerServicesComponent(Game game) : base(game) { }
    }
}
