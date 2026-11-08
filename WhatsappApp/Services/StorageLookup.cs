using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>Looks up a file without throwing when it is absent.</summary>
    public static class StorageLookup
    {
        public static async Task<IStorageItem> TryGetItemAsync(this StorageFolder folder, string name)
        {
            if (folder == null) throw new ArgumentNullException("folder");
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A file name is required", "name");

            IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();
            for (int i = 0; i < files.Count; i++)
            {
                if (string.Equals(files[i].Name, name, StringComparison.OrdinalIgnoreCase)) return files[i];
            }

            return null;
        }
    }
}
