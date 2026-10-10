namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>One half of a `HandshakeMessage.Hello`: client or server.</summary>
    public sealed class Hello
    {
        /// <summary>The sender's ephemeral public key (field 1).</summary>
        public byte[] Ephemeral;

        /// <summary>The sender's static public key, the server's only (field 2).</summary>
        public byte[] Static;

        /// <summary>The encrypted blob that follows the keys (field 3).</summary>
        public byte[] Payload;
    }

    /// <summary>
    /// The protobuf messages the handshake is carried in.
    ///
    /// The first exchange between the client and WhatsApp is **not** a binary node:
    /// it is this message, and a driver that waits for a node waits forever. The plan
    /// assumed the other way round until the live probe showed a 4-byte first message
    /// that is not a node - see the plan's "What execution changed about this plan".
    ///
    /// Field numbers are from the protocol's own definitions (`clientHello = 2` is
    /// confirmed by the generated bindings; the rest follow the same file) and the live
    /// probe is what checks them: a wrong number here means the server's reply does not
    /// decode and the handshake stops at the first step.
    /// </summary>
    public static class HandshakeMessage
    {
        public const int ClientHelloField = 2;
        public const int ServerHelloField = 3;
        public const int ClientFinishField = 4;

        private const int EphemeralField = 1;
        private const int StaticField = 2;
        private const int PayloadField = 3;
        private const int FinishStaticField = 1;
        private const int FinishPayloadField = 2;

        /// <summary>`HandshakeMessage{clientHello{ephemeral}}`.</summary>
        public static byte[] EncodeClientHello(byte[] ephemeralPublicKey)
        {
            var hello = new ProtoWriter()
                .Bytes(EphemeralField, ephemeralPublicKey)
                .ToArray();

            return new ProtoWriter().Bytes(ClientHelloField, hello).ToArray();
        }

        /// <summary>`HandshakeMessage{clientFinish{static, payload}}`.</summary>
        public static byte[] EncodeClientFinish(byte[] staticKey, byte[] payload)
        {
            var finish = new ProtoWriter()
                .Bytes(FinishStaticField, staticKey)
                .Bytes(FinishPayloadField, payload)
                .ToArray();

            return new ProtoWriter().Bytes(ClientFinishField, finish).ToArray();
        }

        /// <summary>
        /// Reads the `serverHello` out of the server's message, or throws when the
        /// message is not one. A message with neither half is refused rather than
        /// returned empty: the caller cannot do anything with a handshake that has no
        /// ephemeral in it.
        /// </summary>
        public static Hello DecodeServerHello(byte[] message)
        {
            var reader = new ProtoReader(message);
            Hello serverHello = null;
            string seen = null;

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == ServerHelloField && field.WireType == 2)
                {
                    serverHello = DecodeHello(reader.ReadBytes());
                    seen = "serverHello";
                    continue;
                }

                if (field.WireType == 2 && (field.Number == ClientHelloField || field.Number == ClientFinishField))
                {
                    reader.ReadBytes();
                    seen = field.Number == ClientHelloField ? "clientHello" : "clientFinish";
                    continue;
                }

                reader.SkipField(field.WireType);
            }

            if (serverHello == null)
            {
                throw new ProtocolException("the server's message carries no serverHello"
                    + (seen == null ? string.Empty : " (it carries a " + seen + ")"));
            }

            if (serverHello.Ephemeral == null)
            {
                throw new ProtocolException("the serverHello carries no ephemeral key");
            }

            return serverHello;
        }

        private static Hello DecodeHello(byte[] bytes)
        {
            var reader = new ProtoReader(bytes);
            var hello = new Hello();

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 2 && field.Number == EphemeralField) hello.Ephemeral = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == StaticField) hello.Static = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == PayloadField) hello.Payload = reader.ReadBytes();
                else reader.SkipField(field.WireType);
            }

            return hello;
        }
    }

    /// <summary>The server's certificate chain, as `CertChain` defines it.</summary>
    public sealed class CertChain
    {
        public sealed class Certificate
        {
            public byte[] Details;
            public byte[] Signature;
        }

        /// <summary>
        /// `NoiseCertificate.Details`. The field numbers come from decoding the server's
        /// own certificate rather than from a .proto: the leaf's field 3 is the static
        /// key the handshake decrypted, and the leaf's issuer serial equals the
        /// intermediate's serial, which is what pins 1, 2 and 3. A wrong number here is
        /// invisible until something verifies a signature against the wrong bytes.
        /// </summary>
        public sealed class Details
        {
            /// <summary>This certificate's serial (field 1).</summary>
            public ulong Serial;

            /// <summary>Who signed it: zero for the intermediate, the intermediate's
            /// serial for the leaf (field 2).</summary>
            public ulong IssuerSerial;

            /// <summary>The public key this certificate vouches for (field 3).</summary>
            public byte[] Key;

            /// <summary>The start of the validity window, in seconds (field 4). The
            /// order is not a guess: the live certificate's field 4 is earlier than its
            /// field 5, and today falls between them.</summary>
            public ulong NotBefore;

            /// <summary>The end of the validity window, in seconds (field 5).</summary>
            public ulong NotAfter;
        }

        public Certificate Leaf;
        public Certificate Intermediate;

        /// <summary>
        /// Decodes the chain. The details of the intermediate are decoded as well
        /// because the reference compares its issuer serial against a compiled
        /// constant; the signatures are **not** verified - see the plan's ruling on
        /// deferring XEdDSA verification, which is why this is not yet a secure
        /// handshake.
        /// </summary>
        public static CertChain Decode(byte[] bytes)
        {
            var chain = new CertChain();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 2 && field.Number == 1) chain.Leaf = DecodeCertificate(reader.ReadBytes());
                else if (field.WireType == 2 && field.Number == 2) chain.Intermediate = DecodeCertificate(reader.ReadBytes());
                else reader.SkipField(field.WireType);
            }

            if (chain.Leaf == null) throw new ProtocolException("the certificate chain carries no leaf");
            return chain;
        }

        /// <summary>Reads a certificate's details.</summary>
        public static Details DecodeDetails(byte[] bytes)
        {
            var details = new Details();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 0 && field.Number == 1) details.Serial = reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == 2) details.IssuerSerial = reader.ReadVarint();
                else if (field.WireType == 2 && field.Number == 3) details.Key = reader.ReadBytes();
                else if (field.WireType == 0 && field.Number == 4) details.NotBefore = reader.ReadVarint();
                else if (field.WireType == 0 && field.Number == 5) details.NotAfter = reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            return details;
        }

        private static Certificate DecodeCertificate(byte[] bytes)
        {
            var certificate = new Certificate();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.WireType == 2 && field.Number == 1) certificate.Details = reader.ReadBytes();
                else if (field.WireType == 2 && field.Number == 2) certificate.Signature = reader.ReadBytes();
                else reader.SkipField(field.WireType);
            }

            return certificate;
        }
    }
}
