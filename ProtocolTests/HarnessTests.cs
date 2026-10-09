namespace ProtocolTests
{
    /// <summary>
    /// The harness's own test. It is here so that a broken runner reports a failure
    /// instead of reporting nothing: a runner that silently discovers no tests would
    /// otherwise look exactly like a suite that passed.
    /// </summary>
    public static class HarnessTests
    {
        [Test]
        public static void The_runner_reports_a_failure_with_both_values()
        {
            Assert.Throws<AssertionException>(delegate { Assert.AreEqual(1, 2); });
        }

        [Test]
        public static void An_assertion_names_the_value_it_got()
        {
            string message = null;

            try
            {
                Assert.AreEqual("left", "right");
            }
            catch (AssertionException error)
            {
                message = error.Message;
            }

            Assert.IsTrue(message != null);
            Assert.IsTrue(message.IndexOf("left", System.StringComparison.Ordinal) >= 0);
            Assert.IsTrue(message.IndexOf("right", System.StringComparison.Ordinal) >= 0);
        }
    }
}
