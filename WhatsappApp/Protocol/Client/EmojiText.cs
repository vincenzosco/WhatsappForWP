using System;
using System.Collections.Generic;
using System.Text;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The text as this phone can actually draw it: the emoji its font has no glyph for
    /// are replaced by the nearest older one, and what has no older cousin is dropped
    /// instead of being drawn as an empty box.
    ///
    /// Windows Phone 8.1 shipped with the emoji of Unicode 6.1 and
    /// has never had a font update, so every emoji added since - the mechanical arm, the
    /// ringed planet, the light blue heart everybody sends from a newer phone - is a
    /// codepoint with no glyph. The platform draws a box, and the message reads as a
    /// broken one. `Benny🩵`, `robotica🦾🦾🦿` and `🪐✨🪐` are three rows of the account
    /// this was written against.
    ///
    /// The table is the whole feature: one line per emoji, the older one it becomes. Every
    /// replacement has to be an emoji of Unicode 6.0 - anything newer would be a box in
    /// place of a box - and the tests hold that rule. To add one, add a line.
    ///
    /// It never touches what is sent: `ChatMessage.Text` stays the message, and only the
    /// property the screen draws asks this.
    /// </summary>
    public static class EmojiText
    {
        /// <summary>Joiner: what makes `👨‍👩‍👧` one glyph, and no old font has one.</summary>
        private const char Joiner = '\u200D';

        /// <summary>The emoji presentation selector: a font that has the glyph ignores it.</summary>
        private const char EmojiPresentation = '\uFE0F';

        /// <summary>
        /// The emoticons a person types when the keyboard gives them no face, and the one
        /// each of them means.
        ///
        /// Why the app converts them and not the phone: Windows Phone 8.1's own keyboard
        /// turns `:)` into a face only for a `Chat` field and only while its setting is on,
        /// and a keyboard that is not Microsoft's never does. The request
        /// was "si possano mandare emoji o:)", so the composer does it on the way out and
        /// what the other side receives is the face itself, not a colon.
        ///
        /// Every face on the right is Unicode 6.0: turning a typed `:)` into a box would be
        /// the same bug one screen further on. The longest emoticons come first, so `<3`
        /// never eats the tail of `</3` - and that order is ours, not the caller's.
        /// </summary>
        private static readonly string[][] Faces = LongestFirst(new string[][]
        {
            new string[] { ":'(", "\U0001F622" },   // a tear -> crying face
            new string[] { "</3", "\U0001F494" },   // broken heart
            new string[] { ":-)", "\U0001F60A" },
            new string[] { ":-(", "\U0001F61E" },
            new string[] { ";-)", "\U0001F609" },
            new string[] { ";-(", "\U0001F622" },
            new string[] { ":-D", "\U0001F604" },
            new string[] { ":-P", "\U0001F61B" },
            new string[] { ":-p", "\U0001F61B" },
            new string[] { ":-O", "\U0001F62E" },
            new string[] { ":-|", "\U0001F610" },
            new string[] { ":)",  "\U0001F60A" },   // smiling, not the slightly smiling of 7.0
            new string[] { ":(",  "\U0001F61E" },
            new string[] { ";)",  "\U0001F609" },
            new string[] { ";(",  "\U0001F622" },
            new string[] { ":D",  "\U0001F604" },
            new string[] { ":P",  "\U0001F61B" },
            new string[] { ":p",  "\U0001F61B" },
            new string[] { ":O",  "\U0001F62E" },
            new string[] { ":o",  "\U0001F62E" },
            new string[] { ":|",  "\U0001F610" },
            new string[] { "<3",  "\u2764\uFE0F" }  // red heart
        });

        /// <summary>
        /// What each emoji this phone lacks becomes. Keyed on the code point, and both
        /// sides of the table are Unicode 6.0 or older.
        /// </summary>
        private static readonly Dictionary<int, string> Older = new Dictionary<int, string>
        {
            // The faces from 8.0 on.
            { 0x1F910, "\U0001F611" },   // zipper mouth -> neutral
            { 0x1F912, "\U0001F637" },   // thermometer -> mask
            { 0x1F913, "\U0001F60E" },   // nerd -> sunglasses
            { 0x1F914, "\U0001F615" },   // thinking -> confused
            { 0x1F915, "\U0001F616" },   // bandaged head -> confounded
            { 0x1F916, "\U0001F47E" },   // robot -> alien monster
            { 0x1F917, "\U0001F60A" },   // hug -> smiling
            { 0x1F918, "\u270C\uFE0F" }, // sign of the horns -> victory hand
            { 0x1F920, "\U0001F60E" },   // cowboy -> sunglasses
            { 0x1F922, "\U0001F616" },   // nauseated -> confounded
            { 0x1F923, "\U0001F602" },   // rofl -> tears of joy
            { 0x1F924, "\U0001F60B" },   // drooling -> savouring
            { 0x1F925, "\U0001F610" },   // lying -> neutral
            { 0x1F926, "\U0001F629" },   // facepalm -> weary
            { 0x1F927, "\U0001F637" },   // sneezing -> mask
            { 0x1F928, "\U0001F615" },   // raised eyebrow -> confused
            { 0x1F929, "\U0001F60D" },   // star-struck -> heart eyes
            { 0x1F92A, "\U0001F61C" },   // zany -> winking tongue
            { 0x1F92B, "\U0001F636" },   // shushing -> no mouth
            { 0x1F92C, "\U0001F620" },   // cursing -> angry
            { 0x1F92D, "\U0001F60F" },   // hand over mouth -> smirk
            { 0x1F92E, "\U0001F631" },   // vomiting -> screaming
            { 0x1F92F, "\U0001F635" },   // exploding head -> dizzy
            { 0x1F970, "\U0001F60D" },   // hearts -> heart eyes
            { 0x1F971, "\U0001F634" },   // yawning -> sleeping
            { 0x1F972, "\U0001F622" },   // tear -> crying
            { 0x1F973, "\U0001F603" },   // party -> smile
            { 0x1F974, "\U0001F635" },   // woozy -> dizzy
            { 0x1F975, "\U0001F613" },   // hot -> downcast sweat
            { 0x1F976, "\U0001F628" },   // cold -> fearful
            { 0x1F979, "\U0001F622" },   // holding tears -> crying
            { 0x1F97A, "\U0001F622" },   // pleading -> crying
            { 0x1FAE0, "\U0001F605" },   // melting face -> sweat smile
            { 0x1FAE1, "\U0001F603" },   // saluting -> smile
            { 0x1FAE5, "\U0001F610" },   // dotted line -> neutral
            { 0x1FAE6, "\U0001F62C" },   // biting lip -> grimacing
            { 0x1FAE8, "\U0001F635" },   // shaking -> dizzy

            // Hearts, hands and people.
            { 0x1F90C, "\U0001F44C" },   // pinched fingers -> ok hand
            { 0x1F90D, "\u2764\uFE0F" }, // white heart -> red heart
            { 0x1F91D, "\U0001F450" },   // handshake -> open hands
            { 0x1F9E1, "\u2764\uFE0F" }, // orange heart -> red heart
            { 0x1F9E4, "\u270B" },       // gloves -> raised hand
            { 0x1F5A4, "\u2764\uFE0F" }, // black heart -> red heart
            { 0x1F90E, "\U0001F49C" },   // brown heart -> purple heart
            { 0x1FAC0, "\u2764\uFE0F" }, // anatomical heart -> red heart
            { 0x1FAC2, "\U0001F450" },   // hugging -> open hands
            { 0x1FA75, "\U0001F499" },   // light blue heart -> blue heart
            { 0x1FA76, "\U0001F499" },   // grey heart -> blue heart
            { 0x1FA77, "\U0001F497" },   // pink heart -> growing heart
            { 0x1FAF0, "\U0001F44C" },   // crossed fingers -> ok hand
            { 0x1FAF1, "\U0001F450" },   // hands, 14.0 and 15.0
            { 0x1FAF2, "\U0001F450" },
            { 0x1FAF3, "\U0001F450" },
            { 0x1FAF4, "\U0001F450" },
            { 0x1FAF5, "\U0001F450" },
            { 0x1FAF6, "\U0001F450" },
            { 0x1FAF7, "\U0001F450" },
            { 0x1FAF8, "\U0001F450" },
            { 0x1F9B8, "\U0001F4AA" },   // superhero -> flexed arm
            { 0x1F9B9, "\U0001F608" },   // supervillain -> smiling devil
            { 0x1F9BE, "\U0001F4AA" },   // mechanical arm -> flexed arm
            { 0x1F9BF, "\U0001F463" },   // mechanical leg -> footprints
            { 0x1F9B5, "\U0001F463" },   // leg -> footprints
            { 0x1F9B6, "\U0001F463" },   // foot -> footprints
            { 0x1F9D1, "\U0001F464" },   // adult -> bust in silhouette
            { 0x1F9D2, "\U0001F466" },   // child -> boy
            { 0x1F9D3, "\U0001F474" },   // older person -> older man
            { 0x1F9D4, "\U0001F468" },   // bearded -> man
            { 0x1F9D5, "\U0001F469" },   // headscarf -> woman
            { 0x1F9D6, "\U0001F6C0" },   // sauna -> bath
            { 0x1F9D7, "\U0001F3C3" },   // climbing -> runner
            { 0x1F9D8, "\U0001F60C" },   // lotus -> relieved
            { 0x1F9D9, "\U0001F3A9" },   // mage -> top hat
            { 0x1F9DA, "\U0001F47C" },   // fairy -> baby angel
            { 0x1F9DB, "\U0001F47B" },   // vampire -> ghost
            { 0x1F9DC, "\U0001F478" },   // merperson -> princess
            { 0x1F9DD, "\U0001F478" },   // elf -> princess
            { 0x1F9DE, "\U0001F47B" },   // genie -> ghost
            { 0x1F9DF, "\U0001F47B" },   // zombie -> ghost
            { 0x1FAC5, "\U0001F451" },   // person with crown -> crown

            // Food and drink.
            { 0x1F32D, "\U0001F354" },   // hot dog -> hamburger
            { 0x1F32E, "\U0001F354" },   // taco -> hamburger
            { 0x1F32F, "\U0001F354" },   // burrito -> hamburger
            { 0x1F37E, "\U0001F377" },   // popping cork -> wine glass
            { 0x1F37F, "\U0001F35F" },   // popcorn -> fries
            { 0x1F9C0, "\U0001F355" },   // cheese -> pizza
            { 0x1F9C1, "\U0001F370" },   // cupcake -> shortcake
            { 0x1F9C2, "\U0001F374" },   // salt -> fork and knife
            { 0x1F9C4, "\U0001F345" },   // garlic -> tomato
            { 0x1F9C5, "\U0001F345" },   // onion -> tomato
            { 0x1F9C6, "\U0001F354" },   // falafel -> hamburger
            { 0x1F9C7, "\U0001F35E" },   // waffle -> bread
            { 0x1F9C8, "\U0001F35E" },   // butter -> bread
            { 0x1F9C9, "\u2615" },       // mate -> coffee
            { 0x1F9CA, "\u2744\uFE0F" }, // ice -> snowflake
            { 0x1F9CB, "\u2615" },       // bubble tea -> coffee
            { 0x1FAD0, "\U0001F347" },   // blueberries -> grapes
            { 0x1FAD1, "\U0001F345" },   // bell pepper -> tomato
            { 0x1FAD2, "\U0001F347" },   // olive -> grapes
            { 0x1FAD3, "\U0001F35E" },   // flatbread -> bread
            { 0x1FAD4, "\U0001F354" },   // tamale -> hamburger
            { 0x1FAD5, "\U0001F372" },   // fondue -> stew
            { 0x1FAD6, "\u2615" },       // teapot -> coffee
            { 0x1FAD7, "\U0001F4A7" },   // pouring liquid -> droplet
            { 0x1FAD8, "\U0001F331" },   // beans -> seedling
            { 0x1FAD9, "\U0001F36F" },   // jar -> honey pot

            // Animals, plants and weather.
            { 0x1FAB0, "\U0001F41B" },   // fly -> bug
            { 0x1FAB1, "\U0001F41B" },   // worm -> bug
            { 0x1FAB2, "\U0001F41B" },   // beetle -> bug
            { 0x1FAB3, "\U0001F41B" },   // cockroach -> bug
            { 0x1FAB4, "\U0001F331" },   // potted plant -> seedling
            { 0x1FAB5, "\U0001F333" },   // wood -> tree
            { 0x1FAB6, "\U0001F343" },   // feather -> leaf in wind
            { 0x1FAB7, "\U0001F33A" },   // lotus -> hibiscus
            { 0x1FAB8, "\U0001F30A" },   // coral -> wave
            { 0x1FAB9, "\U0001F426" },   // empty nest -> bird
            { 0x1FABA, "\U0001F426" },   // nest with eggs -> bird
            { 0x1FABB, "\U0001F337" },   // hyacinth -> tulip
            { 0x1FABC, "\U0001F419" },   // jellyfish -> octopus
            { 0x1FABD, "\U0001F426" },   // wing -> bird
            { 0x1FABE, "\U0001F333" },   // leafless tree -> tree
            { 0x1FABF, "\U0001F426" },   // goose -> bird
            { 0x1FACE, "\U0001F434" },   // moose -> horse
            { 0x1FACF, "\U0001F434" },   // donkey -> horse

            // Body, health and the household objects of 9.0 to 15.0.
            { 0x1F9B4, "\U0001F480" },   // bone -> skull
            { 0x1F9B7, "\U0001F601" },   // tooth -> grinning
            { 0x1F9BA, "\U0001F455" },   // safety vest -> t-shirt
            { 0x1F9BB, "\U0001F442" },   // ear with aid -> ear
            { 0x1F9BC, "\u267F" },       // motorized wheelchair
            { 0x1F9BD, "\u267F" },       // manual wheelchair
            { 0x1FA7A, "\U0001F48A" },   // stethoscope -> pill
            { 0x1FA7B, "\U0001F480" },   // x-ray -> skull
            { 0x1FA7C, "\u267F" },       // crutch -> wheelchair sign
            { 0x1F9E0, "\U0001F4AD" },   // brain -> thought balloon
            { 0x1F9E2, "\U0001F3A9" },   // billed cap -> top hat
            { 0x1F9E3, "\U0001F455" },   // scarf -> t-shirt
            { 0x1F9E5, "\U0001F455" },   // coat -> t-shirt
            { 0x1F9E6, "\U0001F456" },   // socks -> jeans
            { 0x1F9E7, "\U0001F381" },   // red envelope -> wrapped present
            { 0x1F9E8, "\U0001F386" },   // firecracker -> fireworks
            { 0x1F9E9, "\U0001F3B2" },   // puzzle piece -> game die
            { 0x1F9EA, "\U0001F52C" },   // test tube -> microscope
            { 0x1F9EB, "\U0001F52C" },   // petri dish -> microscope
            { 0x1F9EC, "\U0001F52C" },   // dna -> microscope
            { 0x1F9ED, "\U0001F30D" },   // compass -> globe
            { 0x1F9EE, "\U0001F4F1" },   // abacus -> mobile phone
            { 0x1F9EF, "\U0001F525" },   // fire extinguisher -> fire
            { 0x1F9F0, "\U0001F527" },   // toolbox -> wrench
            { 0x1F9F1, "\U0001F3E0" },   // brick -> house
            { 0x1F9F2, "\U0001F4CE" },   // magnet -> paperclip
            { 0x1F9F3, "\U0001F4BC" },   // luggage -> briefcase
            { 0x1F9F4, "\U0001F37C" },   // lotion -> baby bottle
            { 0x1F9F5, "\U0001F4CF" },   // thread -> straight ruler
            { 0x1F9F6, "\U0001F380" },   // yarn -> ribbon
            { 0x1F9F7, "\U0001F4CE" },   // safety pin -> paperclip
            { 0x1F9F8, "\U0001F43B" },   // teddy bear -> bear
            { 0x1F9FA, "\U0001F45C" },   // basket -> handbag
            { 0x1F9FB, "\U0001F4C4" },   // roll of paper -> page
            { 0x1F9FC, "\U0001F4A7" },   // soap -> droplet
            { 0x1F9FD, "\U0001F4A7" },   // sponge -> droplet
            { 0x1F9FE, "\U0001F4C4" },   // receipt -> page
            { 0x1F9FF, "\U0001F441" },   // nazar amulet -> eye
            { 0x1FA92, "\u2702\uFE0F" }, // razor -> scissors
            { 0x1FA93, "\U0001F528" },   // axe -> hammer
            { 0x1FA94, "\U0001F4A1" },   // diya lamp -> bulb
            { 0x1FA95, "\U0001F3B8" },   // banjo -> guitar
            { 0x1FA96, "\U0001F452" },   // military helmet -> woman's hat
            { 0x1FA97, "\U0001F3B9" },   // accordion -> piano
            { 0x1FA98, "\U0001F3B5" },   // long drum -> music note
            { 0x1FA99, "\U0001F4B0" },   // coin -> money bag
            { 0x1FA9A, "\U0001F528" },   // carpentry saw -> hammer
            { 0x1FA9B, "\U0001F527" },   // screwdriver -> wrench
            { 0x1FA9D, "\U0001F4CE" },   // hook -> paperclip
            { 0x1FA9F, "\U0001F3E0" },   // window -> house
            { 0x1FAA1, "\U0001F4CF" },   // sewing needle -> ruler
            { 0x1FAA2, "\U0001F380" },   // knot -> ribbon
            { 0x1FAA4, "\U0001F42D" },   // mouse trap -> mouse face
            { 0x1FAA5, "\U0001F4A7" },   // toothbrush -> droplet
            { 0x1FAA6, "\u26B0" },       // headstone
            { 0x1FAA7, "\U0001F4C4" },   // placard -> page
            { 0x1FAA8, "\u26F0" },       // rock -> mountain
            { 0x1FAA9, "\U0001F389" },   // mirror ball -> party popper
            { 0x1FAAA, "\U0001F4C4" },   // id card -> page
            { 0x1FAAB, "\U0001F50B" },   // low battery -> battery
            { 0x1FAAC, "\u270B" },       // hamsa -> raised hand

            // Sky, travel and the rest of the 7.0 to 13.0 objects.
            { 0x1FA90, "\U0001F30D" },   // ringed planet -> globe
            { 0x1F6F0, "\U0001F680" },   // satellite -> rocket
            { 0x1F6F3, "\U0001F6A2" },   // passenger ship -> ship
            { 0x1F6F4, "\U0001F6B2" },   // kick scooter -> bicycle
            { 0x1F6F5, "\U0001F697" },   // motor scooter -> car
            { 0x1F6F6, "\u26F5" },       // canoe -> sailboat
            { 0x1F6F7, "\u26F7" },       // sled -> skier
            { 0x1F6F8, "\U0001F680" },   // flying saucer -> rocket
            { 0x1F6F9, "\U0001F6B2" },   // skateboard -> bicycle
            { 0x1F6FA, "\U0001F697" },   // auto rickshaw -> car
            { 0x1F6FB, "\U0001F69A" },   // pickup truck -> truck
            { 0x1F6FC, "\u26F8" },       // roller skate -> ice skate
            { 0x1F6D0, "\U0001F3E0" },   // place of worship -> house
            { 0x1F6D1, "\u26D4" },       // stop sign
            { 0x1F6D2, "\U0001F3EA" },   // shopping trolley -> convenience store
            { 0x1F6D5, "\U0001F3E0" },   // hindu temple -> house
            { 0x1F6D6, "\U0001F3E0" },   // hut -> house
            { 0x1F6D7, "\U0001F3E2" },   // elevator -> office building
            { 0x1F6DC, "\U0001F4F6" },   // wireless -> signal bars
            { 0x1F6DD, "\U0001F3A2" },   // playground slide -> roller coaster
            { 0x1F6DE, "\u2699" },       // wheel -> gear
            { 0x1F6DF, "\u26D1" },       // ring buoy -> helmet with cross
            { 0x1F54B, "\U0001F3E0" },   // kaaba -> house
            { 0x1F54C, "\U0001F3E0" },   // mosque -> house
            { 0x1F54D, "\U0001F3E0" },   // synagogue -> house
            { 0x1F4FF, "\U0001F52E" },   // prayer beads -> crystal ball
            { 0x1F57A, "\U0001F483" },   // man dancing -> dancer
            { 0x1F3F8, "\U0001F3BE" },   // badminton -> tennis
            { 0x1F643, "\U0001F609" },   // upside-down -> winking
            { 0x1F644, "\U0001F612" },   // rolling eyes -> unamused

            // The coloured circles and squares of 12.0.
            { 0x1F7E0, "\U0001F534" },
            { 0x1F7E1, "\u26AA" },
            { 0x1F7E2, "\U0001F49A" },
            { 0x1F7E3, "\U0001F49C" },
            { 0x1F7E4, "\u26AB" },
            { 0x1F7E5, "\U0001F534" },
            { 0x1F7E6, "\U0001F535" },
            { 0x1F7E7, "\U0001F534" },
            { 0x1F7E8, "\u26AB" },
            { 0x1F7E9, "\U0001F49A" },
            { 0x1F7EA, "\U0001F49C" },
            { 0x1F7EB, "\u2B1C" },
            { 0x1F7F0, "\U0001F517" }    // heavy equals sign -> link
        };

        /// <summary>
        /// The text, with every emoji this phone cannot draw turned into one it can.
        ///
        /// Text with no emoji comes back as the same string and no allocation: this runs
        /// for every row of a list of 171.
        /// </summary>
        public static string Compatible(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!NeedsWork(text)) return text;

            var built = new StringBuilder(text.Length);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // A joiner asks one glyph out of several, and no font older than 9.0 has
                // one: taking it out leaves `👨👩👧` as three emoji this phone draws.
                if (c == Joiner) continue;

                // The presentation selector says "draw this as an emoji". A font that has
                // the glyph ignores it, and without it some emoji draw as text, so it stays.
                if (c == EmojiPresentation) { built.Append(c); continue; }

                if (!char.IsSurrogate(c))
                {
                    built.Append(c);
                    continue;
                }

                if (!char.IsHighSurrogate(c) || i + 1 >= text.Length
                    || !char.IsLowSurrogate(text[i + 1]))
                {
                    // Half a character is not text: it would reach the renderer as the
                    // replacement mark, and the message as a broken one.
                    continue;
                }

                int codePoint = char.ConvertToUtf32(c, text[i + 1]);
                i++;

                // A tag sequence spells a subdivision flag, and the tags are not glyphs.
                if (codePoint >= 0xE0020 && codePoint <= 0xE007F) continue;

                // A skin tone is a modifier this font has none of: the emoji it modifies
                // is what should show.
                if (codePoint >= 0x1F3FB && codePoint <= 0x1F3FF) continue;

                string older;
                if (Older.TryGetValue(codePoint, out older)) { built.Append(older); continue; }

                // No older cousin, and not a code point this phone has: what is left would
                // be drawn as an empty box, which is the thing this exists to avoid.
                if (NewerThanThisPhone(codePoint)) continue;

                built.Append(c);
                built.Append(text[i]);
            }

            return built.ToString();
        }

        /// <summary>
        /// The text with the emoticons typed into it turned into the faces they mean.
        ///
        /// Text with no colon, semicolon or angle bracket comes back as the same string and
        /// no allocation, and text with one that is not an emoticon is copied once.
        /// </summary>
        public static string Emoticons(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!HoldsAnEmoticon(text)) return text;

            var built = new StringBuilder(text.Length + 8);

            for (int i = 0; i < text.Length; i++)
            {
                string[] face = FaceAt(text, i);

                if (face == null)
                {
                    built.Append(text[i]);
                    continue;
                }

                built.Append(face[1]);
                i += face[0].Length - 1;
            }

            return built.ToString();
        }

        /// <summary>
        /// The row of the table - the emoticon and the face - for an emoticon that starts
        /// at `at`, or null.
        ///
        /// The one guard is what follows: an emoticon ends where a word resumes, so `:Domani`
        /// stays a typo and `ciao:)` becomes a smile. Nothing is asked of what comes before,
        /// because the way people actually type is `ci vediamo:)` without a space.
        /// </summary>
        private static string[] FaceAt(string text, int at)
        {
            for (int i = 0; i < Faces.Length; i++)
            {
                string candidate = Faces[i][0];
                if (at + candidate.Length > text.Length) continue;
                if (string.CompareOrdinal(text, at, candidate, 0, candidate.Length) != 0) continue;
                if (!EndsAWord(text, at + candidate.Length)) continue;

                return Faces[i];
            }

            return null;
        }

        /// <summary>Whether the text resumes as a word right after this position.</summary>
        private static bool EndsAWord(string text, int at)
        {
            if (at >= text.Length) return true;

            char c = text[at];
            return !char.IsLetter(c) && !char.IsDigit(c);
        }

        /// <summary>Whether there is anything the conversion could possibly match.</summary>
        private static bool HoldsAnEmoticon(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ':' || c == ';' || c == '<') return true;
            }

            return false;
        }

        /// <summary>The table ordered so that the longest emoticon is tried first.</summary>
        private static string[][] LongestFirst(string[][] faces)
        {
            var ordered = new List<string[]>(faces);
            ordered.Sort(delegate(string[] a, string[] b)
            {
                return b[0].Length.CompareTo(a[0].Length);
            });

            return ordered.ToArray();
        }

        /// <summary>
        /// The table, for the test that every replacement is an emoji this phone draws:
        /// `Compatible` applied to it has to answer itself, because a replacement that
        /// needed replacing would be a box in place of a box.
        /// </summary>
        public static IList<KeyValuePair<int, string>> Entries()
        {
            var entries = new List<KeyValuePair<int, string>>();

            foreach (KeyValuePair<int, string> entry in Older)
            {
                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// The emoticons, for the tests: the typed text and the face it becomes, in the
        /// order they are tried.
        /// </summary>
        public static IList<KeyValuePair<string, string>> EmoticonEntries()
        {
            var entries = new List<KeyValuePair<string, string>>();

            for (int i = 0; i < Faces.Length; i++)
            {
                entries.Add(new KeyValuePair<string, string>(Faces[i][0], Faces[i][1]));
            }

            return entries;
        }

        /// <summary>
        /// Whether the text holds anything the loop above would change: a surrogate pair,
        /// a joiner, or the selector. ASCII never does, so the common message costs one
        /// scan and no string.
        /// </summary>
        private static bool NeedsWork(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsSurrogate(c) || c == Joiner || c == EmojiPresentation) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a code point belongs to a run of emoji added after the font this phone
        /// shipped with, so that it cannot be drawn at all.
        ///
        /// These are the blocks and the runs created whole after Unicode 6.1: the
        /// supplemental symbols (everything from 8.0 on lives there), the extended
        /// pictographs (12.0 on), the skin tones, the coloured shapes, and the handful of
        /// stragglers in the older blocks. An emoji of 7.0 that is not in one of them is
        /// left alone: being wrong about one costs a glyph that was there.
        /// </summary>
        private static bool NewerThanThisPhone(int codePoint)
        {
            if (codePoint >= 0x1F900 && codePoint <= 0x1F9FF) return true;
            if (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF) return true;
            if (codePoint >= 0x1F7E0 && codePoint <= 0x1F7EB) return true;
            if (codePoint >= 0x1F6D0 && codePoint <= 0x1F6DF) return true;
            if (codePoint >= 0x1F6F0 && codePoint <= 0x1F6FF) return true;
            if (codePoint >= 0x1F3F8 && codePoint <= 0x1F3FF) return true;
            if (codePoint >= 0x1F32D && codePoint <= 0x1F32F) return true;
            if (codePoint >= 0x1F37E && codePoint <= 0x1F37F) return true;
            if (codePoint >= 0x1F549 && codePoint <= 0x1F54F) return true;
            if (codePoint >= 0x1F643 && codePoint <= 0x1F644) return true;
            if (codePoint == 0x1F4FF) return true;
            if (codePoint == 0x1F57A) return true;
            if (codePoint == 0x1F5A4) return true;
            if (codePoint == 0x1F7F0) return true;

            return false;
        }
    }
}
