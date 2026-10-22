using System;

namespace ProtocolTests
{
    /// <summary>
    /// Marks a public static, parameterless method as a protocol test. It is a
    /// hand-rolled attribute on purpose: the app has no test framework and the
    /// protocol core has to compile inside it, so the runner stays dependency-free.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute
    {
    }

    /// <summary>Thrown by <see cref="Assert"/>; the runner prints its message.</summary>
    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The four assertions the protocol tests need. Everything goes through here
    /// rather than `Debug.Assert`, because the runner must report the two values,
    /// not just fail: the value a vector disagrees on is the whole diagnostic.
    /// </summary>
    public static class Assert
    {
        public static void AreEqual(object expected, object actual)
        {
            if (!Equals(expected, actual))
            {
                throw new AssertionException(
                    "expected <" + Describe(expected) + "> but was <" + Describe(actual) + ">");
            }
        }

        /// <summary>
        /// The same, with the reason the caller was checking it, as `IsTrue` has had all
        /// along. It is here because the reason is the whole diagnostic when the two values
        /// disagree about something the numbers do not explain - which code means the
        /// account is gone, say - and a test that has to drop its sentence to fit the
        /// signature is a test that says less the moment it fails.
        /// </summary>
        public static void AreEqual(object expected, object actual, string because)
        {
            if (!Equals(expected, actual))
            {
                throw new AssertionException(
                    "expected <" + Describe(expected) + "> but was <" + Describe(actual) + ">: " + because);
            }
        }

        public static void IsTrue(bool condition)
        {
            if (!condition) throw new AssertionException("expected true but was false");
        }

        /// <summary>
        /// The same, with the reason the caller was checking it. A failure that says what
        /// it expected to be true is the difference between a fix and a search.
        /// </summary>
        public static void IsTrue(bool condition, string because)
        {
            if (!condition) throw new AssertionException("expected true but was false: " + because);
        }

        public static void IsFalse(bool condition)
        {
            if (condition) throw new AssertionException("expected false but was true");
        }

        public static void IsFalse(bool condition, string because)
        {
            if (condition) throw new AssertionException("expected false but was true: " + because);
        }

        public static void ByteArraysEqual(byte[] expected, byte[] actual)
        {
            ByteArraysEqual(expected, actual, null);
        }

        /// <summary>
        /// The same, with the reason: two arrays of bytes rarely say which of them was the
        /// one being asked about, and a canvas of pixels says nothing at all.
        /// </summary>
        public static void ByteArraysEqual(byte[] expected, byte[] actual, string because)
        {
            if (because != null) because = ": " + because; else because = "";

            if (expected == null || actual == null)
            {
                throw new AssertionException("expected two byte arrays, got "
                    + Describe(expected) + " and " + Describe(actual));
            }

            if (expected.Length != actual.Length)
            {
                throw new AssertionException("byte array length " + expected.Length
                    + " != " + actual.Length + because + "\n  expected " + Hex(expected)
                    + "\n  actual   " + Hex(actual));
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    throw new AssertionException("byte " + i + " differs" + because + "\n  expected "
                        + Hex(expected) + "\n  actual   " + Hex(actual));
                }
            }
        }

        public static void Throws<T>(Action action) where T : Exception
        {
            Throws<T>(action, null);
        }

        /// <summary>The same, with the reason the caller expected it to be refused.</summary>
        public static void Throws<T>(Action action, string because) where T : Exception
        {
            if (because != null) because = ": " + because; else because = "";

            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            catch (Exception other)
            {
                throw new AssertionException("expected " + typeof(T).Name + " but got "
                    + other.GetType().Name + ": " + other.Message + because);
            }

            throw new AssertionException("expected " + typeof(T).Name + " but nothing was thrown" + because);
        }

        public static string Hex(byte[] bytes)
        {
            if (bytes == null) return "(null)";

            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("x2"));
            return text.ToString();
        }

        private static string Describe(object value)
        {
            if (value == null) return "null";

            var bytes = value as byte[];
            if (bytes != null) return Hex(bytes);

            return value.ToString();
        }
    }
}
