// AndroidContentSource.cs -- PORT (public build): read content:// URIs returned
// by the Storage Access Framework.  Never assumes a filesystem path.
//
//   * ACTION_OPEN_DOCUMENT_TREE -> SafTreeContentSource walks the tree with
//     DocumentsContract and maps normalized relative paths to document URIs.
//   * ACTION_OPEN_DOCUMENT (a .zip or raw package) -> copied to app cache and
//     handed to ContentSourceFactory (magic sniffing).
#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Database;
using Android.Provider;
using Uri = Android.Net.Uri;

namespace Dishwasher.Import
{
    public sealed class SafTreeContentSource : IGameContentSource
    {
        private struct Node { public Uri Uri; public long Size; }

        private readonly ContentResolver _resolver;
        private readonly Uri _treeUri;
        private readonly Dictionary<string, Node> _map =
            new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);

        public SafTreeContentSource(Context context, Uri treeUri)
        {
            _resolver = context.ContentResolver;
            _treeUri = treeUri;
            string rootId = DocumentsContract.GetTreeDocumentId(treeUri);
            Walk(rootId, "");
        }

        private void Walk(string docId, string prefix)
        {
            Uri children;
            try { children = DocumentsContract.BuildChildDocumentsUriUsingTree(_treeUri, docId); }
            catch { return; }

            ICursor cursor = null;
            try
            {
                cursor = _resolver.Query(children, new[]
                {
                    DocumentsContract.Document.ColumnDocumentId,
                    DocumentsContract.Document.ColumnDisplayName,
                    DocumentsContract.Document.ColumnMimeType,
                    DocumentsContract.Document.ColumnSize,
                }, null, null, null);
                if (cursor == null) return;

                int idCol = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDocumentId);
                int nameCol = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
                int mimeCol = cursor.GetColumnIndex(DocumentsContract.Document.ColumnMimeType);
                int sizeCol = cursor.GetColumnIndex(DocumentsContract.Document.ColumnSize);

                while (cursor.MoveToNext())
                {
                    string id = cursor.GetString(idCol);
                    string name = cursor.GetString(nameCol);
                    string mime = cursor.GetString(mimeCol);
                    long size = (sizeCol >= 0 && !cursor.IsNull(sizeCol)) ? cursor.GetLong(sizeCol) : -1L;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;

                    string rel = prefix.Length == 0 ? name : prefix + "/" + name;
                    if (mime == DocumentsContract.Document.MimeTypeDir)
                    {
                        Walk(id, rel);
                    }
                    else
                    {
                        try
                        {
                            _map[rel] = new Node
                            {
                                Uri = DocumentsContract.BuildDocumentUriUsingTree(_treeUri, id),
                                Size = size,
                            };
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("[import] SAF walk error under '" + prefix + "': " + ex.Message);
            }
            finally
            {
                cursor?.Close();
            }
        }

        public bool TryOpen(string path, out Stream stream, out long length)
        {
            stream = null;
            length = 0;
            if (!_map.TryGetValue(path, out Node node)) return false;
            try
            {
                stream = _resolver.OpenInputStream(node.Uri);
                if (stream == null) return false;
                length = node.Size;
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("[import] cannot open '" + path + "': " + ex.Message);
                stream = null;
                return false;
            }
        }

        public void Dispose() { }
    }

    public static class AndroidContentSourceFactory
    {
        public static IGameContentSource FromTree(Context ctx, Uri treeUri)
        {
            return new SafTreeContentSource(ctx, treeUri);
        }

        public static IGameContentSource FromDocument(Context ctx, Uri uri, string tempDir)
        {
            string local = CopyToTemp(ctx, uri, tempDir);
            return ContentSourceFactory.FromLocalFile(local, tempDir);
        }

        /// <summary>Copy a content:// stream to app cache (SAF streams are not
        /// seekable / not path-addressable).</summary>
        public static string CopyToTemp(Context ctx, Uri uri, string tempDir)
        {
            Directory.CreateDirectory(tempDir);
            string dest = Path.Combine(tempDir, "picked-import.bin");
            using Stream input = ctx.ContentResolver.OpenInputStream(uri);
            if (input == null) throw new IOException("could not open the selected file");
            using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output, 1 << 20);
            return dest;
        }
    }
}
#endif
