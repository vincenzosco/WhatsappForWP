using System;
using System.ComponentModel;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    /// <summary>
    /// One row of the status list: everything a contact has posted today, as one line.
    ///
    /// Why it exists: the feed is one row per *update*, and a person who posts three
    /// clips filled the list with three rows of the same face, each opening a screen
    /// that showed only that clip. WhatsApp's own list is one row per person carrying
    /// "3 updates", and tapping it plays the three in the order they were posted. The
    /// rows still come from the one collection `DataService.Statuses` keeps - this is
    /// only the shape the list draws, and the viewer still gets a `ChatMessage` to open.
    ///
    /// What is on the row is the newest update: its author name, its picture and its
    /// time. What tells the two kinds of row apart is `Detail` (the caption of a single
    /// update, or the count of several) and `IsStatusViewed` (the accent ring, which
    /// stays while *any* of the day's updates is unwatched, exactly as the official
    /// list does).
    /// </summary>
    public sealed class StatusGroup : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The author's JID. Set once, before the row exists, so it never changes.</summary>
        public string Author { get; set; }

        /// <summary>
        /// The newest update of this author, and the row the tap hands to the viewer:
        /// the feed is newest first, so the first one met is the newest.
        /// </summary>
        public ChatMessage Latest
        {
            get { return _latest; }
            set { _latest = value; Raise("Latest"); Raise("Initials"); }
        }

        /// <summary>"3 updates", or the words of a person's only update.</summary>
        public string Detail
        {
            get { return _detail; }
            set { _detail = value; Raise("Detail"); }
        }

        /// <summary>True when every update of the day has been watched: it drops the ring.</summary>
        public bool IsStatusViewed
        {
            get { return _isStatusViewed; }
            set { _isStatusViewed = value; Raise("IsStatusViewed"); }
        }

        /// <summary>The letter the circle draws when the author has no picture.</summary>
        public string Initials
        {
            get { return Latest == null ? "" : Latest.Initials; }
        }

        /// <summary>How many updates this author has today. Not bound: `Detail` says it.</summary>
        public int Count;

        /// <summary>How many of them nobody has watched yet. Not bound either.</summary>
        public int Unseen;

        private ChatMessage _latest;
        private string _detail;
        private bool _isStatusViewed;

        /// <summary>
        /// How many updates the day holds, said in the one language the app is in.
        /// A single update says its own words instead of counting to one.
        /// </summary>
        public void Finish()
        {
            if (Count > 1)
            {
                Detail = string.Format(Loc.Get("StatusPage_Updates", "{0} updates"), Count);
            }
            else
            {
                Detail = Latest == null ? "" : Latest.Text;
            }

            IsStatusViewed = Unseen == 0;
        }

        /// <summary>
        /// Takes the values of the freshly built row of the same author.
        ///
        /// Why not replace the object in the collection: the list is bound to it, and
        /// swapping an item re-creates the row - during a sync burst that is a row
        /// rebuilt once per arriving update. Copying the values changes only what the
        /// bindings draw.
        /// </summary>
        public void Adopt(StatusGroup other)
        {
            if (other == null) return;

            Latest = other.Latest;
            Detail = other.Detail;
            IsStatusViewed = other.IsStatusViewed;
        }

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
