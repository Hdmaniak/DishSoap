// Local compatibility shim for XNA 3.0 Microsoft.Xna.Framework.Storage.
//
// On Android there is no XNA StorageDevice/StorageContainer. This shim maps
// the whole surface onto a plain app-private directory. Single-player save
// data (settings.sav) will land there via the game's existing FileStreams.
using System;
using System.IO;

namespace Microsoft.Xna.Framework.Storage
{
    public sealed class StorageDevice
    {
        public bool IsConnected => true;

        public StorageContainer OpenContainer(string displayName, bool allowTransferBetweenPlayers, out bool isTransferredFromOtherPlayer)
        {
            isTransferredFromOtherPlayer = false;
            return new StorageContainer();
        }
    }

    public sealed class StorageContainer : IDisposable
    {
        // XNA semantics: TitleLocation is the directory where the title's
        // installed resources live. The port's raw content is extracted by
        // AndroidContentBootstrap into the app-private "content" dir, so point
        // TitleLocation there. This is what GameResourceManager uses to build
        // the file-based ResourceManager path (<TitleLocation>/Resources).
        // Falls back to the save-data dir if the bootstrap hasn't run.
        public static string TitleLocation
        {
            get
            {
                string contentRoot = Dishwasher.AndroidContentBootstrap.ContentRoot;
                return string.IsNullOrEmpty(contentRoot) ? ResolveBasePath() : contentRoot;
            }
        }

        public string Path { get; }

        public bool IsDisposed { get; private set; }

        public StorageContainer()
        {
            Path = ResolveBasePath();
            try { Directory.CreateDirectory(Path); } catch { }
        }

        private static string ResolveBasePath()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                if (string.IsNullOrEmpty(appData)) appData = System.IO.Path.GetTempPath();
                return System.IO.Path.Combine(appData, "TheDishwasher");
            }
            catch
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TheDishwasher");
            }
        }

        public void Dispose() { IsDisposed = true; }
    }
}
