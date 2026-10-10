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
            if (expected == null || actual == null)
            {
                throw new AssertionException("expected two byte arrays, got "
                    + Describe(expected) + " and " + Describe(actual));
            }

            if (expected.Length != actual.Length)
            {
                throw new AssertionException("byte array length " + expected.Length
                    + " != " + actual.Length + "\n  expected " + Hex(expected)
                    + "\n  actual   " + Hex(actual));
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    throw new AssertionException("byte " + i + " differs\n  expected "
                        + Hex(expected) + "\n  actual   " + Hex(actual));
                }
            }
        }

        public static void Throws<T>(Action action) where T : Exception
        {
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
                    + other.GetType().Name + ": " + other.Message);
            }

            throw new AssertionException("expected " + typeof(T).Name + " but nothing was thrown");
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
