namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// `ClientPayload`: the first thing a client says after the handshake, and the only
    /// place a companion asks to be paired.
    ///
    /// Every field number here is from the reference's `WAWebProtobufsWa6.proto`
    /// (`username` 1, `passive` 3, `userAgent` 5, `webInfo` 6, `pushName` 7,
    /// `connectType` 12, `connectReason` 13, `device` 18, `devicePairingData` 19,
    /// `pull` 33), and the nested messages' numbers from the same file. They are not
    /// guesses: a wrong one is a payload the server reads as something else - and
    /// because it is protobuf, usually as something that parses and is then refused
    /// with no explanation. `node tools/test-protocol.js --connect --pair` is what
    /// settles them against the server.
    ///
    /// Two payloads come out of here. `Registration` is the companion asking to be
    /// paired: it carries `devicePairingData` and neither `username` nor `pull`.
    /// `Login` is the same device coming back with an account it already has.
    /// </summary>
    public static class ClientPayload
    {
        // ClientPayload's own fields.
        public const int UsernameField = 1;
        public const int PassiveField = 3;
        public const int UserAgentField = 5;
        public const int WebInfoField = 6;
        public const int PushNameField = 7;
        public const int ConnectTypeField = 12;
        public const int ConnectReasonField = 13;
        public const int DeviceField = 18;
        public const int DevicePairingDataField = 19;
        public const int PullField = 33;

        // UserAgent's fields.
        public const int UserAgentPlatformField = 1;
        public const int UserAgentAppVersionField = 2;
        public const int UserAgentMccField = 3;
        public const int UserAgentMncField = 4;
        public const int UserAgentOsVersionField = 5;
        public const int UserAgentManufacturerField = 6;
        public const int UserAgentDeviceField = 7;
        public const int UserAgentOsBuildNumberField = 8;
        public const int UserAgentReleaseChannelField = 10;
        public const int UserAgentLocaleLanguageField = 11;
        public const int UserAgentLocaleCountryField = 12;

        // WebInfo's fields.
        public const int WebInfoRefTokenField = 1;
        public const int WebInfoVersionField = 2;
        public const int WebInfoWebSubPlatformField = 4;

        // DevicePairingRegistrationData's fields.
        public const int PairingERegidField = 1;
        public const int PairingEKeyTypeField = 2;
        public const int PairingEIdentField = 3;
        public const int PairingESkeyIdField = 4;
        public const int PairingESkeyValField = 5;
        public const int PairingESkeySigField = 6;
        public const int PairingBuildHashField = 7;
        public const int PairingDevicePropsField = 8;

        // The enum values this client sends, spelled out because the payload is the
        // one place where a number that reads like a guess reaches WhatsApp.
        /// <summary>`UserAgent.Platform.WEB`.</summary>
        public const int PlatformWeb = 14;

        /// <summary>`UserAgent.ReleaseChannel.RELEASE`.</summary>
        public const int ReleaseChannelRelease = 0;

        /// <summary>`WebInfo.WebSubPlatform.WEB_BROWSER`.</summary>
        public const int WebSubPlatformWebBrowser = 0;

        /// <summary>`ClientPayload.ConnectType.WIFI_UNKNOWN`.</summary>
        public const int ConnectTypeWifiUnknown = 1;

        /// <summary>`ClientPayload.ConnectReason.USER_ACTIVATED`.</summary>
        public const int ConnectReasonUserActivated = 1;

        /// <summary>`ecc.DjbType`: a Curve25519 key, the only key type the protocol has.</summary>
        public const byte KeyTypeCurve25519 = 0x05;

        /// <summary>
        /// The version this client announces in its `UserAgent`.
        ///
        /// **It is not this app's version, and it cannot be.** The server refuses a
        /// companion whose announced version is older than the minimum it supports, and
        /// the refusal is a bare `&lt;failure reason="405"&gt;` that names nothing - the
        /// first live pairing run ended exactly there. What the server accepts is a
        /// current WhatsApp Web version, which is what the reference sends too, so this
        /// is a number to **keep current**: when the server raises its floor, 405 comes
        /// back and these three move to whatever the reference ships.
        ///
        /// The app's own version lives in `DeviceProps`, where it is a label rather than
        /// a gate.
        /// </summary>
        public const uint WebVersionPrimary = 2;
        public const uint WebVersionSecondary = 3000;
        public const uint WebVersionTertiary = 1049558099;

        /// <summary>The version as the dot-separated string the build hash covers.</summary>
        public static string WebVersionText
        {
            get
            {
                return WebVersionPrimary.ToString() + "." + WebVersionSecondary.ToString()
                    + "." + WebVersionTertiary.ToString();
            }
        }

        /// <summary>The version this app announces as its own, in `DeviceProps`.</summary>
        public const uint AppVersionPrimary = 1;
        public const uint AppVersionSecondary = 0;
        public const uint AppVersionTertiary = 0;

        /// <summary>
        /// The pairing payload: the device's identity, its signed pre-key and the
        /// `DeviceProps` blob, plus the switches that tell the server a companion is
        /// asking to be linked (`passive` and `pull` are false here, and the absence of
        /// `username` is what says "I have no account yet").
        /// </summary>
        public static byte[] Registration(
            uint registrationId,
            byte[] identityPublicKey,
            uint signedPreKeyId,
            byte[] signedPreKeyPublicKey,
            byte[] signedPreKeySignature,
            byte[] deviceProps)
        {
            if (identityPublicKey == null || identityPublicKey.Length != 32)
            {
                throw new ProtocolException("the pairing payload needs a 32-byte identity public key");
            }

            if (signedPreKeyPublicKey == null || signedPreKeyPublicKey.Length != 32)
            {
                throw new ProtocolException("the pairing payload needs a 32-byte signed pre-key");
            }

            if (signedPreKeySignature == null || signedPreKeySignature.Length != 64)
            {
                throw new ProtocolException("the pairing payload needs a 64-byte signed pre-key signature");
            }

            if (deviceProps == null || deviceProps.Length == 0)
            {
                throw new ProtocolException("the pairing payload needs the device props");
            }

            byte[] pairingData = new ProtoWriter()
                .Bytes(PairingERegidField, BigEndian(registrationId, 4))
                .Bytes(PairingEKeyTypeField, new byte[] { KeyTypeCurve25519 })
                .Bytes(PairingEIdentField, identityPublicKey)
                // The reference writes the key id as three bytes: the fourth of a
                // 32-bit id is the top byte, and the server reads three.
                .Bytes(PairingESkeyIdField, BigEndian(signedPreKeyId, 3))
                .Bytes(PairingESkeyValField, signedPreKeyPublicKey)
                .Bytes(PairingESkeySigField, signedPreKeySignature)
                .Bytes(PairingDevicePropsField, deviceProps)
                .ToArray();

            return new ProtoWriter()
                .Varint(PassiveField, 0)
                .Bytes(UserAgentField, UserAgent())
                .Bytes(WebInfoField, WebInfo())
                .Varint(ConnectTypeField, ConnectTypeWifiUnknown)
                .Varint(ConnectReasonField, ConnectReasonUserActivated)
                .Bytes(DevicePairingDataField, pairingData)
                .Varint(PullField, 0)
                .ToArray();
        }

        /// <summary>
        /// The payload a device that already has an account sends: it names the account
        /// and the device, and asks for the backlog (`passive` and `pull` are true).
        /// </summary>
        public static byte[] Login(ulong user, uint device)
        {
            if (user == 0) throw new ProtocolException("a login payload needs the account's user number");

            return new ProtoWriter()
                .Varint(UsernameField, user)
                .Varint(PassiveField, 1)
                .Bytes(UserAgentField, UserAgent())
                .Bytes(WebInfoField, WebInfo())
                .Varint(ConnectTypeField, ConnectTypeWifiUnknown)
                .Varint(ConnectReasonField, ConnectReasonUserActivated)
                .Varint(DeviceField, device)
                .Varint(PullField, 1)
                .ToArray();
        }

        /// <summary>
        /// The client's `UserAgent`. The strings are labels rather than capabilities, so
        /// they say what this client is - a Windows Phone app - while `platform` stays
        /// `WEB`: that is the value the reference's own UWP pairing sends, and it is the
        /// one a server that cross-checks the platform word against `DeviceProps`
        /// accepts.
        /// </summary>
        public static byte[] UserAgent()
        {
            return new ProtoWriter()
                .Varint(UserAgentPlatformField, PlatformWeb)
                .Bytes(UserAgentAppVersionField, Version(WebVersionPrimary, WebVersionSecondary, WebVersionTertiary))
                .Text(UserAgentMccField, "000")
                .Text(UserAgentMncField, "000")
                .Text(UserAgentOsVersionField, "8.1")
                .Text(UserAgentManufacturerField, "Microsoft")
                .Text(UserAgentDeviceField, "Phone")
                .Text(UserAgentOsBuildNumberField, "8.1")
                .Varint(UserAgentReleaseChannelField, ReleaseChannelRelease)
                .Text(UserAgentLocaleLanguageField, "en")
                .Text(UserAgentLocaleCountryField, "US")
                .ToArray();
        }

        /// <summary>
        /// The web client's `WebInfo`. `webSubPlatform` is `WEB_BROWSER` and there is no
        /// `refToken`: this client was not handed a token by a web page, and the
        /// reference's UWP pairing sends the same zero here.
        /// </summary>
        public static byte[] WebInfo()
        {
            return new ProtoWriter()
                .Varint(WebInfoWebSubPlatformField, WebSubPlatformWebBrowser)
                .ToArray();
        }

        private static byte[] Version(uint primary, uint secondary, uint tertiary)
        {
            return new ProtoWriter()
                .Varint(1, primary)
                .Varint(2, secondary)
                .Varint(3, tertiary)
                .ToArray();
        }

        /// <summary>A number in `count` bytes, most significant first: what the payload's byte fields are.</summary>
        private static byte[] BigEndian(uint value, int count)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                bytes[count - 1 - i] = (byte)((value >> (8 * i)) & 0xff);
            }

            return bytes;
        }
    }
}
