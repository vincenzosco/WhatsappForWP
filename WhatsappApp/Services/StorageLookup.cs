using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The lookup WinRT gives Windows 10 but Windows Phone 8.1 does not: the item
    /// with this name, or null when there is none.
    ///
    /// Why it exists: the first run has no file, and asking for a file that is not
    /// there throws, which the debugger reports as a first-chance exception on every
    /// normal start. Answering null here keeps the missing file out of the exception
    /// path. Any other fault still throws, as it did before.
    /// </summary>
    public static class StorageLookup
    {
        public static async Task<IStorageItem> TryGetItemAsync(this StorageFolder folder, string name)
        {
            try
            {
                return await folder.GetFileAsync(name);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }
}
